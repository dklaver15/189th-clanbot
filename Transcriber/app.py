"""
ClanGuard transcription sidecar.

Turns a recorded meeting into a single chronological, speaker-labelled
transcript using faster-whisper — locally, so member audio never leaves the
droplet and Anthropic stays the only external AI dependency.

Speaker attribution is free: the recorder tags audio with the speaking member's
Discord display name. There is no diarization.

── Two manifest formats are supported ──────────────────────────────────────
schemaVersion 2 (current): one CONTINUOUS Ogg track per speaker. Because each
  track is silence-padded to real meeting time by the recorder, a segment's
  in-file timestamp IS its offset from recording start, so we can merge all
  speakers chronologically.

  ── Why the track is decoded in windows ──────────────────────────────────
  faster-whisper decodes a whole file into memory as one 16 kHz float32 array
  before it transcribes anything. Silence padding means every speaker's track
  is as long as the MEETING, not as long as that person talked, so a 62-minute
  meeting costs ~238 MB per track no matter how little was said. On 2026-08-09
  that OOM-killed this container (640m cgroup limit, no swap) roughly 180 times
  in a row: two meetings' audio was never transcribed and both recordings aged
  out and were lost. See _build_windows / _iter_window_audio below.

  So a track is never handed to Whisper whole. It is stream-decoded in ONE pass
  and cut into windows of at most WHISPER_MAX_WINDOW_SEC, and only the window
  currently being filled is held in memory. Peak RSS is therefore set by the
  window size (~19 MB at the 300s default) and is flat in meeting length.
  Windows the manifest says contain no speech are skipped entirely, so cost is
  proportional to talking rather than to sitting in the channel.

schemaVersion 1 (legacy / pre-rewrite recordings): one Ogg per UTTERANCE, with
  a flat `segments` list. We transcribe each file and order by recorded start
  time. Kept so older recordings (and any captured mid-migration) still work.

Control API (called by the C# bot's MeetingMinutesService over the compose
network; never exposed to the host):
  GET  /health                 -> {ok, ready, model, busy}
  POST /transcribe {audioDir}  -> {ok, segmentCount, speakerCount, transcript, segments}
                                  also writes transcript.txt + transcript.json into audioDir
"""

import os
import json
import resource
import threading
from datetime import datetime, timezone

import av
import numpy as np
from flask import Flask, request, jsonify
from faster_whisper import WhisperModel

# ── Config ──────────────────────────────────────────────────────────────────
SHARED_SECRET = os.environ.get("TRANSCRIBER_SHARED_SECRET", "")
PORT = int(os.environ.get("TRANSCRIBER_PORT", "8090"))
MODEL_NAME = os.environ.get("WHISPER_MODEL", "base.en")
DEVICE = os.environ.get("WHISPER_DEVICE", "cpu")
COMPUTE = os.environ.get("WHISPER_COMPUTE", "int8")
BEAM_SIZE = int(os.environ.get("WHISPER_BEAM_SIZE", "1"))   # 1 = greedy/fast; bump for accuracy
LANGUAGE = os.environ.get("WHISPER_LANGUAGE", "en")

# Whisper's own input rate. Decoding to anything else would just make it resample.
SAMPLE_RATE = 16000
# Longest stretch of a speaker track handed to Whisper in one go. This is the
# knob that bounds memory: seconds * 16000 * 4 bytes. 300s ~= 19 MB.
MAX_WINDOW_SEC = float(os.environ.get("WHISPER_MAX_WINDOW_SEC", "300"))
# Utterances closer together than this are treated as one stretch of speech when
# deciding which windows are worth transcribing.
RANGE_MERGE_GAP_SEC = float(os.environ.get("WHISPER_RANGE_MERGE_GAP_SEC", "5"))
# Slack applied when testing a window against the manifest's utterance ranges.
# The recorder's silence padding rounds +/-10ms per gap, so a track's internal
# timeline can drift from the manifest by a few seconds over a long meeting.
# This is deliberately far larger than any plausible drift: being too generous
# only costs a little CPU, being too tight would silently drop real speech.
RANGE_SLACK_SEC = float(os.environ.get("WHISPER_RANGE_SLACK_SEC", "30"))


def log(*a):
    print(datetime.now(timezone.utc).isoformat(), "[transcriber]", *a, flush=True)


def rss_mb():
    """
    Current resident memory in MB, read straight from /proc so it costs nothing
    and needs no extra dependency.
    """
    try:
        with open("/proc/self/statm") as f:
            pages = int(f.read().split()[1])
        return pages * os.sysconf("SC_PAGE_SIZE") / (1024 * 1024)
    except Exception:
        return None


def peak_rss_mb():
    """High-water resident memory in MB. ru_maxrss is kilobytes on Linux."""
    try:
        return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024
    except Exception:
        return None


def mem_note():
    """
    'mem 312 MB (peak 340 MB)' for log lines.

    Worth carrying on every track: when this container is OOM-killed the only
    record is in the host's dmesg, and the container log just shows the process
    silently restarting. A running memory figure makes the trend visible BEFORE
    the kill instead of only after it, and shows how much of the cgroup limit
    (640m at the time of writing) is actually being used.
    """
    cur, peak = rss_mb(), peak_rss_mb()
    if cur is None and peak is None:
        return "mem unknown"
    if peak is None:
        return f"mem {cur:.0f} MB"
    return f"mem {cur:.0f} MB (peak {peak:.0f} MB)"


if not SHARED_SECRET:
    log("WARNING: TRANSCRIBER_SHARED_SECRET is empty — the control API is unauthenticated.")

log(f"loading whisper model '{MODEL_NAME}' (device={DEVICE}, compute={COMPUTE})...")
model = WhisperModel(MODEL_NAME, device=DEVICE, compute_type=COMPUTE)
log(f"model loaded. {mem_note()}")

# faster-whisper is not thread-safe and transcription is CPU-heavy; serialize.
_lock = threading.Lock()

app = Flask(__name__)


def parse_iso(s):
    """Parse an ISO-8601 timestamp (handles the trailing 'Z' from JS Date)."""
    try:
        return datetime.fromisoformat(str(s).replace("Z", "+00:00"))
    except Exception:
        return None


def fmt_offset(seconds):
    if seconds is None or seconds < 0:
        seconds = 0
    m, s = divmod(int(seconds), 60)
    h, m = divmod(m, 60)
    return f"{h:02d}:{m:02d}:{s:02d}" if h else f"{m:02d}:{s:02d}"


def atomic_write_text(path, text):
    """Write text to a temp file then rename into place, so a concurrent reader
    (the bot resuming after a timeout) never sees a half-written file."""
    tmp = f"{path}.tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
    os.replace(tmp, path)


def authorized(req):
    return not SHARED_SECRET or req.headers.get("x-transcriber-secret") == SHARED_SECRET


@app.get("/health")
def health():
    # rssMb/peakRssMb are here so memory can be checked live without waiting for a
    # meeting: `curl transcriber:8090/health`. Against the 640m container limit,
    # a steady peak well under that is the evidence that windowing is holding.
    return jsonify(
        ok=True,
        ready=True,
        model=MODEL_NAME,
        busy=_lock.locked(),
        rssMb=round(rss_mb() or 0, 1),
        peakRssMb=round(peak_rss_mb() or 0, 1),
        maxWindowSec=MAX_WINDOW_SEC,
    )


# ── schemaVersion 2: continuous per-speaker tracks ───────────────────────────
def _speech_ranges(track):
    """
    Where this speaker actually talked, in track-relative seconds, taken from the
    manifest's per-utterance entries and merged into runs. Returns [] when the
    manifest carries no usable timing, which callers must read as "unknown", not
    as "silent" — an empty list means transcribe everything.
    """
    raw = []
    for seg in track.get("segments") or []:
        start_ms = seg.get("startOffsetMs")
        if start_ms is None:
            continue
        try:
            start = float(start_ms) / 1000.0
        except (TypeError, ValueError):
            continue
        a, b = parse_iso(seg.get("startUtc")), parse_iso(seg.get("endUtc"))
        dur = (b - a).total_seconds() if (a and b) else 0.0
        # A missing or nonsensical duration should never shrink the range.
        if not dur or dur <= 0:
            dur = 1.0
        raw.append((max(0.0, start), max(0.0, start) + dur))

    raw.sort()
    merged = []
    for a, b in raw:
        if merged and a - merged[-1][1] <= RANGE_MERGE_GAP_SEC:
            merged[-1][1] = max(merged[-1][1], b)
        else:
            merged.append([a, b])
    return [(a, b) for a, b in merged]


def _has_speech(start, end, speech):
    """Does [start, end) plausibly contain speech, allowing for timeline drift?"""
    if not speech:
        return True  # unknown timing: never skip on a guess
    return any(a - RANGE_SLACK_SEC < end and b + RANGE_SLACK_SEC > start for a, b in speech)


def _file_duration(path):
    """Track length in seconds, or None if the container does not report one."""
    try:
        with av.open(path) as container:
            if container.duration:
                return float(container.duration) / av.time_base
            stream = container.streams.audio[0]
            if stream.duration and stream.time_base:
                return float(stream.duration * stream.time_base)
    except Exception as e:
        log("  could not read duration:", repr(e))
    return None


def _build_windows(duration, speech):
    """
    Cut [0, duration) into windows of at most MAX_WINDOW_SEC, dropping the ones
    the manifest says are silent. A boundary that would land mid-utterance is
    pushed out to the end of that utterance so a sentence is not sliced in half;
    the push is capped so one long run cannot grow a window without bound.
    """
    windows = []
    cursor = 0.0
    hard_cap = MAX_WINDOW_SEC * 1.5
    while cursor < duration:
        end = min(cursor + MAX_WINDOW_SEC, duration)
        for a, b in speech:
            if a < end < b:
                end = min(b + 0.5, cursor + hard_cap, duration)
                break
        if end <= cursor:  # defensive: always make progress
            end = min(cursor + MAX_WINDOW_SEC, duration)
        if _has_speech(cursor, end, speech):
            windows.append((cursor, end))
        cursor = end
    return windows


def _resampled(resampler, frame):
    """PyAV returns a frame, a list of frames, or None depending on version."""
    out = resampler.resample(frame)
    if out is None:
        return []
    return out if isinstance(out, list) else [out]


def _iter_window_audio(path, windows):
    """
    Stream-decode `path` ONCE, yielding (window_start_seconds, samples) for each
    requested window in order. Only the window being filled is retained, so peak
    memory is a function of MAX_WINDOW_SEC and not of the file's length. This is
    the whole point of the module: never materialise a full track.
    """
    if not windows:
        return

    idx = 0
    pos = 0  # absolute sample index of the next decoded sample
    buf = []
    w_start, w_end = windows[0]
    s_start, s_end = int(w_start * SAMPLE_RATE), int(w_end * SAMPLE_RATE)

    with av.open(path) as container:
        stream = container.streams.audio[0]
        stream.thread_type = "AUTO"
        resampler = av.AudioResampler(format="flt", layout="mono", rate=SAMPLE_RATE)

        def chunks():
            for frame in container.decode(stream):
                for rs in _resampled(resampler, frame):
                    yield rs.to_ndarray().reshape(-1)
            for rs in _resampled(resampler, None):  # flush
                yield rs.to_ndarray().reshape(-1)

        for arr in chunks():
            if idx >= len(windows):
                break
            arr = np.asarray(arr, dtype=np.float32)
            chunk_start, chunk_end = pos, pos + arr.size
            pos = chunk_end

            # One decoded chunk can straddle a window boundary, so loop.
            while idx < len(windows):
                if chunk_end <= s_start:
                    break  # still ahead of this window
                lo = max(chunk_start, s_start) - chunk_start
                hi = min(chunk_end, s_end) - chunk_start
                if hi > lo:
                    buf.append(arr[lo:hi])
                if chunk_end < s_end:
                    break  # window not full yet
                yield w_start, (np.concatenate(buf) if buf else np.zeros(0, np.float32))
                buf = []
                idx += 1
                if idx < len(windows):
                    w_start, w_end = windows[idx]
                    s_start, s_end = int(w_start * SAMPLE_RATE), int(w_end * SAMPLE_RATE)

    # The file ended mid-window (short track, or a duration the container
    # over-reported). Whatever we gathered is still real audio.
    if idx < len(windows) and buf:
        yield w_start, np.concatenate(buf)


def _transcribe_track(fpath, name, label, speech):
    """Transcribe one speaker track in bounded windows. Returns a result list."""
    duration = _file_duration(fpath)
    if duration is None:
        # No reported duration: fall back to covering the last known utterance,
        # and failing that just stream until the file runs out.
        duration = (speech[-1][1] + 60.0) if speech else 24 * 3600.0

    windows = _build_windows(duration, speech)
    size_mb = os.path.getsize(fpath) / (1024 * 1024)
    log(f"  {label} {name}: {size_mb:.1f} MB, {duration / 60:.1f} min, "
        f"{len(windows)} window(s) with speech")

    results = []
    for w_start, samples in _iter_window_audio(fpath, windows):
        if samples.size == 0:
            continue
        segments, _info = model.transcribe(
            samples,
            language=LANGUAGE,
            beam_size=BEAM_SIZE,
            vad_filter=True,  # skip the silence padding inside the window
        )
        for seg in segments:
            text = (seg.text or "").strip()
            if not text:
                continue
            # seg.start is relative to the window; the window's own start is the
            # offset from recording start, because the track is padded to real time.
            results.append({
                "offset": w_start + float(seg.start),
                "displayName": name,
                "text": text,
            })
    return results


def transcribe_tracks(audio_dir, manifest):
    """
    Transcribe each speaker's continuous track in bounded windows and tag every
    resulting segment with that speaker. Returns a flat, speaker-tagged result
    list (unsorted). A track that fails is logged and skipped: one unreadable or
    oversized file must not cost us the whole meeting.
    """
    tracks = manifest.get("tracks", [])
    participants = manifest.get("participants", {}) or {}
    total = len(tracks)
    log(f"transcribing {total} speaker track(s) from {audio_dir}, {mem_note()}")

    results = []
    speakers = set()
    for idx, tr in enumerate(tracks, start=1):
        fname = tr.get("file", "")
        fpath = os.path.join(audio_dir, fname)
        uid = str(tr.get("userId", ""))
        name = (
            tr.get("displayName")
            or (participants.get(uid) or {}).get("displayName")
            or uid
            or "Unknown"
        )
        label = f"[{idx}/{total}]"
        if not os.path.isfile(fpath):
            log(f"  {label} missing track file, skipping: {fname}")
            continue

        try:
            track_results = _transcribe_track(fpath, name, label, _speech_ranges(tr))
        except Exception as e:
            log(f"  {label} {name}: track FAILED, skipping it: {repr(e)}")
            continue

        if track_results:
            speakers.add(name)
            results.extend(track_results)
        log(f"  {label} {name}: {len(track_results)} segment(s), {mem_note()}")

    return results, speakers


# ── schemaVersion 1: legacy per-utterance files ──────────────────────────────
def transcribe_file(path):
    segments, _info = model.transcribe(
        path,
        language=LANGUAGE,
        beam_size=BEAM_SIZE,
        vad_filter=True,
    )
    return " ".join((seg.text or "").strip() for seg in segments).strip()


def transcribe_segments(audio_dir, manifest):
    """Legacy path: one file per utterance, ordered by recorded start time."""
    rec_start = parse_iso(manifest.get("recordingStartedUtc"))
    raw_segments = manifest.get("segments", [])
    total = len(raw_segments)
    log(f"transcribing {total} legacy utterance file(s) from {audio_dir}")

    results = []
    speakers = set()
    for idx, seg in enumerate(raw_segments, start=1):
        fpath = os.path.join(audio_dir, seg.get("file", ""))
        if not os.path.isfile(fpath):
            continue
        text = transcribe_file(fpath)
        if not text:
            continue
        seg_start = parse_iso(seg.get("startUtc"))
        offset = (seg_start - rec_start).total_seconds() if (seg_start and rec_start) else None
        name = seg.get("displayName") or seg.get("userId") or "Unknown"
        speakers.add(name)
        results.append({"offset": offset, "displayName": name, "text": text})
        if idx % 250 == 0:
            log(f"  ...{idx}/{total} utterances transcribed")

    return results, speakers


@app.post("/transcribe")
def transcribe():
    if not authorized(request):
        return jsonify(error="unauthorized"), 401

    body = request.get_json(silent=True) or {}
    audio_dir = body.get("audioDir")
    if not audio_dir:
        return jsonify(error="audioDir is required"), 400

    manifest_path = os.path.join(audio_dir, "manifest.json")
    if not os.path.isfile(manifest_path):
        return jsonify(error=f"manifest.json not found in {audio_dir}"), 404

    # Serve from cache: if this dir was already transcribed (transcript.json is
    # present and valid), return it instead of re-running Whisper. This makes a
    # duplicate or retried /transcribe call cheap and idempotent — e.g. after the
    # bot's HTTP call timed out while the (still-running) transcription finished.
    transcript_json = os.path.join(audio_dir, "transcript.json")
    if os.path.isfile(transcript_json):
        try:
            with open(transcript_json, "r", encoding="utf-8") as f:
                cached = json.load(f)
            if cached.get("transcript"):
                segs = cached.get("segments", []) or []
                speakers = {s.get("displayName") for s in segs if s.get("displayName")}
                log(f"serving cached transcript for {audio_dir} ({len(segs)} segment(s))")
                return jsonify(
                    ok=True,
                    segmentCount=len(segs),
                    speakerCount=len(speakers),
                    transcript=cached["transcript"],
                    segments=segs,
                    cached=True,
                )
        except Exception as e:
            log("cached transcript unreadable, will re-transcribe:", repr(e))

    if not _lock.acquire(blocking=False):
        return jsonify(error="busy: another transcription is in progress"), 409

    try:
        with open(manifest_path, "r", encoding="utf-8") as f:
            manifest = json.load(f)

        # Choose path by schema. Default to continuous-track (v2) when `tracks`
        # is present; fall back to legacy per-utterance otherwise.
        schema = manifest.get("schemaVersion", 1)
        if schema >= 2 or manifest.get("tracks"):
            results, speakers = transcribe_tracks(audio_dir, manifest)
        else:
            results, speakers = transcribe_segments(audio_dir, manifest)

        # Chronological merge by offset (seconds from recording start). Segments
        # with an unknown offset sort to the front deterministically.
        results.sort(key=lambda r: (r["offset"] is None, r["offset"] or 0.0))
        lines = [f"[{fmt_offset(r['offset'])}] {r['displayName']}: {r['text']}" for r in results]
        transcript = "\n".join(lines)

        atomic_write_text(os.path.join(audio_dir, "transcript.txt"), transcript)
        atomic_write_text(
            os.path.join(audio_dir, "transcript.json"),
            json.dumps({"segments": results, "transcript": transcript}, ensure_ascii=False, indent=2),
        )

        log(f"done: {len(results)} non-empty segment(s), {len(speakers)} speaker(s), {mem_note()}")
        return jsonify(
            ok=True,
            segmentCount=len(results),
            speakerCount=len(speakers),
            transcript=transcript,
            segments=results,
        )
    except Exception as e:
        log("transcribe error:", repr(e))
        return jsonify(error=str(e)), 500
    finally:
        _lock.release()


if __name__ == "__main__":
    log(f"control API listening on :{PORT}")
    app.run(host="0.0.0.0", port=PORT, threaded=True)
