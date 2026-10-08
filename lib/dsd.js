"use strict";
/*
 * dsd.js — DSD files (DSF and DSDIFF/DFF) sent to the DAC as DoP.
 *
 * DoP ("DSD over PCM", v1.1) carries DSD through a PCM path untouched: each
 * 24-bit PCM sample holds 16 DSD bits of one channel under a marker byte
 * (0x05 and 0xFA, taking turns frame by frame) that tells the DAC it is
 * DSD. The PCM rate is the DSD rate / 16: DSD64 → 176.4 kHz, DSD128 →
 * 352.8 kHz, DSD256 → 705.6 kHz. The bridge's PCM path is bit-perfect, so
 * the DAC gets the DSD exactly.
 *
 * In our 32-bit samples a DoP word is  marker << 24 | first byte << 16 |
 * second byte << 8,  the first byte being the earlier in time, most
 * significant bit first.
 */
const { Transform } = require("stream");

const MARKERS = [0x05, 0xfa];
const SILENCE = 0x69;   // DSD's idle pattern

const REVERSE = new Uint8Array(256);
for (let i = 0; i < 256; i++) {
  let r = 0;
  for (let b = 0; b < 8; b++) if (i & (1 << b)) r |= 0x80 >> b;
  REVERSE[i] = r;
}

/*
 * The start of a DSD file → { kind, rate, channels, dataStart, dataBytes,
 * blockSize, lsbFirst, seconds }, or null when it isn't one (or is DST
 * compressed, which can't be sent as DoP). dataStart is where the samples
 * begin in the file.
 */
function parseHeader(buf) {
  if (!buf || buf.length < 16) return null;
  const tag = buf.toString("latin1", 0, 4);
  if (tag === "DSD ") return parseDsf(buf);
  if (tag === "FRM8") return parseDff(buf);
  return null;
}

function parseDsf(buf) {
  // 'DSD ' chunk (28 bytes), then 'fmt ' (52), then 'data' (12 + samples).
  const dsdSize = Number(buf.readBigUInt64LE(4));
  let at = dsdSize;
  if (buf.length < at + 52 || buf.toString("latin1", at, at + 4) !== "fmt ") return null;
  const fmtSize = Number(buf.readBigUInt64LE(at + 4));
  const channels = buf.readUInt32LE(at + 24);
  const rate = buf.readUInt32LE(at + 28);
  const bits = buf.readUInt32LE(at + 32);
  const samples = Number(buf.readBigUInt64LE(at + 36));
  const blockSize = buf.readUInt32LE(at + 44);
  at += fmtSize;
  if (buf.length < at + 12 || buf.toString("latin1", at, at + 4) !== "data") return null;
  const dataSize = Number(buf.readBigUInt64LE(at + 4)) - 12;
  if (!channels || !rate || !blockSize) return null;
  return {
    kind: "dsf", rate, channels, blockSize, lsbFirst: bits === 1,
    dataStart: at + 12, dataBytes: dataSize,
    bytesPerChannel: Math.ceil(samples / 8),
    seconds: samples / rate
  };
}

function parseDff(buf) {
  // FRM8 <size> 'DSD ' then chunks; PROP holds SND with FS, CHNL and CMPR.
  if (buf.toString("latin1", 12, 16) !== "DSD ") return null;
  let at = 16, rate = 0, channels = 0, compressed = false;
  while (at + 12 <= buf.length) {
    const id = buf.toString("latin1", at, at + 4);
    const size = Number(buf.readBigUInt64BE(at + 4));
    if (id === "PROP") {
      let p = at + 16;  // after 'SND '
      const end = Math.min(at + 12 + size, buf.length);
      while (p + 12 <= end) {
        const sid = buf.toString("latin1", p, p + 4);
        const ssize = Number(buf.readBigUInt64BE(p + 4));
        if (sid === "FS  " && p + 16 <= end) rate = buf.readUInt32BE(p + 12);
        if (sid === "CHNL" && p + 14 <= end) channels = buf.readUInt16BE(p + 12);
        if (sid === "CMPR" && p + 16 <= end) compressed = buf.toString("latin1", p + 12, p + 16) !== "DSD ";
        p += 12 + ssize + (ssize & 1);
      }
    } else if (id === "DSD ") {
      if (!rate || !channels || compressed) return null;
      return {
        kind: "dff", rate, channels, blockSize: 0, lsbFirst: false,
        dataStart: at + 12, dataBytes: size,
        bytesPerChannel: Math.floor(size / channels),
        seconds: (size / channels) * 8 / rate
      };
    } else if (id === "DST ") return null;
    at += 12 + size + (size & 1);
  }
  return null;
}

/*
 * Where to start reading the samples for a seek to `seconds`: a byte offset
 * from dataStart, on a boundary the DoP packer can start at.
 */
function seekOffset(h, seconds) {
  const perChannel = Math.floor(Math.max(0, seconds) * h.rate / 8);
  if (h.kind === "dsf") {
    const block = Math.floor(perChannel / h.blockSize);
    return { offset: block * h.blockSize * h.channels, consumedPerChannel: block * h.blockSize };
  }
  const even = perChannel - (perChannel % 2);
  return { offset: even * h.channels, consumedPerChannel: even };
}

/*
 * The sample bytes (from `consumedPerChannel` on) → DoP frames as signed
 * 32-bit little-endian PCM, channel-interleaved. Stops at the end of the real
 * samples (a DSF file's last block is padded), and ends with 50 ms of DoP
 * silence so the DAC doesn't click on the way out.
 */
class DopPacker extends Transform {
  constructor(h, consumedPerChannel = 0) {
    super();
    this.h = h;
    this.pending = Buffer.alloc(0);
    this.left = Math.max(0, h.bytesPerChannel - consumedPerChannel);   // real bytes per channel still to send
    this.marker = 0;
  }

  _transform(chunk, enc, done) {
    try {
      this.pending = this.pending.length ? Buffer.concat([this.pending, chunk]) : chunk;
      const out = this.h.kind === "dsf" ? this.dsf(false) : this.dff(false);
      if (out && out.length) this.push(out);
      done();
    } catch (e) { done(e); }
  }

  _flush(done) {
    const out = this.h.kind === "dsf" ? this.dsf(true) : this.dff(true);
    if (out && out.length) this.push(out);
    this.push(this.silence(Math.round(this.h.rate / 16 * 0.05)));
    done();
  }

  word(a, b) {
    const w = ((MARKERS[this.marker] << 24) | (a << 16) | (b << 8)) | 0;
    return w;
  }

  /* DSF: blocks of blockSize bytes per channel, one channel after another. */
  dsf(final) {
    const { channels, blockSize, lsbFirst } = this.h;
    const group = blockSize * channels;
    const groups = Math.floor(this.pending.length / group);
    const frames = [];
    for (let g = 0; g < groups && this.left > 0; g++) {
      const base = g * group;
      const use = Math.min(blockSize, this.left);
      frames.push(this.pack(use, (ch, i) => {
        const v = this.pending[base + ch * blockSize + i];
        return lsbFirst ? REVERSE[v] : v;
      }));
      this.left -= use;
    }
    this.pending = this.pending.subarray(groups * group);
    if (final && this.pending.length && this.left > 0) {
      // A short last group: take what each channel has.
      const per = Math.min(Math.floor(this.pending.length / channels), this.left, blockSize);
      const p = this.pending;
      frames.push(this.pack(per, (ch, i) => {
        const v = p[ch * blockSize + i];
        return v == null ? SILENCE : (lsbFirst ? REVERSE[v] : v);
      }));
      this.left = 0;
      this.pending = Buffer.alloc(0);
    }
    return frames.length ? Buffer.concat(frames) : null;
  }

  /* DFF: one byte per channel at a time, MSB first. */
  dff(final) {
    const { channels } = this.h;
    let perChannel = Math.min(Math.floor(this.pending.length / channels), this.left);
    if (!final) perChannel -= perChannel % 2;
    if (perChannel <= 0) {
      if (final) this.pending = Buffer.alloc(0);
      return null;
    }
    const p = this.pending;
    const out = this.pack(perChannel, (ch, i) => p[i * channels + ch]);
    this.left -= perChannel;
    this.pending = final ? Buffer.alloc(0) : p.subarray(perChannel * channels);
    return out;
  }

  /* `bytes` DSD bytes per channel, read by at(ch, i), → DoP frames. */
  pack(bytes, at) {
    const { channels } = this.h;
    const frames = Math.ceil(bytes / 2);
    const out = Buffer.allocUnsafe(frames * channels * 4);
    let o = 0;
    for (let f = 0; f < frames; f++) {
      const i = f * 2;
      for (let ch = 0; ch < channels; ch++) {
        const a = at(ch, i);
        const b = i + 1 < bytes ? at(ch, i + 1) : SILENCE;
        out.writeInt32LE(this.word(a, b), o);
        o += 4;
      }
      this.marker ^= 1;
    }
    return out;
  }

  silence(frames) {
    const out = Buffer.allocUnsafe(frames * this.h.channels * 4);
    let o = 0;
    for (let f = 0; f < frames; f++) {
      for (let ch = 0; ch < this.h.channels; ch++) { out.writeInt32LE(this.word(SILENCE, SILENCE), o); o += 4; }
      this.marker ^= 1;
    }
    return out;
  }
}

/* A muted DoP buffer: the markers kept (so the DAC stays in DSD), the DSD idle. */
function muteDop(buf) {
  const out = Buffer.allocUnsafe(buf.length);
  for (let o = 0; o + 4 <= buf.length; o += 4) {
    out.writeInt32LE(((buf.readInt32LE(o) & 0xff000000) | (SILENCE << 16) | (SILENCE << 8)) | 0, o);
  }
  return out;
}

/* "DSD64" from a DSD rate. */
const dsdName = (rate) => "DSD" + Math.round(rate / 44100);

module.exports = { parseHeader, seekOffset, DopPacker, muteDop, dsdName, REVERSE };
