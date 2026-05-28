# Transcriber sidecar: Python + faster-whisper (CTranslate2), CPU/int8.
#
# faster-whisper decodes audio via PyAV, whose wheels bundle FFmpeg, so no
# system ffmpeg is required. If you ever see a decode error on the Ogg Opus
# segments, add ffmpeg with:
#   RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Pre-download the model into the image so the first /transcribe doesn't stall
# on a HuggingFace fetch. Pass --build-arg WHISPER_MODEL=small.en to bake a
# bigger model for better accuracy (uses more RAM at runtime).
ARG WHISPER_MODEL=base.en
ENV WHISPER_MODEL=${WHISPER_MODEL}
RUN python -c "import os; from faster_whisper import WhisperModel; WhisperModel(os.environ['WHISPER_MODEL'], device='cpu', compute_type='int8')"

COPY . .

ENV TRANSCRIBER_PORT=8090
EXPOSE 8090

CMD ["python", "app.py"]
