// Minimal, self-contained Ogg/Opus muxer.
//
// Why this exists: prism-media 1.3.5 (the version @discordjs/voice depends on)
// does NOT export OggLogicalBitstream/OpusHead — those only exist in the
// unreleased prism-media v2 alpha, which drags in node-crc (an ESM module that
// breaks under CommonJS). Rather than take an alpha dependency for our monthly
// meeting, we wrap the receiver's raw Opus frames into an Ogg container here.
//
// Input:  a stream of raw Opus packets (one Buffer per 20ms frame), exactly
//         what VoiceReceiver.subscribe() emits.
// Output: a valid Ogg/Opus (.ogg) byte stream: an OpusHead page, an OpusTags
//         page, then the audio frames paged up. faster-whisper / ffmpeg read
//         this directly.
//
// ── Continuous-track support (added for per-speaker tracks) ──────────────────
// The recorder now keeps ONE muxer open per speaker for the whole meeting,
// rather than one per utterance. Between a speaker's utterances Discord sends
// nothing, so to keep each track aligned to real meeting wall-clock time we
// inject Opus "silence" frames to fill the gap (writeSilence). That alignment
// is what lets the transcriber turn each track's per-segment timestamps back
// into one chronological, speaker-labelled transcript. The silence frame used
// is the canonical 3-byte Opus silence packet (TOC 0xF8 = SILK NB, 20ms, mono
// + two zero length-bytes for the CELT/stereo padding), which every Opus
// decoder renders as 20ms of quiet.
//
// References: RFC 7845 (Ogg Opus), RFC 3533 (Ogg), RFC 6716 (Opus). Opus runs
// at 48 kHz; one Discord frame = 20 ms = 960 samples.

import { Transform } from 'node:stream';

// Samples per 20ms Opus frame at 48kHz.
const SAMPLES_PER_FRAME = 960;
const FRAME_MS = 20;

// Canonical short Opus silence frame. TOC byte 0xF8 selects config 31
// (CELT-only is 0x80+, SILK is low) — in practice the widely-used "Opus
// silence" packet is 0xF8,0xFF,0xFE; decoders emit 20ms of silence for it.
// We keep it tiny so silence padding costs almost nothing on disk.
const OPUS_SILENCE_FRAME = Buffer.from([0xf8, 0xff, 0xfe]);
// An Ogg page holds at most 255 lacing values; a 3-byte packet uses one.
const SILENCE_PACKETS_PER_PAGE = 255;

// ── Ogg CRC (RFC 3533): poly 0x04C11DB7, no reflection, init 0, xorout 0 ─────
const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let r = i << 24;
    for (let j = 0; j < 8; j++) {
      r = (r & 0x80000000) ? ((r << 1) ^ 0x04c11db7) : (r << 1);
    }
    table[i] = r >>> 0;
  }
  return table;
})();

function oggCrc(buf) {
  let crc = 0;
  for (let i = 0; i < buf.length; i++) {
    crc = ((crc << 8) ^ CRC_TABLE[((crc >>> 24) ^ buf[i]) & 0xff]) >>> 0;
  }
  return crc >>> 0;
}

// Build one Ogg page. packets: array of Buffers, each < 255*255 bytes of payload.
function buildPage({ headerType, granulePosition, serial, sequence, packets }) {
  // Lacing: each packet is split into 255-byte segments; a value <255 ends a packet.
  const lacing = [];
  const body = [];
  for (const pkt of packets) {
    let remaining = pkt.length;
    let offset = 0;
    while (remaining >= 255) {
      lacing.push(255);
      body.push(pkt.subarray(offset, offset + 255));
      offset += 255;
      remaining -= 255;
    }
    lacing.push(remaining); // final (possibly 0) lacing value ends the packet
    body.push(pkt.subarray(offset));
  }

  const segmentTable = Buffer.from(lacing);
  const bodyBuf = Buffer.concat(body);

  const header = Buffer.alloc(27 + segmentTable.length);
  header.write('OggS', 0, 'ascii');           // capture pattern
  header.writeUInt8(0, 4);                     // version
  header.writeUInt8(headerType, 5);            // header type flag
  // 64-bit granule position (little-endian)
  header.writeUInt32LE(granulePosition >>> 0, 6);
  header.writeUInt32LE(Math.floor(granulePosition / 0x100000000) >>> 0, 10);
  header.writeUInt32LE(serial >>> 0, 14);      // bitstream serial number
  header.writeUInt32LE(sequence >>> 0, 18);    // page sequence number
  header.writeUInt32LE(0, 22);                 // CRC placeholder
  header.writeUInt8(segmentTable.length, 26);  // number of segments
  segmentTable.copy(header, 27);

  const page = Buffer.concat([header, bodyBuf]);
  const crc = oggCrc(page);
  page.writeUInt32LE(crc, 22);
  return page;
}

function opusHead({ channelCount = 2, sampleRate = 48000 } = {}) {
  const b = Buffer.alloc(19);
  b.write('OpusHead', 0, 'ascii'); // magic
  b.writeUInt8(1, 8);              // version
  b.writeUInt8(channelCount, 9);  // channel count
  b.writeUInt16LE(0, 10);         // pre-skip
  b.writeUInt32LE(sampleRate, 12);// original sample rate
  b.writeUInt16LE(0, 16);         // output gain
  b.writeUInt8(0, 18);            // channel mapping family 0
  return b;
}

function opusTags() {
  const vendor = Buffer.from('clanguard-recorder', 'utf8');
  const b = Buffer.alloc(8 + 4 + vendor.length + 4);
  b.write('OpusTags', 0, 'ascii');
  b.writeUInt32LE(vendor.length, 8);
  vendor.copy(b, 12);
  b.writeUInt32LE(0, 12 + vendor.length); // user comment list length = 0
  return b;
}

/**
 * A Transform that consumes raw Opus frames (Buffers) and emits Ogg/Opus bytes.
 *
 * Two ways to feed it:
 *   - pipe a VoiceReceiver opus stream into it (each chunk = one 20ms frame), or
 *   - call write(frame) / writeSilence(ms) directly for the continuous-track
 *     path, where the recorder owns timing and injects silence between
 *     utterances. (Both can be mixed; piping just calls _transform per chunk.)
 *
 * granuleAtMs(ms) lets the caller know the current track position in ms, so the
 * recorder can compute exactly how much silence to insert to re-align a track
 * to real meeting time before writing the next utterance's frames.
 */
export class OggOpusStream extends Transform {
  constructor({ channelCount = 2, sampleRate = 48000 } = {}) {
    super({ readableObjectMode: false, writableObjectMode: true });
    this._serial = (Math.random() * 0xffffffff) >>> 0;
    this._seq = 0;
    this._granule = 0;
    this._headerWritten = false;
    this._channelCount = channelCount;
    this._sampleRate = sampleRate;
  }

  /** Current track length in milliseconds (based on emitted 20ms frames). */
  get positionMs() {
    return (this._granule / this._sampleRate) * 1000;
  }

  _writeHeaders() {
    // BOS page: OpusHead (its own page, header type 0x02 = beginning of stream)
    this.push(buildPage({
      headerType: 0x02,
      granulePosition: 0,
      serial: this._serial,
      sequence: this._seq++,
      packets: [opusHead({ channelCount: this._channelCount, sampleRate: this._sampleRate })],
    }));
    // OpusTags (its own page)
    this.push(buildPage({
      headerType: 0x00,
      granulePosition: 0,
      serial: this._serial,
      sequence: this._seq++,
      packets: [opusTags()],
    }));
    this._headerWritten = true;
  }

  _emitFrame(frame) {
    if (!this._headerWritten) this._writeHeaders();
    // Each Opus frame is 20ms = 960 samples @ 48kHz.
    this._granule += SAMPLES_PER_FRAME;
    this.push(buildPage({
      headerType: 0x00,
      granulePosition: this._granule,
      serial: this._serial,
      sequence: this._seq++,
      packets: [frame],
    }));
  }

  /**
   * Insert `ms` of Opus silence (rounded to whole 20ms frames). Used by the
   * continuous-track recorder to keep a speaker's track aligned to real meeting
   * time across the gaps when they aren't talking. Capped per call so a bug or
   * a very long idle period can't try to allocate a runaway number of frames.
   * Returns the number of 20ms frames actually written.
   */
  writeSilence(ms) {
    if (!Number.isFinite(ms) || ms <= 0) return 0;
    let frames = Math.round(ms / FRAME_MS);
    // Safety cap: 6 hours of silence is far past any real meeting.
    const MAX_FRAMES = (6 * 60 * 60 * 1000) / FRAME_MS;
    if (frames > MAX_FRAMES) frames = MAX_FRAMES;
    if (!this._headerWritten) this._writeHeaders();
    // Pack up to 255 silence packets per page (one lacing byte each) instead of
    // one page per frame: an hour-long gap becomes ~700 pages, not 180,000,
    // so filling it doesn't stall the event loop that's receiving live audio.
    let remaining = frames;
    while (remaining > 0) {
      const n = Math.min(remaining, SILENCE_PACKETS_PER_PAGE);
      this._granule += n * SAMPLES_PER_FRAME;
      this.push(buildPage({
        headerType: 0x00,
        granulePosition: this._granule,
        serial: this._serial,
        sequence: this._seq++,
        packets: new Array(n).fill(OPUS_SILENCE_FRAME),
      }));
      remaining -= n;
    }
    return frames;
  }

  _transform(frame, _enc, cb) {
    try {
      // VoiceReceiver emits Buffers; ignore non-buffer control chunks defensively.
      if (Buffer.isBuffer(frame) && frame.length > 0) this._emitFrame(frame);
      cb();
    } catch (e) {
      cb(e);
    }
  }

  _flush(cb) {
    try {
      if (!this._headerWritten) this._writeHeaders();
      // EOS marker page (header type 0x04 = end of stream) with no packets.
      // Not an empty packet: FFmpeg rejects a zero-length Opus packet as invalid
      // data, which aborted decoding of every track right at its end.
      this.push(buildPage({
        headerType: 0x04,
        granulePosition: this._granule,
        serial: this._serial,
        sequence: this._seq++,
        packets: [],
      }));
      cb();
    } catch (e) {
      cb(e);
    }
  }
}
