"""
ClanGuard transcription sidecar.

Turns a recorded meeting into a single chronological, speaker-labelled
transcript using faster-whisper — locally, so member audio never leaves the
droplet and Anthropic stays the only external AI dependency.

Speaker attribution is free: the recorder tags audio with the speaking member's
Discord display name. There is no diarization.

── Two manifest formats are supported ──────────────────────────────────────
schemaVersion 2 (current): one CONTINUOUS Ogg track per speaker. We transcribe
  each speaker's track ONCE (with word/segment timestamps) and tag every
  resulting segment with that speaker. Because each track is silence-padded to
  real meeting time by the recorder, a segment's in-file timestamp IS its
  offset from recording start, so we can merge all speakers chronologically.
  This is ~N Whisper invocations (N = speakers) instead of one per utterance —
  the fix for hour-long meetings taking 75+ minutes.

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
import threading
from datetime import datetime, timezone

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


def log(*a):
    print(datetime.now(timezone.utc).isoformat(), "[transcriber]", *a, flush=True)


if not SHARED_SECRET:
    log("WARNING: TRANSCRIBER_SHARED_SECRET is empty — the control API is unauthenticated.")

log(f"loading whisper model '{MODEL_NAME}' (device={DEVICE}, compute={COMPUTE})...")
model = WhisperModel(MODEL_NAME, device=DEVICE, compute_type=COMPUTE)
log("model loaded.")

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


def authorized(req):
    return not SHARED_SECRET or req.headers.get("x-transcriber-secret") == SHARED_SECRET


@app.get("/health")
def health():
    return jsonify(ok=True, ready=True, model=MODEL_NAME, busy=_lock.locked())


# ── schemaVersion 2: continuous per-speaker tracks ───────────────────────────
def transcribe_tracks(audio_dir, manifest):
    """
    One Whisper call per speaker track. Each track is wall-clock aligned to the
    recording start, so a segment's in-file `start` (seconds) is its offset from
    recording start. Returns a flat, speaker-tagged result list (unsorted).
    """
    tracks = manifest.get("tracks", [])
    participants = manifest.get("participants", {}) or {}
    total = len(tracks)
    log(f"transcribing {total} speaker track(s) from {audio_dir}")

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
        if not os.path.isfile(fpath):
            log(f"  [{idx}/{total}] missing track file, skipping: {fname}")
            continue

        log(f"  [{idx}/{total}] transcribing {name}'s track ({fname})...")
        segments, _info = model.transcribe(
            fpath,
            language=LANGUAGE,
            beam_size=BEAM_SIZE,
            vad_filter=True,  # skip the silence padding between utterances
        )

        n = 0
        for seg in segments:
            text = (seg.text or "").strip()
            if not text:
                continue
            # seg.start is seconds from the start of THIS track == offset from
            # recording start, because the track is silence-padded to real time.
            results.append({
                "offset": float(seg.start),
                "displayName": name,
                "text": text,
            })
            n += 1
        if n:
            speakers.add(name)
        log(f"  [{idx}/{total}] {name}: {n} segment(s)")

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

        with open(os.path.join(audio_dir, "transcript.txt"), "w", encoding="utf-8") as f:
            f.write(transcript)
        with open(os.path.join(audio_dir, "transcript.json"), "w", encoding="utf-8") as f:
            json.dump({"segments": results, "transcript": transcript}, f, ensure_ascii=False, indent=2)

        log(f"done: {len(results)} non-empty segment(s), {len(speakers)} speaker(s)")
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
