"use strict";
/*
 * manager.js — the DACs found, and one bridge for each: a helper holding the
 * DAC, a renderer playing to it, an arbiter guarding it, and its UPnP face.
 *
 * Looked for every few seconds, so a DAC plugged in later appears by itself
 * (and on the network a moment after), and one unplugged goes. A DAC can be
 * switched off on its page: the bridge lets go of it and stops offering it.
 */
const fs = require("fs");
const path = require("path");
const { EventEmitter } = require("events");
const Devices = require("./devices");
const { Sink } = require("./sink");
const { Renderer } = require("./renderer");
const { Arbiter } = require("./arbiter");
const Control = require("./upnp/control");
const { udnFor } = require("./upnp/description");

class Settings {
  constructor(dir) {
    this.file = path.join(dir, "settings.json");
    try { this.data = JSON.parse(fs.readFileSync(this.file, "utf8")); } catch (e) { this.data = {}; }
    if (!this.data.dacs) this.data.dacs = {};
  }
  dac(id) { return Object.assign({ enabled: true, dsd: "auto", name: "" }, this.data.dacs[id] || {}); }
  set(id, patch) {
    this.data.dacs[id] = Object.assign(this.dac(id), patch);
    try { fs.writeFileSync(this.file, JSON.stringify(this.data, null, 2)); } catch (e) { /* kept for this run */ }
    return this.data.dacs[id];
  }
}

class Bridge extends EventEmitter {
  constructor({ dev, config, settings, log }) {
    super();
    this.id = dev.id;
    this.dev = dev;
    this.config = config;
    this.settings = settings;
    this.log = (m) => log(`[${dev.name}] ${m}`);
    this.udn = udnFor(config.hostname, dev.key);
    this.arbiter = new Arbiter({ graceMs: config.lockGraceMs, mandarinPort: config.mandarinPort });
    this.sink = new Sink({ helper: config.helper, device: dev.spec, log: this.log });
    this.renderer = new Renderer({
      id: this.id, sink: this.sink, ffmpeg: config.ffmpeg, arbiter: this.arbiter, log: this.log,
      dac: () => ({ rates: this.dev.rates, channels: this.dev.channels, dsd: this.dsdMode() })
    });
    this.renderer.on("change", () => this.emit("change"));
    this.sink.on("status", () => {
      if (this.sink.exclusive !== this.wasExclusive) {
        this.log(this.sink.exclusive ? "exclusive: the DAC is the bridge's" : `waiting: ${this.sink.msg}`);
        this.wasExclusive = this.sink.exclusive;
      }
      this.emit("change");
    });
    this.sink.on("gone", () => this.emit("gone"));
  }

  start() { this.sink.start(); }

  async close() {
    try { await this.renderer.halt(); } catch (e) { /* closing anyway */ }
    this.sink.close();
  }

  dsdMode() {
    const s = this.settings.dac(this.id).dsd;
    if (s === "dop" || s === "pcm") return s;
    // auto: DoP where the DAC shows it knows DSD (native DSD formats, Linux); PCM otherwise.
    return this.dev.dsdNative && this.dev.dsdNative.length ? "dop" : "pcm";
  }

  friendlyName() {
    const s = this.settings.dac(this.id);
    return (s.name || this.dev.name) + this.config.nameSuffix;
  }

  protocolInfo() { return Control.protocolInfo(this.dev.rates); }
}

class Manager extends EventEmitter {
  constructor({ config, log = console.log, injected = null }) {
    super();
    this.config = config;
    this.log = log;
    this.injected = injected;
    this.settings = new Settings(config.dataDir);
    this.devices = new Map();   // id → dev (every DAC found, bridged or not)
    this.bridges = new Map();   // id → Bridge
    this.error = "";
    this.scanning = false;
  }

  start() {
    this.scan();
    this.timer = setInterval(() => this.scan(), this.config.scanMs);
  }

  async scan() {
    if (this.scanning) return;
    this.scanning = true;
    try {
      const { devices, error } = await Devices.list({
        platform: this.config.platform, helper: this.config.helper, all: this.config.allOutputs, injected: this.injected
      });
      this.error = error;
      const seen = new Set();
      for (const dev of devices) {
        seen.add(dev.id);
        const known = this.devices.get(dev.id);
        this.devices.set(dev.id, dev);
        const b = this.bridges.get(dev.id);
        if (b) b.dev = dev;
        if (!known) this.log(`found ${dev.name} (${dev.transport}${dev.usb ? " " + dev.usb : ""})`);
        const want = this.settings.dac(dev.id).enabled;
        if (want && !b) this.open(dev);
        if (!want && b) this.closeBridge(dev.id);
      }
      for (const id of [...this.devices.keys()]) {
        if (seen.has(id)) continue;
        this.log(`${this.devices.get(id).name} is gone`);
        this.devices.delete(id);
        this.closeBridge(id);
      }
      this.emit("change");
    } catch (e) {
      this.error = e.message;
    } finally {
      this.scanning = false;
    }
  }

  open(dev) {
    const b = new Bridge({ dev, config: this.config, settings: this.settings, log: this.log });
    this.bridges.set(dev.id, b);
    b.on("change", () => this.emit("bridge-change", b));
    b.on("gone", () => { this.devices.delete(dev.id); this.closeBridge(dev.id); this.emit("change"); });
    b.start();
    this.emit("added", b);
  }

  closeBridge(id) {
    const b = this.bridges.get(id);
    if (!b) return;
    this.bridges.delete(id);
    this.emit("removed", b);
    b.close();
  }

  setSettings(id, patch) {
    const clean = {};
    if ("enabled" in patch) clean.enabled = !!patch.enabled;
    if ("dsd" in patch && ["auto", "dop", "pcm"].includes(patch.dsd)) clean.dsd = patch.dsd;
    if ("name" in patch) clean.name = String(patch.name || "").slice(0, 60).trim();
    const s = this.settings.set(id, clean);
    const dev = this.devices.get(id);
    if (dev && "enabled" in clean) {
      if (clean.enabled && !this.bridges.has(id)) this.open(dev);
      if (!clean.enabled) this.closeBridge(id);
    }
    if ("name" in clean && this.bridges.has(id)) this.emit("renamed", this.bridges.get(id));
    this.emit("change");
    return s;
  }

  release(id) {
    const b = this.bridges.get(id);
    if (b) { b.arbiter.release(); b.emit("change"); }
  }

  async stopAll() {
    clearInterval(this.timer);
    await Promise.all([...this.bridges.values()].map((b) => b.close()));
  }

  /* Everything the page shows. */
  view() {
    const out = [];
    for (const dev of this.devices.values()) {
      const s = this.settings.dac(dev.id);
      const b = this.bridges.get(dev.id);
      const entry = {
        id: dev.id, name: s.name || dev.name, deviceName: dev.name, manufacturer: dev.manufacturer, model: dev.model,
        transport: dev.transport, usb: dev.usb, rates: dev.rates, bits: dev.bits, channels: dev.channels,
        formats: dev.formats, dsdNative: dev.dsdNative, dopRates: dev.dopRates, currentRate: dev.currentRate,
        volume: dev.volume, enabled: s.enabled, dsd: s.dsd, platform: this.config.platform
      };
      if (b) {
        entry.upnpName = b.friendlyName();
        entry.dsdMode = b.dsdMode();
        entry.exclusive = b.sink.exclusive;
        entry.waiting = b.sink.exclusive ? "" : b.sink.msg;
        entry.holder = !b.sink.exclusive && dev.holderPid ? (dev.holderName || `process ${dev.holderPid}`) : "";
        entry.player = b.renderer.nowPlaying();
        entry.control = b.arbiter.view(b.renderer.isActive());
      } else {
        entry.holder = dev.holderPid ? (dev.holderName || `process ${dev.holderPid}`) : "";
      }
      out.push(entry);
    }
    out.sort((a, b) => a.name.localeCompare(b.name));
    return { dacs: out, error: this.error, version: this.config.version, host: this.config.hostname, platform: this.config.platform };
  }
}

module.exports = { Manager, Bridge, Settings };
