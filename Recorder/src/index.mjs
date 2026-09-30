// ClanGuard meeting-recording sidecar.
//
// Why this exists as a separate Node process: Discord.Net (what the main C#
// bot uses) only implements the xsalsa20_poly1305 voice encryption mode that
// Discord discontinued on 2024-11-18, and its receive path is unmaintained.
// @discordjs/voice supports the current encryption modes and has a working
// receiver, so the *recording* concern lives here. Everything else
// (scheduling, announcing, transcription, minutes, retention) stays in the
// C# bot, which drives this process over a localhost HTTP API.
//
// ── Audio strategy: ONE continuous track per speaker ─────────────────────────
// Earlier this sidecar wrote one Ogg per *utterance* (Discord only transmits
// while a user speaks). That gave perfect attribution but produced thousands
// of tiny files for a long meeting — and the transcriber then had to invoke
// Whisper once per file, which took 75+ minutes for a single monthly meeting.
//
// Now we keep ONE OggOpusStream open per speaker for the whole meeting. When a
// speaker talks we append their Opus frames; when they go quiet we leave the
// muxer open, and before their *next* utterance we inject exactly enough Opus
// silence to re-align the track to real meeting wall-clock time. The result is
// ~N continuous, time-aligned tracks (N = number of people who spoke) instead
// of thousands of clips, so the transcriber runs Whisper ~N times. Per-segment
// timestamps from each track are merged downstream into one chronological,
// speaker-labelled transcript.
//
// ── Stop policy: stop when the VC empties, not on a fixed clock ──────────────
// The bot used to tell us a fixed stop time derived from the calendar, so a
// meeting that ran long got cut off. Now the recorder watches VC occupancy
// (real members, bots excluded) and auto-stops once the channel has been below
// the occupancy threshold continuously for VC_EMPTY_GRACE_MS — i.e. when the
// meeting actually ends. A hard cap (HARD_CAP_GRACE_MS past the expected stop,
// bounded by ABSOLUTE_MAX_MS) guarantees a forgotten-open VC can't record forever.
//
// ── Surviving a restart mid-meeting ──────────────────────────────────────────
// Every CHECKPOINT_MS the recorder writes manifest.partial.json next to the
// tracks. If the process is killed before it can write the final manifest.json,
// the bot promotes the checkpoint so the audio captured so far is still
// transcribed instead of being reported as "no audio".
//
// ── Idempotent stop / finalized-recording memory ─────────────────────────────
// When we auto-stop (VC empty or hard cap), we finalize and remember the
// result in `finalized` keyed by meetingRecordingId. If the bot's scheduled
// /stop then arrives late, we return the SAME audioDir instead of "nothing
// here" — this is the fix for the May-31 race where the recorder's own stop
// beat the bot's and the meeting was wrongly marked Failed despite full audio
// on disk.

import { Client, GatewayIntentBits, Events } from 'discord.js';
import {
  joinVoiceChannel,
  EndBehaviorType,
  entersState,
  VoiceConnectionStatus,
} from '@discordjs/voice';
import { OggOpusStream } from './ogg-opus.mjs';
import express from 'express';
import fs from 'node:fs';
import path from 'node:path';

// ── Config ───────────────────────────────────────────────────────────────
const TOKEN = process.env.RECORDER_BOT_TOKEN;
const SHARED_SECRET = process.env.RECORDER_SHARED_SECRET ?? '';
const PORT = Number(process.env.RECORDER_PORT ?? 8080);
const AUDIO_ROOT = process.env.AUDIO_ROOT ?? '/app/data/recordings';
// End an utterance (close the receiver subscription for it) after this many ms
// of silence from a speaker. The track stays open; only the per-utterance
// subscription ends, which is how we get clean per-utterance start/end times.
const SILENCE_MS = Number(process.env.RECORDER_SILENCE_MS ?? 800);
// Discord stops sending packets during short pauses inside an utterance. A gap
// between packets longer than this is filled with silence so the track keeps
// pace with real time; shorter gaps are treated as network jitter.
const INTRA_GAP_MS = Number(process.env.RECORDER_INTRA_GAP_MS ?? 200);
// Stop once the VC has been empty (no non-bot members) for this long.
const VC_EMPTY_GRACE_MS = Number(process.env.RECORDER_VC_EMPTY_GRACE_MS ?? 120_000);
// Treat "empty" as "at most this many non-bot members" (1 = just a straggler).
// 0 means truly empty. Default 0: stop only when everyone has left.
const VC_EMPTY_THRESHOLD = Number(process.env.RECORDER_VC_EMPTY_THRESHOLD ?? 0);
// Hard safety cap: never record longer than this past the bot's expected stop.
// The bot already sends a generous expected stop, so no extra grace by default.
const HARD_CAP_GRACE_MS = Number(process.env.RECORDER_HARD_CAP_GRACE_MS ?? 0);
// Absolute ceiling regardless of expected stop (defense against a bad/missing
// expectedStopUtc): never record a single meeting longer than this.
const ABSOLUTE_MAX_MS = Number(process.env.RECORDER_ABSOLUTE_MAX_MS ?? 3 * 60 * 60_000);
// How often to evaluate VC occupancy for the empty-stop check.
const OCCUPANCY_POLL_MS = Number(process.env.RECORDER_OCCUPANCY_POLL_MS ?? 15_000);
// How often to write manifest.partial.json while recording.
const CHECKPOINT_MS = Number(process.env.RECORDER_CHECKPOINT_MS ?? 30_000);
// How many finalized recordings to remember for late idempotent /stop calls.
const FINALIZED_MEMORY = Number(process.env.RECORDER_FINALIZED_MEMORY ?? 8);

if (!TOKEN) {
  console.error('FATAL: RECORDER_BOT_TOKEN is not set.');
  process.exit(1);
}
if (!SHARED_SECRET) {
  console.warn('WARNING: RECORDER_SHARED_SECRET is empty — the control API is unauthenticated.');
}

const log = (...a) => console.log(new Date().toISOString(), '[recorder]', ...a);

// ── State ────────────────────────────────────────────────────────────────
// The clan runs one meeting VC, so one active recording at a time is enough.
let current = null;
// { meetingRecordingId, promise } while a /record is still joining the VC, so a
// retried /record for the same meeting joins that attempt instead of racing it.
let starting = null;
// Set on SIGTERM/SIGINT so no new recording starts in a process that is exiting.
let shuttingDown = false;
// meetingRecordingId -> { audioDir, segmentCount, speakerCount, finalizedUtc, reason }
// so a late /stop after an auto-stop returns the real dir, not null.
// audioDir is null when nothing was captured.
const finalized = new Map();

const delay = (ms) => new Promise((r) => setTimeout(r, ms));

function rememberFinalized(id, info) {
  finalized.set(id, info);
  // Bound the memory: drop the oldest entries beyond FINALIZED_MEMORY.
  while (finalized.size > FINALIZED_MEMORY) {
    const oldest = finalized.keys().next().value;
    finalized.delete(oldest);
  }
}

// ── Discord client ──────────────────────────────────────────────────────────
const client = new Client({
  intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildVoiceStates],
});

client.once(Events.ClientReady, (c) => log(`Logged in as ${c.user.tag} (recorder).`));
client.on(Events.Error, (e) => log('discord client error:', e?.message ?? e));

// Resolve a speaker's display name (server nickname). Falls back to global
// username, then to the raw id. Uses a single REST member fetch which works
// without the privileged Server Members intent.
async function resolveSpeaker(guild, userId) {
  try {
    const member = await guild.members.fetch(userId);
    return { displayName: member.displayName, username: member.user.username };
  } catch {
    try {
      const user = await client.users.fetch(userId);
      return { displayName: user.username, username: user.username };
    } catch {
      return { displayName: userId, username: userId };
    }
  }
}

// Count non-bot members currently in the recorded VC.
function countHumansInVc(state) {
  try {
    const guild = client.guilds.cache.get(state.guildId);
    const channel = guild?.channels?.cache?.get(state.voiceChannelId);
    if (!channel || !channel.members) return null; // unknown — don't act on it
    let n = 0;
    for (const m of channel.members.values()) {
      if (!m.user?.bot) n++;
    }
    return n;
  } catch {
    return null;
  }
}

// ── Per-speaker continuous track ────────────────────────────────────────────
// Lazily create one OggOpusStream + file per speaker, kept open for the meeting.
function trackFor(state, userId, guild) {
  let tr = state.tracks.get(userId);
  if (tr) return tr;

  const file = `speaker_user${userId}.ogg`;
  const filePath = path.join(state.audioDir, file);
  const out = fs.createWriteStream(filePath);
  const ogg = new OggOpusStream({ channelCount: 2, sampleRate: 48000 });
  ogg.on('error', (e) => log(`track ${file} ogg error:`, e?.message ?? e));
  out.on('error', (e) => log(`track ${file} write error:`, e?.message ?? e));
  ogg.pipe(out);

  tr = {
    userId,
    file,
    filePath,
    ogg,
    out,
    // Real-meeting-time (ms since recording start) of the END of audio we've
    // written so far, so we know how much silence to insert before the next
    // utterance to stay aligned.
    writtenUntilMs: 0,
    firstUtteranceUtc: null,
    lastUtteranceEndUtc: null,
    segments: [], // {startUtc, endUtc, startOffsetMs} — for transcript timing
    open: null,   // the utterance currently being captured, if any
    closed: false,
  };
  state.tracks.set(userId, tr);

  // Resolve the speaker name once, lazily. Stop waits on these so the final
  // manifest doesn't label someone by their raw user id.
  if (!state.participants[userId]) {
    state.nameLookups.push(resolveSpeaker(guild, userId).then((s) => { state.participants[userId] = s; }));
  }
  return tr;
}

// Capture one utterance into the speaker's continuous track.
function captureUtterance(state, userId, guild) {
  if (state.stopping) return;
  if (state.activeSubs.has(userId)) return; // already subscribed for this speaker
  state.activeSubs.add(userId);

  const tr = trackFor(state, userId, guild);
  const startUtc = new Date();
  const startOffsetMs = startUtc.getTime() - state.startMs;

  // Align the track: insert silence for the gap since we last wrote audio.
  // Advance by what was actually written (whole 20ms frames), so rounding
  // doesn't accumulate as drift over many utterances.
  const gapMs = startOffsetMs - tr.writtenUntilMs;
  if (gapMs > 0) {
    try {
      tr.writtenUntilMs += tr.ogg.writeSilence(gapMs) * 20;
    } catch (e) {
      log('writeSilence error:', e?.message ?? e);
    }
  }

  const opusStream = state.receiver.subscribe(userId, {
    end: { behavior: EndBehaviorType.AfterSilence, duration: SILENCE_MS },
  });

  // The utterance in progress, visible to checkpoints and to stop (which closes
  // it out if the speaker is still talking, so that audio isn't dropped).
  const open = { startUtc, startOffsetMs, frames: 0, padMs: 0, finish: null };
  tr.open = open;
  const onData = (frame) => {
    if (tr.closed) return; // track already ended by stop; writing would error per frame
    try {
      const lagMs = (Date.now() - state.startMs) - (tr.writtenUntilMs + open.frames * 20 + open.padMs);
      if (lagMs > INTRA_GAP_MS) open.padMs += tr.ogg.writeSilence(lagMs) * 20;
      tr.ogg.write(frame);
      open.frames++;
    } catch (e) {
      log(`track write error for user ${userId}:`, e?.message ?? e);
    }
  };
  opusStream.on('data', onData);

  const done = new Promise((resolve) => {
    let finished = false;
    const finish = () => {
      if (finished) return;        // end + close can both fire — only count once
      finished = true;
      opusStream.off('data', onData);
      state.activeSubs.delete(userId);
      if (tr.open === open) tr.open = null;
      const endUtc = new Date();
      // Each Discord frame is 20ms; advance our written-time marker.
      tr.writtenUntilMs += open.frames * 20 + open.padMs;
      if (open.frames > 0) {
        tr.firstUtteranceUtc ??= startUtc.toISOString();
        tr.lastUtteranceEndUtc = endUtc.toISOString();
        tr.segments.push({
          startUtc: startUtc.toISOString(),
          endUtc: endUtc.toISOString(),
          startOffsetMs,
        });
      }
      resolve();
    };
    open.finish = finish;
    opusStream.once('end', finish);
    opusStream.once('close', finish);
    opusStream.once('error', (e) => {
      if (e && e.code !== 'ERR_STREAM_PREMATURE_CLOSE' && e.code !== 'ABORT_ERR') {
        log(`utterance stream error for user ${userId}:`, e.message);
      }
      finish();
    });
  });

  state.pending.add(done);
  done.finally(() => state.pending.delete(done));
}

// ── Start / stop ──────────────────────────────────────────────────────────
// Idempotent per meeting: a retried /record for the meeting that is already
// recording (or still joining) succeeds instead of returning 409. The check and
// the `starting` claim happen synchronously, so two overlapping calls can't
// both get past it and open two recordings.
async function startRecording(opts) {
  const id = opts.meetingRecordingId;
  if (shuttingDown) throw new Error('Recorder is shutting down.');
  if (current?.meetingRecordingId === id && !current.stopping) return;
  if (starting?.meetingRecordingId === id) return starting.promise;
  if (current) throw new Error(`Already recording meeting #${current.meetingRecordingId}.`);
  if (starting) throw new Error(`Already recording meeting #${starting.meetingRecordingId}.`);

  const attempt = { meetingRecordingId: id, promise: doStartRecording(opts) };
  starting = attempt;
  try {
    await attempt.promise;
  } finally {
    if (starting === attempt) starting = null;
  }
}

async function doStartRecording({ meetingRecordingId, guildId, voiceChannelId, meetingTitle, expectedStopUtc }) {
  if (!client.isReady()) throw new Error('Discord client not ready yet.');

  const guild = await client.guilds.fetch(String(guildId));
  await guild.channels.fetch(String(voiceChannelId)); // verify it exists / cache it

  const connection = joinVoiceChannel({
    channelId: String(voiceChannelId),
    guildId: String(guildId),
    adapterCreator: guild.voiceAdapterCreator,
    selfDeaf: false, // MUST be false — a deafened bot receives no audio
    selfMute: true,  // the recorder never speaks
  });

  connection.on('error', (err) => log(`[voice] connection error: ${err?.message ?? err}`));

  try {
    await entersState(connection, VoiceConnectionStatus.Ready, 30_000);
  } catch (e) {
    try { connection.destroy(); } catch {}
    throw new Error(`Voice connection never became Ready: ${e.message}`);
  }

  const startUtc = new Date();
  const audioDir = path.join(AUDIO_ROOT, `meeting_${meetingRecordingId}_${startUtc.getTime()}`);
  try {
    fs.mkdirSync(audioDir, { recursive: true });
  } catch (e) {
    try { connection.destroy(); } catch {} // don't sit in the VC with nowhere to write
    throw e;
  }

  // Compute the hard cap: the bot's expected stop plus HARD_CAP_GRACE_MS,
  // bounded by an absolute max.
  const expMs = Date.parse(expectedStopUtc);
  const hardCapMs = Math.min(
    (Number.isFinite(expMs) ? expMs : startUtc.getTime() + 60 * 60_000) + HARD_CAP_GRACE_MS,
    startUtc.getTime() + ABSOLUTE_MAX_MS,
  );

  const state = {
    meetingRecordingId,
    guildId: String(guildId),
    voiceChannelId: String(voiceChannelId),
    meetingTitle: meetingTitle ?? '',
    connection,
    receiver: connection.receiver,
    audioDir,
    tracks: new Map(),       // userId -> track
    participants: {},
    nameLookups: [],
    activeSubs: new Set(),
    pending: new Set(),
    startUtc: startUtc.toISOString(),
    startMs: startUtc.getTime(),
    hardCapMs,
    stopping: false,
    stopPromise: null,
    stopReason: null,
    occupancyTimer: null,
    checkpointTimer: null,
    emptySinceMs: null,     // when the VC first dropped to/below threshold
    sawAnyone: false,        // don't stop-on-empty until at least one human showed up
  };
  current = state;

  state.receiver.speaking.on('start', (userId) => {
    try {
      captureUtterance(state, userId, guild);
    } catch (e) {
      state.activeSubs.delete(userId);
      log(`captureUtterance error for user ${userId}:`, e?.message ?? e);
    }
  });

  // If we lose the connection and can't recover quickly, finalize what we have.
  connection.on(VoiceConnectionStatus.Disconnected, async () => {
    try {
      await Promise.race([
        entersState(connection, VoiceConnectionStatus.Signalling, 5_000),
        entersState(connection, VoiceConnectionStatus.Connecting, 5_000),
      ]);
    } catch {
      log('voice connection lost and did not recover — finalizing recording.');
      stopRecording(meetingRecordingId, 'disconnected').catch((e) =>
        log('finalize-on-disconnect error:', e.message));
    }
  });

  // ── Occupancy-driven auto-stop + hard cap ──────────────────────────────────
  state.occupancyTimer = setInterval(() => {
    if (state.stopping) return;
    const now = Date.now();

    // Hard safety cap.
    if (now >= state.hardCapMs) {
      log(`hard cap reached for meeting #${meetingRecordingId} — stopping.`);
      stopRecording(meetingRecordingId, 'hard-cap').catch((e) => log('hard-cap stop error:', e.message));
      return;
    }

    const humans = countHumansInVc(state);
    if (humans === null) return; // occupancy unknown this tick — skip
    if (humans > VC_EMPTY_THRESHOLD) {
      state.sawAnyone = true;
      state.emptySinceMs = null;
      return;
    }
    // At/below threshold. Only act on empties after we've seen real attendance,
    // so we don't stop in the lead-time window before anyone joins. If nobody
    // ever shows (everyone left while we were joining), don't sit there until
    // the hard cap.
    if (!state.sawAnyone) {
      if (now - state.startMs >= VC_EMPTY_GRACE_MS) {
        log(`nobody joined meeting #${meetingRecordingId} within ${Math.round(VC_EMPTY_GRACE_MS / 1000)}s — stopping.`);
        stopRecording(meetingRecordingId, 'no-show').catch((e) => log('no-show stop error:', e.message));
      }
      return;
    }
    if (state.emptySinceMs === null) {
      state.emptySinceMs = now;
      log(`VC for meeting #${meetingRecordingId} dropped to ${humans} member(s); ` +
          `will stop if it stays empty ${Math.round(VC_EMPTY_GRACE_MS / 1000)}s.`);
      return;
    }
    if (now - state.emptySinceMs >= VC_EMPTY_GRACE_MS) {
      log(`VC empty ${Math.round((now - state.emptySinceMs) / 1000)}s — stopping meeting #${meetingRecordingId}.`);
      stopRecording(meetingRecordingId, 'vc-empty').catch((e) => log('vc-empty stop error:', e.message));
    }
  }, OCCUPANCY_POLL_MS);

  state.checkpointTimer = setInterval(() => {
    if (state.stopping) return;
    try {
      writeJsonAtomic(path.join(state.audioDir, 'manifest.partial.json'), buildManifest(state, true));
    } catch (e) {
      log('checkpoint manifest write error:', e?.message ?? e);
    }
  }, CHECKPOINT_MS);

  log(`recording meeting #${meetingRecordingId} ("${state.meetingTitle}") in VC ${state.voiceChannelId} → ${audioDir} ` +
      `(hard cap ${new Date(hardCapMs).toISOString()})`);
}

function writeJsonAtomic(filePath, obj) {
  const tmp = `${filePath}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(obj, null, 2));
  fs.renameSync(tmp, filePath);
}

// Build the manifest from the live state. `partial` marks a checkpoint taken
// mid-recording: its tracks can hold audio past the last listed segment (an
// utterance still in progress), so the transcriber must not trust its timing.
function buildManifest(state, partial) {
  const tracks = [];
  const nowIso = new Date().toISOString();
  for (const tr of state.tracks.values()) {
    const segs = [...tr.segments];
    // Include an utterance still in progress at checkpoint time.
    if (tr.open?.frames > 0) {
      segs.push({ startUtc: tr.open.startUtc.toISOString(), endUtc: nowIso, startOffsetMs: tr.open.startOffsetMs });
    }
    if (segs.length === 0) continue;
    const speaker = state.participants[tr.userId] ?? { displayName: tr.userId };
    tracks.push({
      userId: tr.userId,
      displayName: speaker.displayName,
      username: speaker.username,
      file: tr.file,
      firstUtteranceUtc: tr.firstUtteranceUtc ?? segs[0].startUtc,
      lastUtteranceEndUtc: segs[segs.length - 1].endUtc,
      segments: segs,
    });
  }

  // Flat list of every utterance across all speakers, for chronological
  // transcript assembly downstream (mirrors the old per-segment manifest).
  const segments = [];
  for (const t of tracks) {
    for (const s of t.segments) {
      segments.push({
        userId: t.userId,
        displayName: t.displayName,
        file: t.file,          // the speaker's continuous track
        startUtc: s.startUtc,
        endUtc: s.endUtc,
        startOffsetMs: s.startOffsetMs, // ms from recording start to this utterance
      });
    }
  }
  segments.sort((a, b) => a.startUtc.localeCompare(b.startUtc));

  return {
    schemaVersion: 2,            // 2 = continuous per-speaker tracks
    partial,
    meetingRecordingId: state.meetingRecordingId,
    guildId: state.guildId,
    voiceChannelId: state.voiceChannelId,
    meetingTitle: state.meetingTitle,
    recordingStartedUtc: state.startUtc,
    recordingStoppedUtc: partial ? null : new Date().toISOString(),
    stopReason: partial ? null : state.stopReason,
    participants: state.participants,
    tracks,                      // one entry per speaker (continuous track)
    segments,                    // every utterance, chronological (for timing)
  };
}

async function stopRecording(meetingRecordingId, reason = 'requested') {
  // A /stop that lands while the same meeting is still joining waits for the
  // join, so it stops the recording rather than missing it.
  if (starting?.meetingRecordingId === meetingRecordingId) {
    await starting.promise.catch(() => {});
  }

  const state = current;
  if (!state || state.meetingRecordingId !== meetingRecordingId) {
    // Not the active recording. If we already finalized it, return that dir so
    // a late/duplicate /stop is idempotent (the May-31 race fix).
    if (finalized.has(meetingRecordingId)) {
      const info = finalized.get(meetingRecordingId);
      log(`stop for #${meetingRecordingId} (${reason}): already finalized → ${info.audioDir ?? '(nothing captured)'}`);
      return info.audioDir;
    }
    log(`stop requested for #${meetingRecordingId} (${reason}) but no matching active recording.`);
    return null;
  }
  if (state.stopping) return state.stopPromise;

  state.stopping = true;
  state.stopReason = reason;
  state.stopPromise = (async () => {
    clearInterval(state.occupancyTimer);
    clearInterval(state.checkpointTimer);
    state.receiver.speaking.removeAllListeners('start');

    // Let in-flight utterances flush (bounded, so a stuck stream can't hang us).
    await Promise.race([Promise.allSettled([...state.pending]), delay(10_000)]);
    // Anyone still talking after that (open mic, or stopped by cap/shutdown
    // mid-sentence): close their utterance now so it's kept, not discarded.
    for (const tr of state.tracks.values()) tr.open?.finish?.();

    // Close every speaker track (flush the Ogg EOS page and the file).
    const closes = [];
    for (const tr of state.tracks.values()) {
      tr.closed = true;
      closes.push(new Promise((resolve) => {
        tr.out.once('finish', resolve);
        tr.out.once('error', resolve);
        try { tr.ogg.end(); } catch { resolve(); }
      }));
    }
    await Promise.race([Promise.allSettled(closes), delay(10_000)]);
    await Promise.race([Promise.allSettled(state.nameLookups), delay(5_000)]);

    const manifest = buildManifest(state, false);
    const speakerCount = manifest.tracks.length;
    const segmentCount = manifest.segments.length;

    try { state.connection.destroy(); } catch {}

    let audioDir = state.audioDir;
    if (speakerCount > 0) {
      // Drop any track that never captured real audio (no utterances).
      for (const tr of state.tracks.values()) {
        if (!manifest.tracks.some((t) => t.userId === tr.userId)) {
          try { fs.unlinkSync(tr.filePath); } catch {}
        }
      }
      fs.writeFileSync(path.join(audioDir, 'manifest.json'), JSON.stringify(manifest, null, 2));
      try { fs.unlinkSync(path.join(audioDir, 'manifest.partial.json')); } catch {}
    } else {
      // Nothing was said: there is nothing to transcribe, so don't leave an
      // empty directory behind for the bot to adopt.
      try { fs.rmSync(audioDir, { recursive: true, force: true }); } catch {}
      audioDir = null;
    }

    current = null;
    rememberFinalized(meetingRecordingId, {
      audioDir, segmentCount, speakerCount, finalizedUtc: manifest.recordingStoppedUtc, reason,
    });
    log(`stopped meeting #${meetingRecordingId} (${reason}): ` +
        `${speakerCount} speaker track(s), ${segmentCount} utterance(s) → ${audioDir ?? '(nothing captured)'}`);
    return audioDir;
  })();

  return state.stopPromise;
}

// ── HTTP control API (localhost / compose-network only) ─────────────────────
const app = express();
app.use(express.json());

function auth(req, res, next) {
  if (SHARED_SECRET && req.get('x-recorder-secret') !== SHARED_SECRET) {
    return res.status(401).json({ error: 'unauthorized' });
  }
  next();
}

app.get('/health', (req, res) =>
  res.json({
    ok: true,
    ready: client.isReady() && !shuttingDown,
    recording: current?.meetingRecordingId ?? starting?.meetingRecordingId ?? null,
    // IDs the recorder has already auto-finalized (VC-empty or hard cap) and is
    // holding for an idempotent /stop. The bot polls this so it can advance a
    // recording promptly instead of waiting for its far-out safety backstop.
    finalized: [...finalized.keys()],
    // Why each one stopped, so the bot can resume a meeting that was cut off by
    // a disconnect or shutdown rather than one that genuinely ended.
    finalizedReasons: Object.fromEntries([...finalized].map(([id, info]) => [id, info.reason])),
  }),
);

app.post('/record', auth, async (req, res) => {
  try {
    await startRecording(req.body ?? {});
    res.json({ ok: true });
  } catch (e) {
    const status = /Already recording/.test(e.message) ? 409 : shuttingDown ? 503 : 500;
    log('record error:', e.message);
    res.status(status).json({ error: e.message });
  }
});

app.post('/stop', auth, async (req, res) => {
  try {
    const audioDir = await stopRecording(req.body?.meetingRecordingId, req.body?.reason ?? 'requested');
    res.json({ audioDir });
  } catch (e) {
    log('stop error:', e.message);
    res.status(500).json({ error: e.message });
  }
});

fs.mkdirSync(AUDIO_ROOT, { recursive: true });
app.listen(PORT, () => log(`control API listening on :${PORT}, audio root ${AUDIO_ROOT}`));

// Finalize cleanly on container shutdown.
for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, async () => {
    log(`${sig} received — shutting down.`);
    shuttingDown = true;
    if (starting) await starting.promise.catch(() => {});
    if (current) await stopRecording(current.meetingRecordingId, 'shutdown').catch(() => {});
    try { client.destroy(); } catch {}
    process.exit(0);
  });
}

client.login(TOKEN);
