"use strict";
/*
 * The whole bridge, end to end, on Linux: the real helper on ALSA's "null"
 * device standing in for a DAC, real ffmpeg, a little HTTP server standing
 * in for Audirvana's or Mandarin's streams, and SOAP from two controllers.
 * Skipped where the helper isn't built (gcc … -lasound) or ffmpeg is missing.
 */
const test = require("node:test");
const assert = require("node:assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const { spawn, execFileSync } = require("child_process");

const ROOT = path.join(__dirname, "..");
const HELPER = path.join(ROOT, "bin", "dachelper");
const has = (cmd) => { try { execFileSync(cmd, ["-version"], { stdio: "ignore" }); return true; } catch (e) { return false; } };
const FAKE = path.join(__dirname, "fake-helper.js");
const realReady = process.platform === "linux" && fs.existsSync(HELPER) && has("ffmpeg");
const fakeReady = process.platform !== "win32" && has("ffmpeg");

let PORT = 55590;
const AVT = "urn:schemas-upnp-org:service:AVTransport:1";

function soap(id, service, type, action, args = {}, ua = "") {
  const body = `<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:${action} xmlns:u="${type}">` +
    Object.entries(args).map(([k, v]) => `<${k}>${String(v).replace(/&/g, "&amp;").replace(/</g, "&lt;")}</${k}>`).join("") +
    `</u:${action}></s:Body></s:Envelope>`;
  return new Promise((resolve, reject) => {
    const headers = { "Content-Type": 'text/xml; charset="utf-8"', SOAPACTION: `"${type}#${action}"`, "Content-Length": Buffer.byteLength(body) };
    if (ua) headers["User-Agent"] = ua;
    const req = http.request({ host: "127.0.0.1", port: PORT, path: `/upnp/${id}/${service}/control`, method: "POST", headers }, (res) => {
      let t = "";
      res.on("data", (c) => (t += c));
      res.on("end", () => {
        const out = { status: res.statusCode, text: t };
        for (const m of t.matchAll(/<(\w+)>([^<]*)<\/\1>/g)) out[m[1]] = m[2];
        resolve(out);
      });
    });
    req.on("error", reject);
    req.end(body);
  });
}
const avt = (id, action, args, ua) => soap(id, "AVTransport", AVT, action, Object.assign({ InstanceID: 0 }, args), ua);
const getJson = (p) => new Promise((resolve, reject) => http.get({ host: "127.0.0.1", port: PORT, path: p }, (res) => {
  let t = ""; res.on("data", (c) => (t += c)); res.on("end", () => resolve(JSON.parse(t)));
}).on("error", reject));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, ms = 8000) {
  const end = Date.now() + ms;
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() > end) throw new Error("timed out waiting");
    await sleep(100);
  }
}

/* Media, a file server, and the bridge with one DAC on the given helper. */
async function setup(t, helper, spec) {
  PORT++;
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "dacbridge-"));
  const media = path.join(tmp, "media");
  fs.mkdirSync(media);
  const make = (name, rate, secs, fmt) => execFileSync("ffmpeg", ["-loglevel", "error", "-y", "-f", "lavfi", "-i", `sine=f=440:r=${rate}:d=${secs}`,
    "-ac", "2", "-sample_fmt", fmt, "-c:a", "flac", path.join(media, name)]);
  make("a96.flac", 96000, 3, "s32");
  make("b96.flac", 96000, 2, "s32");
  make("c44.flac", 44100, 2, "s16");

  const files = http.createServer((req, res) => {
    const f = path.join(media, path.basename(req.url));
    if (!fs.existsSync(f)) { res.writeHead(404); return res.end(); }
    const data = fs.readFileSync(f);
    res.writeHead(200, { "Content-Type": "audio/flac", "Content-Length": data.length });
    res.end(data);
  });
  await new Promise((r) => files.listen(0, "127.0.0.1", r));
  const base = `http://127.0.0.1:${files.address().port}`;

  const dev = { key: "test:" + spec, name: "Test DAC", manufacturer: "ALSA", model: "null", transport: "USB", usb: "0000:0000",
    spec, rates: [44100, 48000, 88200, 96000, 176400, 192000], bits: [16, 24, 32], channels: 2, formats: ["S32_LE · 2 ch"],
    dsdNative: [], currentRate: 0, volume: null, holderPid: 0, holderName: "" };
  const bridge = spawn(process.execPath, [path.join(ROOT, "bridge.js")], {
    env: Object.assign({}, process.env, { PORT: String(PORT), DATA_DIR: tmp, BRIDGE_TEST_DEVICES: JSON.stringify([dev]), BRIDGE_IP: "127.0.0.1",
      LOCK_GRACE_S: "2", DAC_HELPER: helper }),
    stdio: ["ignore", "pipe", "pipe"]
  });
  const logs = { text: "" };
  bridge.stdout.on("data", (c) => (logs.text += c));
  bridge.stderr.on("data", (c) => (logs.text += c));
  t.after(() => { bridge.kill("SIGTERM"); files.close(); });
  const view = await until(async () => { try { const v = await getJson("/api/dacs"); return v.dacs[0] && v.dacs[0].exclusive && v; } catch (e) { return null; } });
  return { id: view.dacs[0].id, base, logs };
}

const AUD = "Audirvana Studio/2.0 UPnP/1.0";

test("plays in real time, keeps others out, goes gapless, seeks, pauses, changes rate", { skip: !fakeReady && "needs ffmpeg" }, async (t) => {
  const { id, base, logs } = await setup(t, FAKE, "fake");
  try {
    // Audirvana loads and plays a 96 kHz track with a 96 kHz one next (gapless).
    let r = await avt(id, "SetAVTransportURI", { CurrentURI: `${base}/a96.flac`, CurrentURIMetaData: "" }, AUD);
    assert.strictEqual(r.status, 200, r.text);
    r = await avt(id, "SetNextAVTransportURI", { NextURI: `${base}/b96.flac`, NextURIMetaData: "" }, AUD);
    assert.strictEqual(r.status, 200, r.text);
    r = await avt(id, "Play", { Speed: 1 }, AUD);
    assert.strictEqual(r.status, 200, r.text);
    await until(async () => (await avt(id, "GetTransportInfo")).CurrentTransportState === "PLAYING");

    // Mandarin (no User-Agent) tries to take over while it plays: kept out, with 705.
    r = await avt(id, "SetAVTransportURI", { CurrentURI: `${base}/c44.flac`, CurrentURIMetaData: "" });
    assert.strictEqual(r.status, 500);
    assert.match(r.text, /<errorCode>705<\/errorCode>/);
    r = await avt(id, "Stop", {});
    assert.match(r.text, /<errorCode>705<\/errorCode>/);
    const v1 = await getJson("/api/dacs");
    assert.strictEqual(v1.dacs[0].control.owner, "Audirvana");
    assert.match(v1.dacs[0].player.format, /96 kHz · 24-bit · FLAC/);
    assert.ok(v1.dacs[0].control.blocked, "the page says who was kept out");

    // The position follows the DAC's clock.
    await sleep(1200);
    const pos = await avt(id, "GetPositionInfo");
    assert.strictEqual(pos.TrackURI, `${base}/a96.flac`);
    assert.match(pos.RelTime, /^0:00:0[12]$/);
    assert.strictEqual(pos.TrackDuration, "0:00:03");

    // The next track takes over when the DAC gets to it; NextURI empties.
    await until(async () => (await avt(id, "GetPositionInfo")).TrackURI === `${base}/b96.flac`, 6000);
    const mi = await avt(id, "GetMediaInfo");
    assert.strictEqual(mi.CurrentURI, `${base}/b96.flac`);
    assert.strictEqual(mi.NextURI, "");
    assert.strictEqual((await avt(id, "GetTransportInfo")).CurrentTransportState, "PLAYING");

    // Seek, pause (the clock stops), resume.
    r = await avt(id, "Seek", { Unit: "REL_TIME", Target: "0:00:01" }, AUD);
    assert.strictEqual(r.status, 200, r.text);
    await avt(id, "Pause", {}, AUD);
    assert.strictEqual((await avt(id, "GetTransportInfo")).CurrentTransportState, "PAUSED_PLAYBACK");
    const p1 = (await avt(id, "GetPositionInfo")).RelTime;
    await sleep(700);
    assert.strictEqual((await avt(id, "GetPositionInfo")).RelTime, p1, "paused: the position holds");
    r = await avt(id, "Play", { Speed: 1 }, AUD);
    assert.strictEqual(r.status, 200, r.text);
    await until(async () => (await avt(id, "GetTransportInfo")).CurrentTransportState === "STOPPED", 6000);

    // After the grace, Mandarin may play: 44.1 kHz, so the DAC changes rate.
    await sleep(2200);
    r = await avt(id, "SetAVTransportURI", { CurrentURI: `${base}/c44.flac`, CurrentURIMetaData: "" });
    assert.strictEqual(r.status, 200, r.text);
    r = await avt(id, "Play", { Speed: 1 });
    assert.strictEqual(r.status, 200, r.text);
    const v2 = await until(async () => { const v = await getJson("/api/dacs"); return /44\.1 kHz · 16-bit/.test(v.dacs[0].player.format) && v; });
    assert.strictEqual(v2.dacs[0].control.owner, "a controller at 127.0.0.1");

    // A different-rate next track: the first plays out, the DAC switches, the second plays.
    r = await avt(id, "SetNextAVTransportURI", { NextURI: `${base}/b96.flac`, NextURIMetaData: "" });
    assert.strictEqual(r.status, 200, r.text);
    await until(async () => { const v = await getJson("/api/dacs"); return /^96 kHz/.test(v.dacs[0].player.format) && v.dacs[0].player.transport === "PLAYING"; }, 6000);
    r = await avt(id, "Stop", {});
    assert.strictEqual(r.status, 200, r.text);
    assert.strictEqual((await avt(id, "GetTransportInfo")).CurrentTransportState, "STOPPED");

    // The UPnP face: description and protocol info.
    const desc = await new Promise((res) => http.get({ host: "127.0.0.1", port: PORT, path: `/upnp/${id}/description.xml` }, (x) => { let s = ""; x.on("data", (c) => (s += c)); x.on("end", () => res(s)); }));
    assert.match(desc, /MediaRenderer:1/);
    assert.match(desc, /Test DAC \(Bridge\)/);
    const pi = await soap(id, "ConnectionManager", "urn:schemas-upnp-org:service:ConnectionManager:1", "GetProtocolInfo");
    assert.match(pi.Sink, /audio\/flac/);
    assert.match(pi.Sink, /audio\/L24;rate=96000/);
  } catch (e) {
    console.log(logs.text);
    throw e;
  }
});

test("the real Linux helper holds an ALSA device and plays through it", { skip: !realReady && "needs bin/dachelper (Linux) and ffmpeg" }, async (t) => {
  // ALSA's "null" device takes audio as fast as it is given, so this checks the path, not the timing.
  const { id, base, logs } = await setup(t, HELPER, "null");
  try {
    for (const f of ["a96.flac", "c44.flac"]) {
      let r = await avt(id, "SetAVTransportURI", { CurrentURI: `${base}/${f}`, CurrentURIMetaData: "" }, AUD);
      assert.strictEqual(r.status, 200, r.text);
      r = await avt(id, "Play", { Speed: 1 }, AUD);
      assert.strictEqual(r.status, 200, r.text);
      await until(async () => (await avt(id, "GetTransportInfo")).CurrentTransportState === "STOPPED");
      const ti = await avt(id, "GetTransportInfo");
      assert.strictEqual(ti.CurrentTransportStatus, "OK");
    }
    assert.match(logs.text, /exclusive: the DAC is the bridge's/);
  } catch (e) {
    console.log(logs.text);
    throw e;
  }
});

test("a DAC another program holds: Play is refused and the page says so", { skip: !fakeReady && "needs ffmpeg" }, async (t) => {
  process.env.FAKE_HELPER_BUSY = "1";
  t.after(() => { delete process.env.FAKE_HELPER_BUSY; });
  PORT++;
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "dacbridge-"));
  const dev = { key: "test:busy", name: "Busy DAC", manufacturer: "", model: "", transport: "USB", usb: null, spec: "busy",
    rates: [44100], bits: [24], channels: 2, formats: [], dsdNative: [], currentRate: 0, volume: null, holderPid: 4242, holderName: "Audirvana" };
  const bridge = spawn(process.execPath, [path.join(ROOT, "bridge.js")], {
    env: Object.assign({}, process.env, { PORT: String(PORT), DATA_DIR: tmp, BRIDGE_TEST_DEVICES: JSON.stringify([dev]), BRIDGE_IP: "127.0.0.1", DAC_HELPER: FAKE }),
    stdio: "ignore"
  });
  t.after(() => bridge.kill("SIGTERM"));
  const v = await until(async () => { try { const x = await getJson("/api/dacs"); return x.dacs[0] && x.dacs[0].waiting && x; } catch (e) { return null; } });
  assert.strictEqual(v.dacs[0].exclusive, false);
  assert.strictEqual(v.dacs[0].holder, "Audirvana");
  let r = await avt(v.dacs[0].id, "SetAVTransportURI", { CurrentURI: "http://127.0.0.1:9/x.flac", CurrentURIMetaData: "" }, AUD);
  r = await avt(v.dacs[0].id, "Play", { Speed: 1 }, AUD);
  assert.match(r.text, /<errorCode>701<\/errorCode>/);
});

test("DSD goes as DoP, an unsupported rate is resampled, and subscribers hear about it", { skip: !fakeReady && "needs ffmpeg" }, async (t) => {
  PORT++;
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "dacbridge-"));
  // A DSD64 DSF of 0.5 s (silence pattern), and a 352.8 kHz FLAC for a DAC that stops at 192 kHz.
  const perCh = 2822400 / 8 / 2, block = 4096, blocks = Math.ceil(perCh / block);
  const data = Buffer.alloc(blocks * block * 2, 0x69);
  const head = Buffer.alloc(92);
  head.write("DSD ", 0); head.writeBigUInt64LE(28n, 4); head.writeBigUInt64LE(BigInt(92 + data.length), 12);
  head.write("fmt ", 28); head.writeBigUInt64LE(52n, 32); head.writeUInt32LE(1, 40); head.writeUInt32LE(2, 48);
  head.writeUInt32LE(2, 52); head.writeUInt32LE(2822400, 56); head.writeUInt32LE(1, 60);
  head.writeBigUInt64LE(BigInt(perCh * 8), 64); head.writeUInt32LE(block, 72);
  head.write("data", 80); head.writeBigUInt64LE(BigInt(12 + data.length), 84);
  const dsfFile = Buffer.concat([head, data]);
  const hi = path.join(tmp, "hi.flac");
  execFileSync("ffmpeg", ["-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:r=352800:d=1", "-ac", "2", "-c:a", "flac", hi]);

  const ranges = [];
  const files = http.createServer((req, res) => {
    const buf = req.url.endsWith(".dsf") ? dsfFile : fs.readFileSync(hi);
    const type = req.url.endsWith(".dsf") ? "audio/dsf" : "audio/flac";
    const m = /bytes=(\d+)-(\d*)/.exec(req.headers.range || "");
    if (m) {
      ranges.push(req.headers.range);
      const a = Number(m[1]), b = m[2] ? Math.min(Number(m[2]), buf.length - 1) : buf.length - 1;
      res.writeHead(206, { "Content-Type": type, "Content-Range": `bytes ${a}-${b}/${buf.length}`, "Content-Length": b - a + 1 });
      return res.end(buf.subarray(a, b + 1));
    }
    res.writeHead(200, { "Content-Type": type, "Content-Length": buf.length });
    res.end(buf);
  });
  await new Promise((r) => files.listen(0, "127.0.0.1", r));
  const base = `http://127.0.0.1:${files.address().port}`;

  // Somewhere for events to arrive.
  const notes = [];
  const cb = http.createServer((req, res) => { let s = ""; req.on("data", (c) => (s += c)); req.on("end", () => { notes.push({ sid: req.headers.sid, body: s }); res.end(); }); });
  await new Promise((r) => cb.listen(0, "127.0.0.1", r));

  const dev = { key: "test:dsd", name: "DSD DAC", manufacturer: "", model: "", transport: "USB", usb: null, spec: "fake",
    rates: [44100, 48000, 88200, 96000, 176400, 192000], bits: [32], channels: 2, formats: [], dsdNative: [176400], currentRate: 0,
    volume: null, holderPid: 0, holderName: "" };
  const bridge = spawn(process.execPath, [path.join(ROOT, "bridge.js")], {
    env: Object.assign({}, process.env, { PORT: String(PORT), DATA_DIR: tmp, BRIDGE_TEST_DEVICES: JSON.stringify([dev]), BRIDGE_IP: "127.0.0.1", DAC_HELPER: FAKE }),
    stdio: ["ignore", "pipe", "pipe"]
  });
  let logs = "";
  bridge.stdout.on("data", (c) => (logs += c));
  t.after(() => { bridge.kill("SIGTERM"); files.close(); cb.close(); });
  try {
    const v = await until(async () => { try { const x = await getJson("/api/dacs"); return x.dacs[0] && x.dacs[0].exclusive && x; } catch (e) { return null; } });
    const id = v.dacs[0].id;

    const sub = await new Promise((resolve, reject) => {
      const req = http.request({ host: "127.0.0.1", port: PORT, path: `/upnp/${id}/AVTransport/event`, method: "SUBSCRIBE",
        headers: { CALLBACK: `<http://127.0.0.1:${cb.address().port}/ev>`, NT: "upnp:event", TIMEOUT: "Second-300" } }, (res) => { res.resume(); resolve(res); });
      req.on("error", reject);
      req.end();
    });
    assert.strictEqual(sub.statusCode, 200);
    assert.match(sub.headers.sid, /^uuid:/);
    await until(() => notes.length >= 1);
    assert.match(notes[0].body, /TransportState val=&quot;NO_MEDIA_PRESENT&quot;/);

    let r = await avt(id, "SetAVTransportURI", { CurrentURI: `${base}/song.dsf`, CurrentURIMetaData: "" }, AUD);
    assert.strictEqual(r.status, 200, r.text);
    await avt(id, "Play", { Speed: 1 }, AUD);
    await until(async () => { const x = await getJson("/api/dacs"); return x.dacs[0].player.format === "DSD64 · DoP at 176.4 kHz"; });
    assert.ok(ranges.includes("bytes=0-65535"), "the header is read first");
    assert.ok(ranges.includes("bytes=92-"), "then the samples from where they start");
    await until(() => notes.some((n) => /TransportState val=&quot;PLAYING&quot;/.test(n.body)));
    await until(async () => (await avt(id, "GetTransportInfo")).CurrentTransportState === "STOPPED", 5000);
    await until(() => notes.some((n) => /TransportState val=&quot;STOPPED&quot;/.test(n.body)));

    r = await avt(id, "SetAVTransportURI", { CurrentURI: `${base}/hi.flac`, CurrentURIMetaData: "" }, AUD);
    await avt(id, "Play", { Speed: 1 }, AUD);
    await until(async () => { const x = await getJson("/api/dacs"); return /^352\.8 kHz → 176\.4 kHz/.test(x.dacs[0].player.format); });
    await avt(id, "Stop", {}, AUD);
  } catch (e) {
    console.log(logs);
    throw e;
  }
});
