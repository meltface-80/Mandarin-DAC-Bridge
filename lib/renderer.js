"use strict";
/*
 * renderer.js — one DAC as a UPnP media renderer: the transport (a URI, the
 * next URI, play, pause, stop, seek), the position, and the audio path from
 * the controller's stream to the DAC's helper.
 *
 *   controller's URL ─▶ source (ffmpeg / DoP) ─▶ 32-bit PCM ─▶ Sink ─▶ helper ─▶ DAC
 *
 * Gapless: when a track has been decoded and the next one is the same rate
 * and channels, it goes into the same open stream with nothing between.
 * When it differs, the first is let play out, the DAC is set to the new
 * rate, and the next one starts.
 *
 * The position is the DAC's own clock: the helper reports the frames it has
 * played; "marks" say at which frame each track began, so the track shown
 * changes when the DAC reaches it, not when it was decoded.
 */
const http = require("http");
const https = require("https");
const { EventEmitter } = require("events");
const Sources = require("./sources");
const XML = require("./xml");
const { muteDop } = require("./dsd");

class UPnPError extends Error {
  constructor(code, description) { super(description); this.code = code; this.description = description; }
}

const ACTIVE = new Set(["PLAYING", "TRANSITIONING"]);

function guessMime(uri) {
  const ext = (/\.([a-z0-9]{2,5})(?:[?#]|$)/i.exec(String(uri).split("?")[0]) || [])[1];
  const map = { flac: "audio/flac", wav: "audio/wav", aif: "audio/aiff", aiff: "audio/aiff", dsf: "audio/dsf", dff: "audio/dff",
    m4a: "audio/mp4", mp4: "audio/mp4", mp3: "audio/mpeg", ogg: "audio/ogg", opus: "audio/opus", aac: "audio/aac" };
  return ext ? map[ext.toLowerCase()] || "" : "";
}

/* The Content-Type a URL answers with, when nothing else says (raw L16/L24 needs it). */
function contentType(uri) {
  return new Promise((resolve) => {
    let u;
    try { u = new URL(uri); } catch (e) { return resolve(""); }
    const lib = u.protocol === "https:" ? https : http;
    const req = lib.request(u, { method: "GET", headers: { Range: "bytes=0-0" }, timeout: 3000 }, (res) => {
      resolve(String(res.headers["content-type"] || ""));
      res.destroy();
    });
    req.on("timeout", () => req.destroy());
    req.on("error", () => resolve(""));
    req.end();
  });
}

class Renderer extends EventEmitter {
  /*
   * dac(): { rates, channels, dsd } as they are now (the settings can change).
   */
  constructor({ id, sink, ffmpeg, dac, arbiter, log = () => {} }) {
    super();
    this.id = id;
    this.sink = sink;
    this.ffmpeg = ffmpeg;
    this.dac = dac;
    this.arbiter = arbiter;
    this.log = log;

    this.transport = "NO_MEDIA_PRESENT";
    this.status = "OK";
    this.error = "";
    this.cur = null;            // the track shown (and heard)
    this.next = null;
    this.volume = 100;
    this.muted = false;

    this.run = 0;               // the play under way; an older one stops by itself
    this.src = null;
    this.fmt = null;            // { rate, channels, physical } the helper is set to
    this.written = 0;           // frames sent in the helper's current session
    this.played = 0;            // frames the DAC has played of them
    this.drained = true;
    this.marks = [];            // [{ frame, track, offset, info }]
    this.mark = null;           // the one being heard
    this.pendingOffset = 0;     // a seek while stopped
    this.feeding = 0;           // the run whose feed is still going

    sink.on("pos", (frames) => this.onPos(frames));
    sink.on("restart", () => {
      this.fmt = null;
      this.drained = true;
      if (this.isActive() || this.transport === "PAUSED_PLAYBACK") this.fail(this.run, new Error("the DAC's helper restarted"));
    });
    sink.on("status", () => this.emit("change"));
  }

  isActive() { return ACTIVE.has(this.transport); }

  track(uri, meta) {
    const d = XML.parseDidl(meta, uri);
    return Object.assign(d, { uri, meta: meta || "", mime: (d.mime || guessMime(uri)).toLowerCase(), info: null });
  }

  /* ------------------------------------------------------------ the transport */

  async setUri(uri, meta) {
    if (!uri) throw new UPnPError(714, "Illegal MIME-type");
    const was = this.isActive();
    await this.halt();
    this.cur = this.track(uri, meta);
    this.next = null;
    this.pendingOffset = 0;
    this.mark = null;
    this.status = "OK";
    this.error = "";
    this.transport = "STOPPED";
    this.emit("change");
    if (was) await this.play();
  }

  async setNext(uri, meta) {
    this.next = uri ? this.track(uri, meta) : null;
    this.emit("change");
  }

  async play() {
    if (!this.sink.exclusive) throw new UPnPError(701, "The DAC is held by another program: " + (this.sink.msg || "waiting for it"));
    if (this.transport === "PAUSED_PLAYBACK" && this.feeding === this.run) {
      this.sink.resume();
      this.transport = this.fmt && this.marks.length ? "PLAYING" : "TRANSITIONING";
      this.emit("change");
      return;
    }
    if (this.isActive()) return;
    if (!this.cur) throw new UPnPError(701, "Nothing to play");
    this.start(this.cur, this.pendingOffset);
  }

  async pause() {
    if (!this.isActive()) return;
    this.sink.pause();
    this.transport = "PAUSED_PLAYBACK";
    this.emit("change");
  }

  async stop() {
    await this.halt();
    this.pendingOffset = 0;
    this.mark = null;
    this.transport = this.cur ? "STOPPED" : "NO_MEDIA_PRESENT";
    this.emit("change");
  }

  async seek(seconds) {
    if (!this.cur) throw new UPnPError(701, "Nothing to seek in");
    const s = Math.max(0, Number(seconds) || 0);
    if (this.isActive() || this.transport === "PAUSED_PLAYBACK") {
      const paused = this.transport === "PAUSED_PLAYBACK";
      await this.halt();
      if (paused) this.sink.pause();
      this.start(this.cur, s, paused);
    } else {
      this.pendingOffset = s;
      this.emit("change");
    }
  }

  setVolume() { this.emit("change"); }   // fixed at 100: bit-perfect
  setMute(m) { this.muted = !!m; this.emit("change"); }

  /* ------------------------------------------------------------ the audio path */

  /* Everything stops, the helper drops what it holds; the DAC stays held. */
  async halt() {
    this.run++;
    if (this.src) { this.src.kill(); this.src = null; }
    await this.sink.stop();
    this.written = 0;
    this.played = 0;
    this.drained = true;
    this.marks = [];
  }

  start(track, offset, paused = false) {
    const run = ++this.run;
    this.status = "OK";
    this.error = "";
    this.transport = paused ? "PAUSED_PLAYBACK" : "TRANSITIONING";
    this.mark = { frame: 0, track, offset, info: track.info };
    if (!paused) this.sink.resume();
    this.emit("change");
    this.feeding = run;
    this.feed(run, track, offset)
      .catch((e) => this.fail(run, e))
      .finally(() => { if (this.feeding === run) this.feeding = 0; });
  }

  async feed(run, first, offset) {
    let t = first, off = offset;
    while (t) {
      if (!t.mime) {
        const ct = await contentType(t.uri);
        if (ct) t.mime = ct.split(";")[0].trim().toLowerCase() + (/^audio\/l\d+/i.test(ct) ? ct.slice(ct.indexOf(";")) : "");
      }
      if (run !== this.run) return;
      let src, info;
      try {
        src = await Sources.open(t, off, { ffmpeg: this.ffmpeg, dac: this.dac(), log: (m) => this.log(m) });
        if (run !== this.run) { src.kill(); return; }
        this.src = src;
        info = await src.ready;
      } catch (e) {
        if (t === first) throw e;
        // The next track won't play: let this one finish, then stop.
        this.log(`next track: ${e.message}`);
        if (src) src.kill();
        this.src = null;
        break;
      }
      if (run !== this.run) { src.kill(); return; }
      t.info = info;
      if (!t.duration && info.duration) t.duration = info.duration;

      if (!this.fmt || this.fmt.rate !== info.rate || this.fmt.channels !== info.channels) {
        if (!this.drained) {
          await this.sink.drain();
          if (run !== this.run) { src.kill(); return; }
          this.afterDrain();
        }
        const ev = await this.sink.format(info.rate, info.channels);
        if (run !== this.run) { src.kill(); return; }
        this.fmt = { rate: info.rate, channels: info.channels, physical: ev.physical || "" };
        this.written = 0;
        this.played = 0;
        this.marks = [];
      }
      const mark = { frame: this.written, track: t, offset: off, info };
      this.marks.push(mark);
      if (t === first) this.announce(mark);
      if (this.transport === "TRANSITIONING") this.transport = "PLAYING";
      this.emit("change");

      await this.pump(run, src, info);
      if (run !== this.run) return;
      this.src = null;

      // The next track: given already, or given while this one plays out.
      t = await this.awaitNext(run);
      off = 0;
      if (run !== this.run) return;
    }
    if (!this.drained) {
      await this.sink.drain();
      if (run !== this.run) return;
      this.afterDrain();
    }
    this.transport = "STOPPED";
    this.mark = null;
    this.src = null;
    this.arbiter.touch();
    this.emit("change");
  }

  /* The next track once this one is decoded; waits while there's audio left to play. */
  async awaitNext(run) {
    for (;;) {
      if (run !== this.run) return null;
      const n = this.next;
      if (n && !n.taken) { n.taken = true; return n; }
      const left = this.fmt ? (this.written - this.played) / this.fmt.rate : 0;
      if (left < 0.5 || this.transport === "STOPPED") return null;
      await new Promise((r) => setTimeout(r, 100));
    }
  }

  pump(run, src, info) {
    return new Promise((resolve, reject) => {
      const s = src.stream;
      const frameBytes = info.channels * 4;
      let rem = null, waiting = false, finished = false;
      const finish = (err) => {
        if (finished) return;
        finished = true;
        s.removeListener("data", onData);
        this.sink.removeListener("drain", onDrain);
        if (err && run === this.run) reject(err); else resolve();
      };
      const onDrain = () => { if (waiting) { waiting = false; s.resume(); } };
      const onData = (chunk) => {
        if (run !== this.run) { s.destroy(); return finish(); }
        let buf = rem ? Buffer.concat([rem, chunk]) : chunk;
        const usable = buf.length - (buf.length % frameBytes);
        rem = usable < buf.length ? Buffer.from(buf.subarray(usable)) : null;
        buf = buf.subarray(0, usable);
        if (!buf.length) return;
        if (this.muted) buf = info.dop ? muteDop(buf) : Buffer.alloc(buf.length);
        this.written += buf.length / frameBytes;
        this.drained = false;
        if (!this.sink.write(buf)) { waiting = true; s.pause(); }
      };
      this.sink.on("drain", onDrain);
      s.on("data", onData);
      s.once("end", () => finish());
      s.once("close", () => finish());
      s.once("error", (e) => finish(e));
    });
  }

  afterDrain() {
    this.written = 0;
    this.played = 0;
    this.marks = [];
    this.drained = true;
  }

  onPos(frames) {
    this.played = frames;
    if (this.isActive()) this.arbiter.touch();
    let m = null;
    for (const x of this.marks) if (x.frame <= frames) m = x;
    if (m && m !== this.mark) this.announce(m);
  }

  /* The DAC has reached this mark's track. */
  announce(mark) {
    this.mark = mark;
    if (mark.track !== this.cur) {
      this.cur = mark.track;
      if (this.next === mark.track) this.next = null;
      this.emit("change");
    }
  }

  fail(run, err) {
    if (run !== this.run || (err && err.stopped)) return;
    this.log("play: " + err.message);
    this.run++;
    if (this.src) { this.src.kill(); this.src = null; }
    this.sink.stop().catch(() => {});
    this.afterDrain();
    this.transport = "STOPPED";
    this.status = "ERROR_OCCURRED";
    this.error = err.message;
    this.mark = null;
    this.emit("change");
  }

  /* ------------------------------------------------------------ reads */

  position() {
    const m = this.mark;
    if (!m) return this.pendingOffset;
    const rate = this.fmt ? this.fmt.rate : 0;
    let t = m.offset + (rate && this.marks.includes(m) ? Math.max(0, this.played - m.frame) / rate : 0);
    const d = this.cur && this.cur.duration;
    if (d && t > d) t = d;
    return t;
  }

  nowPlaying() {
    const m = this.mark;
    const t = this.cur;
    const i = (m && m.info) || (t && t.info) || null;
    return {
      transport: this.transport,
      status: this.status,
      error: this.error,
      title: t ? t.title : "",
      artist: t ? t.artist : "",
      album: t ? t.album : "",
      position: Math.round(this.position()),
      duration: Math.round((t && t.duration) || 0),
      format: i ? describe(i) : "",
      physical: this.fmt ? this.fmt.physical : "",
      muted: this.muted
    };
  }
}

/* { rate, bits, codec, dop, resampled } → "96 kHz · 24-bit · FLAC". */
function describe(i) {
  const k = (r) => (r / 1000).toFixed(r % 1000 ? 1 : 0).replace(/\.0$/, "") + " kHz";
  if (i.dop) return `${i.codec} · DoP at ${k(i.rate)}`;
  const parts = [i.resampled ? `${k(i.srcRate)} → ${k(i.rate)}` : k(i.rate)];
  if (i.bits && !i.float) parts.push(`${i.bits}-bit`);
  const codec = String(i.codec || "").replace(/^pcm_.*/, "PCM").replace(/^dsd_.*/, "DSD → PCM").toUpperCase();
  if (codec) parts.push(codec);
  return parts.join(" · ");
}

module.exports = { Renderer, UPnPError, describe, guessMime };
