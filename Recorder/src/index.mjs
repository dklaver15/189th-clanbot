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
// Audio strategy: we re-containerize the raw Opus packets straight into Ogg
// Opus files (no decode → no native opus dependency on the hot path), one
// file per *utterance* per speaker. Discord only transmits while a user is
// actually speaking, so each Ogg is a contiguous chunk of speech; we record
// the start/end timestamps of every chunk in manifest.json so the downstream
// transcription stage can interleave speakers into one chronological,
// speaker-labelled transcript. Speaker labels come from the guild member's
// display name (server nickname), resolved per user.

import { Client, GatewayIntentBits, Events } from 'discord.js';
import {
  joinVoiceChannel,
  EndBehaviorType,
  entersState,
  VoiceConnectionStatus,
} from '@discordjs/voice';
import { OggOpusStream } from './ogg-opus.mjs';
import express from 'express';
import { pipeline } from 'node:stream';
import fs from 'node:fs';
import path from 'node:path';

// ── Config ───────────────────────────────────────────────────────────────
const TOKEN = process.env.RECORDER_BOT_TOKEN;
const SHARED_SECRET = process.env.RECORDER_SHARED_SECRET ?? '';
const PORT = Number(process.env.RECORDER_PORT ?? 8080);
const AUDIO_ROOT = process.env.AUDIO_ROOT ?? '/app/data/recordings';
// End an utterance after this many ms of silence from a speaker.
const SILENCE_MS = Number(process.env.RECORDER_SILENCE_MS ?? 800);
// Hard cap so a missed /stop never leaves the bot parked in a VC forever.
const AUTO_STOP_GRACE_MS = Number(process.env.RECORDER_AUTO_STOP_GRACE_MS ?? 60_000);

if (!TOKEN) {
  console.error('FATAL: RECORDER_BOT_TOKEN is not set.');
  process.exit(1);
}
if (!SHARED_SECRET) {
  console.warn('WARNING: RECORDER_SHARED_SECRET is empty — the control API is unauthenticated.');
}

const log = (...a) => console.log(new Date().toISOString(), '[recorder]', ...a);

// ── Single active recording state ──────────────────────────────────────────
// The clan runs one meeting VC, so one active recording at a time is enough.
let current = null;

const delay = (ms) => new Promise((r) => setTimeout(r, ms));

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

// Capture one utterance from a speaker into its own Ogg Opus file.
function captureUtterance(state, userId, guild) {
  if (state.stopping || state.active.has(userId)) return;
  state.active.add(userId);

  const seq = String(++state.seq).padStart(6, '0');
  const file = `seg_${seq}_user${userId}.ogg`;
  const filePath = path.join(state.audioDir, file);
  const startUtc = new Date().toISOString();

  const opusStream = state.receiver.subscribe(userId, {
    end: { behavior: EndBehaviorType.AfterSilence, duration: SILENCE_MS },
  });
  const oggStream = new OggOpusStream({ channelCount: 2, sampleRate: 48000 });
  const out = fs.createWriteStream(filePath);

  const done = new Promise((resolve) => {
    pipeline(opusStream, oggStream, out, (err) => {
      state.active.delete(userId);
      const endUtc = new Date().toISOString();
      if (err && err.code !== 'ERR_STREAM_PREMATURE_CLOSE' && err.code !== 'ABORT_ERR') {
        log(`segment ${file} pipeline error:`, err.message);
      }
      // Drop empty/near-empty segments (a tiny Ogg header with no audio).
      try {
        if (fs.statSync(filePath).size < 256) {
          fs.unlinkSync(filePath);
          return resolve();
        }
      } catch {
        return resolve();
      }
      const speaker = state.participants[userId] ?? { displayName: userId };
      state.segments.push({ userId, displayName: speaker.displayName, file, startUtc, endUtc });
      resolve();
    });
  });

  state.pending.add(done);
  done.finally(() => state.pending.delete(done));

  // Resolve the speaker name once, lazily, alongside the first capture.
  if (!state.participants[userId]) {
    resolveSpeaker(guild, userId).then((s) => {
      state.participants[userId] = s;
    });
  }
}

// ── Start / stop ──────────────────────────────────────────────────────────
async function startRecording({ meetingRecordingId, guildId, voiceChannelId, meetingTitle, expectedStopUtc }) {
  if (!client.isReady()) throw new Error('Discord client not ready yet.');
  if (current) throw new Error(`Already recording meeting #${current.meetingRecordingId}.`);

  const guild = await client.guilds.fetch(String(guildId));
  await guild.channels.fetch(String(voiceChannelId)); // verify it exists / cache it

  const connection = joinVoiceChannel({
    channelId: String(voiceChannelId),
    guildId: String(guildId),
    adapterCreator: guild.voiceAdapterCreator,
    selfDeaf: false, // MUST be false — a deafened bot receives no audio
    selfMute: true,  // the recorder never speaks
  });

  // Log genuine connection errors; the verbose handshake tracing used during
  // bring-up has been removed now that the connection path is healthy.
  connection.on('error', (err) => log(`[voice] connection error: ${err?.message ?? err}`));

  try {
    await entersState(connection, VoiceConnectionStatus.Ready, 30_000);
  } catch (e) {
    try { connection.destroy(); } catch {}
    throw new Error(`Voice connection never became Ready: ${e.message}`);
  }

  const audioDir = path.join(AUDIO_ROOT, `meeting_${meetingRecordingId}_${Date.now()}`);
  fs.mkdirSync(audioDir, { recursive: true });

  const state = {
    meetingRecordingId,
    guildId: String(guildId),
    voiceChannelId: String(voiceChannelId),
    meetingTitle: meetingTitle ?? '',
    connection,
    receiver: connection.receiver,
    audioDir,
    segments: [],
    participants: {},
    active: new Set(),
    pending: new Set(),
    seq: 0,
    startUtc: new Date().toISOString(),
    stopping: false,
    stopPromise: null,
    autoStop: null,
  };
  current = state;

  state.receiver.speaking.on('start', (userId) => {
    try {
      captureUtterance(state, userId, guild);
    } catch (e) {
      // A capture failure for one utterance must never crash the whole recorder.
      state.active.delete(userId);
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
      stopRecording(meetingRecordingId).catch((e) => log('finalize-on-disconnect error:', e.message));
    }
  });

  // Auto-stop safety net.
  const stopAt = Date.parse(expectedStopUtc);
  const ms = (Number.isFinite(stopAt) ? Math.max(0, stopAt - Date.now()) : 60 * 60_000) + AUTO_STOP_GRACE_MS;
  state.autoStop = setTimeout(() => {
    log(`auto-stop timer fired for meeting #${meetingRecordingId}.`);
    stopRecording(meetingRecordingId).catch((e) => log('auto-stop error:', e.message));
  }, ms);

  log(`recording meeting #${meetingRecordingId} ("${state.meetingTitle}") in VC ${state.voiceChannelId} → ${audioDir}`);
}

async function stopRecording(meetingRecordingId) {
  const state = current;
  if (!state || state.meetingRecordingId !== meetingRecordingId) {
    log(`stop requested for #${meetingRecordingId} but no matching active recording.`);
    return null;
  }
  if (state.stopping) return state.stopPromise;

  state.stopping = true;
  state.stopPromise = (async () => {
    clearTimeout(state.autoStop);
    state.receiver.speaking.removeAllListeners('start');

    // Let in-flight utterances flush (bounded, so a stuck stream can't hang us).
    await Promise.race([Promise.allSettled([...state.pending]), delay(15_000)]);

    state.segments.sort((a, b) => a.startUtc.localeCompare(b.startUtc));
    const manifest = {
      meetingRecordingId: state.meetingRecordingId,
      guildId: state.guildId,
      voiceChannelId: state.voiceChannelId,
      meetingTitle: state.meetingTitle,
      recordingStartedUtc: state.startUtc,
      recordingStoppedUtc: new Date().toISOString(),
      participants: state.participants,
      segments: state.segments,
    };
    fs.writeFileSync(path.join(state.audioDir, 'manifest.json'), JSON.stringify(manifest, null, 2));

    try { state.connection.destroy(); } catch {}

    const audioDir = state.audioDir;
    const count = state.segments.length;
    current = null;
    log(`stopped meeting #${meetingRecordingId}: ${count} segment(s) → ${audioDir}`);
    return count > 0 ? audioDir : null;
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
  res.json({ ok: true, ready: client.isReady(), recording: current?.meetingRecordingId ?? null }),
);

app.post('/record', auth, async (req, res) => {
  try {
    await startRecording(req.body ?? {});
    res.json({ ok: true });
  } catch (e) {
    const conflict = /Already recording/.test(e.message);
    log('record error:', e.message);
    res.status(conflict ? 409 : 500).json({ error: e.message });
  }
});

app.post('/stop', auth, async (req, res) => {
  try {
    const audioDir = await stopRecording(req.body?.meetingRecordingId);
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
    if (current) await stopRecording(current.meetingRecordingId).catch(() => {});
    try { client.destroy(); } catch {}
    process.exit(0);
  });
}

client.login(TOKEN);
