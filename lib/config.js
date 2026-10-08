"use strict";
/*
 * config.js — every setting, with a default that needs no changing.
 *
 *   PORT                 the page and the UPnP devices (55500)
 *   DATA_DIR             settings.json lives here (./data)
 *   DAC_HELPER           the native helper (./bin/dachelper)
 *   FFMPEG               the decoder (ffmpeg on the PATH)
 *   BRIDGE_IP            the address to advertise, when the machine has several
 *   LOCK_GRACE_S         how long a controller keeps a DAC after it stops (10)
 *   MANDARIN_PORT        Mandarin's own port, to recognise its streams (3500)
 *   BRIDGE_ALL_OUTPUTS   1 = offer every output, not only USB DACs
 *   BRIDGE_NAME_SUFFIX   added to each DAC's name on the network (" (Bridge)")
 */
const fs = require("fs");
const os = require("os");
const path = require("path");

const ROOT = path.join(__dirname, "..");
const env = process.env;

const config = {
  root: ROOT,
  version: require("../package.json").version,
  port: Number(env.PORT) || 55500,
  dataDir: env.DATA_DIR || path.join(ROOT, "data"),
  helper: env.DAC_HELPER || path.join(ROOT, "bin", "dachelper"),
  ffmpeg: env.FFMPEG || "ffmpeg",
  bindIp: env.BRIDGE_IP || "",
  lockGraceMs: (env.LOCK_GRACE_S != null && env.LOCK_GRACE_S !== "" ? Number(env.LOCK_GRACE_S) : 10) * 1000,
  mandarinPort: Number(env.MANDARIN_PORT) || 3500,
  allOutputs: env.BRIDGE_ALL_OUTPUTS === "1",
  nameSuffix: env.BRIDGE_NAME_SUFFIX != null ? env.BRIDGE_NAME_SUFFIX : " (Bridge)",
  scanMs: 3000,
  platform: process.platform,
  hostname: os.hostname().replace(/\.local$/, "")
};

try { fs.mkdirSync(config.dataDir, { recursive: true }); } catch (e) { /* reported when settings are saved */ }

module.exports = config;
