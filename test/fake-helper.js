#!/usr/bin/env node
"use strict";
/*
 * A stand-in for bin/dachelper in tests (helper/proto.h): it takes PCM at
 * the speed a DAC would play it — half a second of buffer, consumed in real
 * time — and reports exactly what the real helper reports. FAKE_HELPER_BUSY=1
 * makes it act as if another program had the DAC.
 */
const fs = require("fs");

const out = (o) => process.stdout.write(JSON.stringify(o) + "\n");
const busy = process.env.FAKE_HELPER_BUSY === "1";

let rate = 0, ch = 2, gen = 0, discard = 0, configured = false, paused = false, draining = false;
let buffered = 0;          // frames waiting to be "played"
let played = 0;            // frames played this session
const queue = [];          // frames read from stdin, not yet handled
let lastPos = -1;
const CAP = () => Math.max(1, Math.floor(rate / 2));

out({ ev: "status", exclusive: !busy, holder: busy ? 4242 : process.pid, msg: busy ? "another program has the DAC in exclusive mode" : "" });

// stdin: frames
let inbuf = Buffer.alloc(0);
process.stdin.on("data", (c) => {
  inbuf = Buffer.concat([inbuf, c]);
  while (inbuf.length >= 9) {
    const len = inbuf.readUInt32LE(5);
    if (inbuf.length < 9 + len) break;
    queue.push({ type: String.fromCharCode(inbuf[0]), gen: inbuf.readUInt32LE(1), payload: inbuf.subarray(9, 9 + len) });
    inbuf = inbuf.subarray(9 + len);
  }
  if (queue.length > 8) process.stdin.pause();
  pump();
});
process.stdin.on("end", () => process.exit(0));

// fd 3: commands
const ctl = fs.createReadStream(null, { fd: 3 });
let lines = "";
ctl.on("data", (c) => {
  lines += c;
  let i;
  while ((i = lines.indexOf("\n")) >= 0) {
    const l = lines.slice(0, i); lines = lines.slice(i + 1);
    if (l.startsWith("stop")) {
      discard = gen = Number(l.slice(5)) || 0;
      buffered = 0; played = 0; draining = false; paused = false;
      out({ ev: "stopped", gen });
    } else if (l === "pause") paused = true;
    else if (l === "resume") { paused = false; pump(); }
    else if (l === "quit") process.exit(0);
  }
});
ctl.on("end", () => process.exit(0));

function pump() {
  while (queue.length && !paused) {
    const f = queue[0];
    if (f.gen < discard) { queue.shift(); continue; }
    gen = f.gen;
    if (f.type === "F") {
      queue.shift();
      if (busy) { out({ ev: "format", gen, ok: false, msg: "another program has the DAC in exclusive mode (pid 4242)" }); continue; }
      const [r, c] = f.payload.toString().split(" ").map(Number);
      rate = r; ch = c || 2; configured = true; buffered = 0; played = 0;
      out({ ev: "format", gen, ok: true, rate, channels: ch, physical: "S32_LE, 2 ch (fake)" });
    } else if (f.type === "P") {
      const frames = f.payload.length / (4 * ch);
      if (buffered + frames > CAP() && buffered > 0) break;   // full: wait for the clock
      queue.shift();
      if (configured) buffered += frames;
    } else if (f.type === "D") {
      queue.shift();
      draining = true;
      if (buffered === 0) { draining = false; played = 0; out({ ev: "drained", gen }); }
      break;
    }
  }
  if (queue.length <= 8) process.stdin.resume();
}

// The DAC's clock.
let last = Date.now();
setInterval(() => {
  const now = Date.now();
  const dt = now - last;
  last = now;
  if (!paused && configured && rate) {
    const n = Math.min(buffered, Math.round(rate * dt / 1000));
    buffered -= n;
    played += n;
    if (draining && buffered === 0) { draining = false; played = 0; out({ ev: "drained", gen }); lastPos = -1; }
  }
  if (played !== lastPos) { out({ ev: "pos", gen, frames: played }); lastPos = played; }
  pump();
}, 20);
