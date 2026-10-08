#!/usr/bin/env node
"use strict";
/*
 * Mandarin DAC Bridge — takes each USB DAC on this machine for itself
 * (exclusive, bit-perfect, the DAC switched to every track's own rate) and
 * offers it on the network as a UPnP renderer, so Audirvana and Mandarin can
 * both play to it — one at a time, never over each other.
 *
 * The page is at http://<this machine>:55500. Settings: lib/config.js.
 */
const os = require("os");
const config = require("./lib/config");
const { Manager } = require("./lib/manager");
const { Events } = require("./lib/upnp/gena");
const { Ssdp, interfaces } = require("./lib/upnp/ssdp");
const Control = require("./lib/upnp/control");
const { createServer } = require("./lib/web");

const log = (m) => console.log(`${new Date().toISOString().slice(0, 19).replace("T", " ")}  ${m}`);

// Tests and odd setups can hand the bridge its DACs (JSON, the same shape lib/devices.js makes).
let injected = null;
if (process.env.BRIDGE_TEST_DEVICES) injected = JSON.parse(process.env.BRIDGE_TEST_DEVICES);

const manager = new Manager({ config, log, injected });

const events = new Events({
  log,
  state: (id, service) => {
    const b = manager.bridges.get(id);
    if (!b) return null;
    if (service === "AVTransport") return Control.avtState(b.renderer);
    if (service === "RenderingControl") return Control.rcsState(b.renderer);
    return Control.cmsState(b);
  }
});

const ssdp = new Ssdp({
  port: config.port,
  bindIp: config.bindIp,
  server: `${os.type()}/${os.release()} UPnP/1.0 MandarinDacBridge/${config.version}`,
  log,
  devices: () => [...manager.bridges.values()].map((b) => ({ udn: b.udn, path: `/upnp/${b.id}/description.xml` }))
});

manager.on("added", (b) => {
  log(`offering ${b.friendlyName()} on the network`);
  setTimeout(() => ssdp.announce({ udn: b.udn, path: `/upnp/${b.id}/description.xml` }), 300);
});
manager.on("removed", (b) => {
  ssdp.byebye({ udn: b.udn, path: `/upnp/${b.id}/description.xml` });
  events.drop(b.id);
});
manager.on("renamed", (b) => {
  // Controllers re-read a device that says goodbye and comes back.
  const d = { udn: b.udn, path: `/upnp/${b.id}/description.xml` };
  ssdp.byebye(d);
  setTimeout(() => ssdp.announce(d), 1500);
});
manager.on("bridge-change", (b) => events.changed(b.id));

const server = createServer({ manager, events, config, log });
server.on("error", (e) => {
  log(e.code === "EADDRINUSE" ? `port ${config.port} is in use: is the bridge already running?` : e.message);
  process.exit(1);
});
server.listen(config.port, () => {
  log(`Mandarin DAC Bridge ${config.version} on ${config.platform}`);
  const ips = interfaces(config.bindIp).map((i) => i.address);
  log(`the page: http://localhost:${config.port}${ips.length ? "  ·  " + ips.map((ip) => `http://${ip}:${config.port}`).join("  ·  ") : ""}`);
  ssdp.start();
  manager.start();
});

let stopping = false;
async function shutdown() {
  if (stopping) return;
  stopping = true;
  log("stopping: letting go of the DACs");
  ssdp.stop();
  await manager.stopAll();
  setTimeout(() => process.exit(0), 600);
}
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
