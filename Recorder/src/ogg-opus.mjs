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
// References: RFC 7845 (Ogg Opus), RFC 3533 (Ogg), Opus is 48 kHz.

import { Transform } from 'node:stream';

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

// Build one Ogg page. segments: array of Buffers, each < 255*255 bytes of payload.
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

// A Transform that consumes raw Opus frames (Buffers) and emits Ogg/Opus bytes.
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

  _transform(frame, _enc, cb) {
    try {
      if (!this._headerWritten) this._writeHeaders();
      // Each Opus frame from Discord is 20ms = 960 samples @ 48kHz.
      this._granule += 960;
      this.push(buildPage({
        headerType: 0x00,
        granulePosition: this._granule,
        serial: this._serial,
        sequence: this._seq++,
        packets: [frame],
      }));
      cb();
    } catch (e) {
      cb(e);
    }
  }

  _flush(cb) {
    try {
      if (!this._headerWritten) this._writeHeaders();
      // EOS marker page (header type 0x04 = end of stream), empty packet.
      this.push(buildPage({
        headerType: 0x04,
        granulePosition: this._granule,
        serial: this._serial,
        sequence: this._seq++,
        packets: [Buffer.alloc(0)],
      }));
      cb();
    } catch (e) {
      cb(e);
    }
  }
}
