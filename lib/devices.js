"use strict";
/*
 * devices.js — the USB DACs plugged into this machine, and what each says
 * it can take.
 *
 *   Linux  /proc/asound: each USB audio card's stream file lists its
 *          playback formats (S32_LE, S24_3LE…, or DSD_U32_BE for native
 *          DSD), channels and rates exactly as the DAC reports them. The
 *          helper opens it as hw:CARD=<id>,DEV=0. In Docker this needs
 *          /dev/snd passed in.
 *   macOS  Core Audio, through the helper's `list`: the physical formats
 *          (rate ranges, bits, integer or float), the nominal rates, the
 *          transport (USB), and which process has the DAC in hog mode.
 */
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { execFile } = require("child_process");

const STANDARD_RATES = [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000, 1411200, 1536000];
const DOP_RATES = [176400, 352800, 705600, 1411200];

const idOf = (key) => "dac-" + crypto.createHash("sha1").update(key).digest("hex").slice(0, 10);
const read = (f) => { try { return fs.readFileSync(f, "utf8"); } catch (e) { return ""; } };

/* ------------------------------------------------------------------ Linux */

/* A card's stream file → its playback alternate settings. */
function parseStream(text) {
  const play = String(text).split(/^Capture:/m)[0];
  const sec = play.split(/^Playback:/m)[1] || "";
  const alts = [];
  let cur = null;
  for (const line of sec.split("\n")) {
    const t = line.trim();
    let m;
    if (/^Altset\s+\d+/.test(t)) { cur = { format: "", channels: 0, rates: [], bits: 0 }; alts.push(cur); continue; }
    if (!cur) continue;
    if ((m = /^Format:\s*(.+)$/.exec(t))) cur.format = m[1].trim();
    else if ((m = /^Channels:\s*(\d+)/.exec(t))) cur.channels = Number(m[1]);
    else if ((m = /^Bits:\s*(\d+)/.exec(t))) cur.bits = Number(m[1]);
    else if ((m = /^Rates:\s*(.+)$/.exec(t))) {
      const r = /(\d+)\s*-\s*(\d+)\s*\(continuous\)/.exec(m[1]);
      cur.rates = r ? STANDARD_RATES.filter((x) => x >= Number(r[1]) && x <= Number(r[2]))
        : m[1].split(",").map((x) => Number(x.trim())).filter((x) => x > 0);
    }
  }
  const status = (/Status:\s*(\w+)/.exec(sec) || [])[1] || "";
  const freq = Number((/Momentary freq\s*=\s*(\d+)/.exec(sec) || [])[1]) || 0;
  return { alts, running: status === "Running", freq };
}

function bitsOf(alt) {
  if (alt.bits) return alt.bits;
  const m = /S(16|24|32)/.exec(alt.format);
  return m ? Number(m[1]) : 0;
}

function listLinux({ root = "/proc/asound" } = {}) {
  const cards = read(path.join(root, "cards"));
  const out = [];
  const lines = cards.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const m = /^\s*(\d+)\s+\[(\S+)\s*\]:\s*(\S+)\s+-\s+(.*)$/.exec(lines[i]);
    if (!m) continue;
    const [, num, cardId, driver, shortName] = m;
    const dir = path.join(root, "card" + num);
    const usbid = read(path.join(dir, "usbid")).trim();
    if (driver !== "USB-Audio" && !usbid) continue;
    const longName = (lines[i + 1] || "").trim();
    const s = parseStream(read(path.join(dir, "stream0")));
    const pcm = s.alts.filter((a) => !/DSD/i.test(a.format));
    const dsd = s.alts.filter((a) => /DSD/i.test(a.format));
    if (!pcm.length && !fs.existsSync(path.join(dir, "pcm0p"))) continue;
    const rates = [...new Set(pcm.flatMap((a) => a.rates))].sort((a, b) => a - b);
    const bits = [...new Set(pcm.map(bitsOf).filter(Boolean))].sort((a, b) => a - b);
    const channels = Math.max(2, ...pcm.map((a) => a.channels || 0));
    const name = shortName.trim();
    const maker = longName.replace(/\s+at usb-.*$/, "").replace(name, "").trim();
    const status = read(path.join(dir, "pcm0p", "sub0", "status"));
    const owner = Number((/owner_pid\s*:\s*(\d+)/.exec(status) || [])[1]) || 0;
    out.push({
      key: `alsa:${cardId}:${usbid}`,
      name, manufacturer: maker, model: name, transport: "USB", usb: usbid || null,
      spec: `hw:CARD=${cardId},DEV=0`,
      rates, bits, channels,
      formats: s.alts.map((a) => `${a.format}${a.bits ? ` (${a.bits}-bit)` : ""} · ${a.channels} ch · ${a.rates.map(k).join(", ")}`),
      dsdNative: dsd.length ? [...new Set(dsd.flatMap((a) => a.rates))].sort((a, b) => a - b) : [],
      currentRate: s.running ? s.freq : 0,
      volume: null,
      holderPid: owner,
      holderName: owner ? read(`/proc/${owner}/comm`).trim() : ""
    });
  }
  return out;
}

/* ------------------------------------------------------------------ macOS */

/* The helper's list → DACs. Only USB unless all is set; never virtual devices. */
function parseMac(json, { all = false } = {}) {
  let items;
  try { items = JSON.parse(json); } catch (e) { return []; }
  const out = [];
  for (const d of Array.isArray(items) ? items : []) {
    if (!d || !d.uid || !d.channels) continue;
    const usb = d.transport === "usb";
    if (!usb && !all) continue;
    if (/^(virt|grup|aggr|bltn)$/.test(d.transport) && !(all && d.transport === "bltn")) continue;
    const ranges = (d.formats || []).filter((f) => f.id === "lpcm");
    const fromFormats = STANDARD_RATES.filter((r) => ranges.some((f) => r >= f.min - 0.5 && r <= f.max + 0.5));
    const fromNominal = STANDARD_RATES.filter((r) => (d.rates || []).some(([a, b]) => r >= a - 0.5 && r <= b + 0.5));
    const rates = fromFormats.length ? fromFormats : fromNominal;
    const ints = ranges.filter((f) => !f.float);
    const bits = [...new Set((ints.length ? ints : ranges).map((f) => f.bits))].sort((a, b) => a - b);
    const formats = groupFormats(ranges);
    out.push({
      key: `coreaudio:${d.uid}`,
      name: d.name || "USB DAC", manufacturer: d.manufacturer || "", model: d.name || "",
      transport: usb ? "USB" : transportName(d.transport), usb: null,
      spec: d.uid,
      rates, bits, channels: d.channels,
      formats,
      dsdNative: [],
      currentRate: Number(d.rate) || 0,
      volume: d.volume == null ? null : Number(d.volume),
      holderPid: d.hog > 0 ? d.hog : 0,
      holderName: ""
    });
  }
  return out;
}

function transportName(t) {
  return { bltn: "Built-in", hdmi: "HDMI", dprt: "DisplayPort", thun: "Thunderbolt", "1394": "FireWire", blue: "Bluetooth", airp: "AirPlay", pci: "PCI" }[t] || t;
}

/* "24-bit integer · 2 ch · 44.1–384 kHz" lines, one per depth/kind. */
function groupFormats(ranges) {
  const by = new Map();
  for (const f of ranges) {
    const key = `${f.bits}-bit ${f.float ? "float" : "integer"} · ${f.channels} ch`;
    const list = by.get(key) || [];
    for (const r of STANDARD_RATES) if (r >= f.min - 0.5 && r <= f.max + 0.5 && !list.includes(r)) list.push(r);
    by.set(key, list);
  }
  return [...by].map(([key, rs]) => `${key} · ${rs.sort((a, b) => a - b).map(k).join(", ")}`);
}

function listMac(helper, opts) {
  return new Promise((resolve) => execFile(helper, ["list"], { timeout: 10000, maxBuffer: 4 << 20 }, (err, stdout) => {
    resolve(err ? { devices: [], error: err.code === "ENOENT" ? "the helper isn't built: run the installer again" : String(err.message) } : { devices: parseMac(stdout, opts) });
  }));
}

/* Process names for the pids holding DACs (macOS). */
function processNames(pids) {
  const list = [...new Set(pids.filter((p) => p > 0))];
  if (!list.length) return Promise.resolve({});
  return new Promise((resolve) => execFile("ps", ["-o", "pid=,comm=", "-p", list.join(",")], { timeout: 3000 }, (err, stdout) => {
    const out = {};
    for (const line of String(stdout || "").split("\n")) {
      const m = /^\s*(\d+)\s+(.+)$/.exec(line);
      if (m) out[m[1]] = friendlyProcess(m[2]);
    }
    resolve(out);
  }));
}

function friendlyProcess(comm) {
  const base = String(comm).split("/").pop();
  const app = /\/([^/]+)\.app\//.exec(String(comm));
  if (/audirvana/i.test(comm)) return "Audirvana";
  if (/dachelper/i.test(base)) return "the bridge";
  return app ? app[1] : base;
}

/* ------------------------------------------------------------------ both */

async function list({ platform = process.platform, helper, all = false, injected = null } = {}) {
  let devices = [], error = "";
  if (injected) devices = injected;
  else if (platform === "linux") devices = listLinux();
  else if (platform === "darwin") {
    const r = await listMac(helper, { all });
    devices = r.devices;
    error = r.error || "";
    const names = await processNames(devices.map((d) => d.holderPid));
    for (const d of devices) d.holderName = names[d.holderPid] || "";
  } else error = `${platform} isn't supported: macOS or Linux only`;
  for (const d of devices) {
    d.id = idOf(d.key);
    d.dopRates = DOP_RATES.filter((r) => d.rates.includes(r));
  }
  return { devices, error };
}

/* "44.1 kHz" */
function k(r) { return (r / 1000).toFixed(r % 1000 ? 1 : 0).replace(/\.0$/, "") + " kHz"; }

module.exports = { list, listLinux, parseStream, parseMac, groupFormats, friendlyProcess, idOf, STANDARD_RATES, DOP_RATES, kHz: k };
