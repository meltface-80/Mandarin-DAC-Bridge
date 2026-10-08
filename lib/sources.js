"use strict";
/*
 * sources.js — a track's URL → 32-bit PCM at the track's own rate.
 *
 * Most tracks go through ffmpeg: whatever the controller sends (FLAC, WAV,
 * AIFF, ALAC, raw L16/L24, MP3, AAC…) comes out as signed 32-bit samples,
 * which hold 16- and 24-bit audio exactly. Nothing is resampled while the
 * DAC takes the track's rate; when it doesn't, the nearest rate it does take
 * in the same family (44.1 or 48 kHz) is made with SoX's resampler.
 *
 * DSD files (DSF, DFF) go to the DAC as DoP when that's switched on for it
 * (lib/dsd.js); otherwise ffmpeg turns them into PCM.
 */
const http = require("http");
const https = require("https");
const { spawn } = require("child_process");
const { Transform } = require("stream");
const DSD = require("./dsd");

const UA = "MandarinDacBridge/1.0";

/* ---------------------------------------------------------------- HTTP */

function httpGet(uri, headers = {}, timeoutMs = 10000, redirects = 5) {
  return new Promise((resolve, reject) => {
    let u;
    try { u = new URL(uri); } catch (e) { return reject(new Error("bad URL " + uri)); }
    const lib = u.protocol === "https:" ? https : http;
    const req = lib.get(u, { headers: Object.assign({ "User-Agent": UA }, headers), timeout: timeoutMs }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
        res.resume();
        return resolve(httpGet(new URL(res.headers.location, u).toString(), headers, timeoutMs, redirects - 1));
      }
      if (res.statusCode >= 400) { res.resume(); return reject(new Error(`HTTP ${res.statusCode} from ${u.host}`)); }
      resolve(res);
    });
    req.on("timeout", () => req.destroy(new Error("timed out fetching " + u.host)));
    req.on("error", reject);
  });
}

/* Up to `bytes` from the start of a URL. */
async function fetchHead(uri, bytes = 65536) {
  const res = await httpGet(uri, { Range: `bytes=0-${bytes - 1}` });
  return new Promise((resolve) => {
    const parts = [];
    let n = 0;
    const done = () => { res.destroy(); resolve(Buffer.concat(parts)); };
    res.on("data", (c) => { parts.push(c); n += c.length; if (n >= bytes) done(); });
    res.on("end", done);
    res.on("error", done);
  });
}

/* Drops the first n bytes of a stream. */
function skipper(n) {
  let left = n;
  return new Transform({
    transform(chunk, enc, done) {
      if (left >= chunk.length) { left -= chunk.length; return done(); }
      const rest = chunk.subarray(left);
      left = 0;
      done(null, rest);
    }
  });
}

/* ---------------------------------------------------------------- choices */

const FAMILY_441 = (r) => r % 11025 === 0;

/*
 * The rate to send a track at: its own when the DAC takes it (or the DAC's
 * rates aren't known), else the DAC's highest rate in the same family that
 * isn't above it, else the family's lowest, else the DAC's highest.
 */
function pickRate(src, rates) {
  if (!rates || !rates.length || rates.includes(src)) return src;
  const fam = rates.filter((r) => FAMILY_441(r) === FAMILY_441(src)).sort((a, b) => b - a);
  const below = fam.find((r) => r <= src);
  if (below) return below;
  if (fam.length) return fam[fam.length - 1];
  return Math.max(...rates);
}

/* "audio/L24;rate=96000;channels=2" → ffmpeg's raw input options. */
function rawPcm(mime) {
  const m = /^audio\/l(16|24|32)\b(.*)$/i.exec(String(mime || "").trim());
  if (!m) return null;
  const rate = Number((/rate=(\d+)/i.exec(m[2]) || [])[1]) || 44100;
  const channels = Number((/channels=(\d+)/i.exec(m[2]) || [])[1]) || 2;
  return { format: `s${m[1]}be`, rate, channels, bits: Number(m[1]) };
}

const isDsd = (track) => /(^|\/)(x-)?(dsf|dff|dsd)\b/i.test(track.mime || "") || /\.(dsf|dff)(\?|#|$)/i.test(track.uri || "");

/* ---------------------------------------------------------------- ffmpeg */

/* Strips ffmpeg's WAV header, telling what it said. */
class WavReader extends Transform {
  constructor() { super(); this.head = Buffer.alloc(0); this.format = null; }
  _transform(chunk, enc, done) {
    if (this.format) return done(null, chunk);
    this.head = Buffer.concat([this.head, chunk]);
    const f = parseWav(this.head);
    if (!f) {
      if (this.head.length > 1 << 20) return done(new Error("ffmpeg's output has no WAV header"));
      return done();
    }
    this.format = f;
    this.emit("format", f);
    const rest = this.head.subarray(f.start);
    this.head = null;
    done(null, rest.length ? rest : undefined);
  }
}

function parseWav(buf) {
  if (buf.length < 12) return null;
  if (buf.toString("latin1", 0, 4) !== "RIFF" || buf.toString("latin1", 8, 12) !== "WAVE") throw new Error("not WAV");
  let at = 12, fmt = null;
  while (at + 8 <= buf.length) {
    const id = buf.toString("latin1", at, at + 4);
    const size = buf.readUInt32LE(at + 4);
    if (id === "data") return fmt ? Object.assign(fmt, { start: at + 8 }) : null;
    if (at + 8 + size > buf.length) return null;
    if (id === "fmt ") fmt = { channels: buf.readUInt16LE(at + 10), rate: buf.readUInt32LE(at + 12), bits: buf.readUInt16LE(at + 22) };
    at += 8 + size + (size & 1);
  }
  return null;
}

/* What ffmpeg says about its input: codec, rate, depth, duration. */
function parseProbe(text) {
  const out = { codec: "", srcRate: 0, bits: 0, float: false, duration: 0, channels: 0 };
  const input = String(text).split(/^Output #0|^Stream mapping/m)[0];
  const d = /Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)/.exec(input);
  if (d) out.duration = Number(d[1]) * 3600 + Number(d[2]) * 60 + Number(d[3]);
  const s = /Stream #\d+:\d+[^:]*: Audio:\s*([^\s,(]+)[^,]*,\s*(\d+)\s*Hz,\s*([^,]+),\s*([a-z0-9]+)(?:\s*\((\d+) bit\))?/.exec(input);
  if (s) {
    out.codec = s[1];
    out.srcRate = Number(s[2]);
    const layout = s[3].trim();
    out.channels = layout === "mono" ? 1 : layout === "stereo" ? 2 : Number((/(\d+) channels/.exec(layout) || [])[1]) || 0;
    const sf = s[4];
    out.float = /^(flt|dbl)/.test(sf);
    out.bits = Number(s[5]) || (/^s16/.test(sf) ? 16 : /^s32/.test(sf) ? 32 : /^u8/.test(sf) ? 8 : /^flt/.test(sf) ? 32 : /^dbl/.test(sf) ? 64 : 0);
  }
  return out;
}

function ffmpegSource({ ffmpeg, uri, offset = 0, raw = null, filters = [], log = () => {} }) {
  const args = ["-hide_banner", "-nostdin", "-loglevel", "info"];
  if (/^https?:/i.test(uri)) args.push("-reconnect", "1", "-reconnect_delay_max", "5", "-user_agent", UA);
  if (offset > 0) args.push("-ss", offset.toFixed(3));
  if (raw) args.push("-f", raw.format, "-ar", String(raw.rate), "-ac", String(raw.channels));
  args.push("-i", uri, "-map", "0:a:0", "-vn", "-sn");
  if (filters.length) args.push("-af", filters.join(","));
  args.push("-c:a", "pcm_s32le", "-f", "wav", "pipe:1");

  const proc = spawn(ffmpeg, args, { stdio: ["ignore", "pipe", "pipe"] });
  const wav = new WavReader();
  let stderr = "";
  let killed = false;
  proc.stderr.on("data", (c) => { if (stderr.length < 65536) stderr += c.toString("utf8"); });
  proc.stdout.pipe(wav);
  proc.stdout.on("error", () => {});

  const ready = new Promise((resolve, reject) => {
    wav.once("format", (f) => {
      // The input's lines come before the output starts; give them a moment.
      setTimeout(() => {
        const p = parseProbe(stderr);
        resolve({
          rate: f.rate, channels: f.channels, codec: p.codec, srcRate: p.srcRate || f.rate,
          bits: raw ? raw.bits : p.bits, float: p.float, duration: p.duration, dop: false
        });
      }, 30);
    });
    proc.on("error", (e) => reject(new Error(e.code === "ENOENT" ? "ffmpeg isn't installed" : e.message)));
    proc.on("exit", (code) => {
      if (wav.format || killed) return;
      const last = stderr.trim().split("\n").filter((l) => !/^\s/.test(l)).slice(-1)[0] || `ffmpeg exited ${code}`;
      reject(new Error(last.slice(0, 300)));
    });
  });
  ready.catch(() => {});
  proc.on("exit", (code, sig) => {
    if (code && !killed && wav.format) log(`ffmpeg ended early (${code}): ${stderr.trim().split("\n").slice(-1)[0]}`);
  });
  wav.on("error", (e) => log("decoder: " + e.message));

  return {
    ready,
    stream: wav,
    kill() { killed = true; try { proc.kill("SIGKILL"); } catch (e) { /* gone */ } wav.destroy(); }
  };
}

/* ---------------------------------------------------------------- DSD as DoP */

async function dsdSource({ uri, offset = 0, dopRates, log = () => {} }) {
  const head = await fetchHead(uri);
  const h = DSD.parseHeader(head);
  if (!h) return null;
  const dopRate = h.rate / 16;
  if (dopRates && dopRates.length && !dopRates.includes(dopRate)) {
    log(`${DSD.dsdName(h.rate)} needs ${dopRate / 1000} kHz for DoP, which the DAC doesn't take: converting to PCM`);
    return null;
  }
  const { offset: off, consumedPerChannel } = DSD.seekOffset(h, offset);
  const start = h.dataStart + off;
  const res = await httpGet(uri, { Range: `bytes=${start}-` });
  const body = res.statusCode === 206 ? res : res.pipe(skipper(start));
  const packer = new DSD.DopPacker(h, consumedPerChannel);
  body.pipe(packer);
  res.on("error", (e) => packer.destroy(e));
  packer.on("error", (e) => log("DSD: " + e.message));
  const info = {
    rate: dopRate, channels: h.channels, bits: 1, codec: DSD.dsdName(h.rate), srcRate: h.rate,
    float: false, duration: h.seconds, dop: true
  };
  return {
    ready: Promise.resolve(info),
    stream: packer,
    kill() { res.destroy(); packer.destroy(); }
  };
}

/* ---------------------------------------------------------------- the choice */

/*
 * A source for a track → { ready: Promise<info>, stream, kill }. info:
 * { rate, channels, bits, codec, srcRate, duration, dop, resampled }.
 *
 *   dac: { rates: [...], channels, dsd: "dop" | "pcm" }
 */
async function open(track, offset, { ffmpeg, dac, log = () => {} }) {
  const rates = (dac && dac.rates) || [];
  if (isDsd(track) && dac && dac.dsd === "dop") {
    try {
      const s = await dsdSource({ uri: track.uri, offset, dopRates: rates, log });
      if (s) return s;
    } catch (e) { log("DSD header: " + e.message + "; trying ffmpeg"); }
  }
  const raw = rawPcm(track.mime);
  let s = ffmpegSource({ ffmpeg, uri: track.uri, offset, raw, log });
  const info = await s.ready;
  const filters = [];
  const want = pickRate(info.rate, rates);
  const maxCh = (dac && dac.channels) || 2;
  if (info.channels === 1) filters.push("pan=stereo|c0=c0|c1=c0");
  else if (info.channels > maxCh) filters.push(`pan=stereo|c0=FL|c1=FR`);
  if (want !== info.rate) filters.push(`aresample=${want}:resampler=soxr:precision=28`);
  if (!filters.length) return s;
  s.kill();
  log(`${track.uri.split("/").pop().slice(0, 60)}: ${filters.join(", ")}`);
  s = ffmpegSource({ ffmpeg, uri: track.uri, offset, raw, filters, log });
  try { await s.ready; } catch (e) {
    if (!filters.some((f) => /soxr/.test(f))) throw e;
    // An ffmpeg built without SoX: its own resampler, at its best.
    log("this ffmpeg has no SoX resampler; using its own");
    const plain = filters.map((f) => f.replace(/:resampler=soxr:precision=28/, ":filter_size=64:phase_shift=10:cutoff=0.97"));
    s = ffmpegSource({ ffmpeg, uri: track.uri, offset, raw, filters: plain, log });
  }
  const ready = s.ready.then((i) => Object.assign(i, {
    srcRate: info.srcRate, bits: info.bits, codec: info.codec, float: info.float,
    duration: i.duration || info.duration, resampled: want !== info.rate
  }));
  ready.catch(() => {});
  return Object.assign(s, { ready });
}

module.exports = { open, pickRate, rawPcm, parseWav, parseProbe, isDsd, httpGet, fetchHead, ffmpegSource, dsdSource };
