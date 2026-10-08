"use strict";
/*
 * sink.js — one DAC's helper process (helper/proto.h), from Node's side.
 *
 * The helper is started when the DAC is found and runs until it goes: it
 * holds the DAC exclusively the whole time, playing or not. If something
 * else has it, the helper keeps trying and says who ("status"). If the
 * helper dies it is started again; if the DAC was unplugged ("gone") the
 * manager lets the bridge for it go.
 *
 * Sessions ("gen"): a stop or seek starts a new one, and anything the helper
 * still holds from an older one is thrown away. The frame counts in "pos"
 * events start at 0 with each format, stop or drain.
 */
const { spawn } = require("child_process");
const { EventEmitter } = require("events");

const CHUNK = 64 * 1024;

class Sink extends EventEmitter {
  constructor({ helper, device, log = () => {} }) {
    super();
    this.helper = helper;
    this.device = device;
    this.log = log;
    this.proc = null;
    this.gen = 1;
    this.exclusive = false;
    this.holder = 0;
    this.msg = "starting";
    this.closing = false;
    this.waits = [];        // [{ ev, gen, resolve, reject }]
    this.restarts = 0;
    this.goneFlag = false;
  }

  start() {
    if (this.proc || this.closing) return;
    let proc;
    try {
      proc = spawn(this.helper, ["hold", this.device], { stdio: ["pipe", "pipe", "pipe", "pipe"] });
    } catch (e) {
      this.msg = "the helper can't start: " + e.message;
      this.emit("status");
      return;
    }
    this.proc = proc;
    this.ctl = proc.stdio[3];
    proc.stdin.on("error", () => {});
    this.ctl.on("error", () => {});
    proc.stdin.on("drain", () => this.emit("drain"));
    let buf = "";
    proc.stdout.on("data", (c) => {
      buf += c.toString("utf8");
      let i;
      while ((i = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, i).trim();
        buf = buf.slice(i + 1);
        if (!line) continue;
        let ev;
        try { ev = JSON.parse(line); } catch (e) { this.log("helper: " + line); continue; }
        this.onEvent(ev);
      }
    });
    proc.stderr.on("data", (c) => this.log("helper: " + c.toString("utf8").trim()));
    proc.on("error", (e) => {
      this.msg = e.code === "ENOENT" ? `the helper (${this.helper}) is missing: run the installer again` : e.message;
      this.emit("status");
    });
    proc.on("exit", (code) => {
      this.proc = null;
      this.exclusive = false;
      this.failWaits(new Error("the DAC's helper stopped"));
      this.emit("restart");
      if (this.closing) return;
      if (code === 3 || this.goneFlag) { this.emit("gone"); return; }
      this.msg = `helper stopped (${code}); starting it again`;
      this.emit("status");
      const wait = Math.min(10000, 1000 * ++this.restarts);
      setTimeout(() => this.start(), wait);
    });
  }

  onEvent(ev) {
    switch (ev.ev) {
      case "status":
        this.exclusive = !!ev.exclusive;
        this.holder = Number(ev.holder) || 0;
        this.msg = ev.msg || "";
        if (this.exclusive) this.restarts = 0;
        this.emit("status");
        break;
      case "pos":
        if (ev.gen === this.gen) this.emit("pos", Number(ev.frames) || 0);
        break;
      case "format":
      case "drained":
      case "stopped":
        this.settle(ev);
        break;
      case "underrun":
        this.emit("underrun");
        break;
      case "gone":
        this.goneFlag = true;
        break;
      case "error":
        this.log("helper: " + ev.msg);
        break;
      default:
    }
  }

  settle(ev) {
    const keep = [];
    for (const w of this.waits) {
      if (w.ev === ev.ev && w.gen === ev.gen) {
        if (ev.ev === "format" && !ev.ok) w.reject(new Error(ev.msg || "the DAC refused the format"));
        else w.resolve(ev);
      } else keep.push(w);
    }
    this.waits = keep;
  }

  wait(ev, gen) {
    return new Promise((resolve, reject) => this.waits.push({ ev, gen, resolve, reject }));
  }

  failWaits(err) {
    const w = this.waits;
    this.waits = [];
    for (const x of w) x.reject(err);
  }

  frame(type, payload) {
    if (!this.proc) return false;
    const h = Buffer.alloc(9);
    h.write(type, 0, "latin1");
    h.writeUInt32LE(this.gen >>> 0, 1);
    h.writeUInt32LE(payload ? payload.length : 0, 5);
    this.proc.stdin.write(h);
    return payload && payload.length ? this.proc.stdin.write(payload) : true;
  }

  command(line) { if (this.proc) this.ctl.write(line + "\n"); }

  /* ------------------------------------------------------------ the player's calls */

  async format(rate, channels) {
    if (!this.proc) throw new Error(this.msg || "the DAC's helper isn't running");
    const p = this.wait("format", this.gen);
    this.frame("F", Buffer.from(`${rate} ${channels}`, "latin1"));
    return p;
  }

  /* PCM; false when the helper is full (wait for "drain"). */
  write(buf) {
    let ok = true;
    for (let i = 0; i < buf.length; i += CHUNK) ok = this.frame("P", buf.subarray(i, Math.min(buf.length, i + CHUNK)));
    return ok;
  }

  /* Resolves once everything sent has been played. */
  async drain() {
    if (!this.proc) return;
    const p = this.wait("drained", this.gen);
    this.frame("D");
    return p;
  }

  /* Drops everything; a new session begins. */
  async stop() {
    this.gen++;
    // Whatever waited on the old session is over.
    const old = this.waits;
    this.waits = [];
    for (const w of old) w.reject(Object.assign(new Error("stopped"), { stopped: true }));
    if (!this.proc) return;
    const p = this.wait("stopped", this.gen);
    this.command(`stop ${this.gen}`);
    const t = new Promise((res) => setTimeout(res, 3000));
    await Promise.race([p.catch(() => {}), t]);
  }

  pause() { this.command("pause"); }
  resume() { this.command("resume"); }

  close() {
    this.closing = true;
    this.failWaits(new Error("closed"));
    if (this.proc) {
      this.command("quit");
      const p = this.proc;
      setTimeout(() => { try { p.kill("SIGKILL"); } catch (e) { /* gone */ } }, 1500);
    }
  }

  get writable() { return !!this.proc && !this.proc.stdin.writableNeedDrain; }
}

module.exports = { Sink };
