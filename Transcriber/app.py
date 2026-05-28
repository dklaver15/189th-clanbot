"""
ClanGuard transcription sidecar.

Turns a recorded meeting (a directory of per-utterance Ogg Opus files + a
manifest.json, produced by the recorder sidecar) into a single chronological,
speaker-labelled transcript using faster-whisper — locally, so member audio
never leaves the droplet and Anthropic stays the only external AI dependency.

Speaker attribution is free: the recorder already tagged every segment with the
speaking member's Discord display name, so we just transcribe each segment's
text and order by its recorded start time (no diarization needed).

Control API (called by the C# bot's MeetingMinutesService over the compose
network; never exposed to the host):
  GET  /health                      -> {ok, ready, model, busy}
  POST /transcribe {audioDir}       -> {ok, segmentCount, speakerCount, transcript, segments}
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


def transcribe_file(path):
    """Transcribe one short audio segment to a single text string."""
    segments, _info = model.transcribe(
        path,
        language=LANGUAGE,
        beam_size=BEAM_SIZE,
        vad_filter=True,  # drop non-speech so silence/keyboard noise isn't hallucinated
    )
    return " ".join(seg.text.strip() for seg in segments).strip()


def authorized(req):
    return not SHARED_SECRET or req.headers.get("x-transcriber-secret") == SHARED_SECRET


@app.get("/health")
def health():
    return jsonify(ok=True, ready=True, model=MODEL_NAME, busy=_lock.locked())


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

        rec_start = parse_iso(manifest.get("recordingStartedUtc"))
        raw_segments = manifest.get("segments", [])
        log(f"transcribing {len(raw_segments)} segment(s) from {audio_dir}")

        results = []
        speakers = set()
        for seg in raw_segments:
            fpath = os.path.join(audio_dir, seg.get("file", ""))
            if not os.path.isfile(fpath):
                log(f"  missing segment file, skipping: {seg.get('file')}")
                continue
            text = transcribe_file(fpath)
            if not text:
                continue
            seg_start = parse_iso(seg.get("startUtc"))
            offset = (seg_start - rec_start).total_seconds() if (seg_start and rec_start) else None
            name = seg.get("displayName") or seg.get("userId") or "Unknown"
            speakers.add(name)
            results.append({
                "startUtc": seg.get("startUtc"),
                "offset": offset,
                "displayName": name,
                "text": text,
            })

        # Chronological merge.
        results.sort(key=lambda r: (r["startUtc"] or ""))
        lines = [f"[{fmt_offset(r['offset'])}] {r['displayName']}: {r['text']}" for r in results]
        transcript = "\n".join(lines)

        # Persist alongside the audio (the C# side also copies the text into the
        # DB so it survives the keep-last-N audio prune).
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
    # threaded=False keeps a single worker so transcriptions never overlap.
    app.run(host="0.0.0.0", port=PORT, threaded=True)
