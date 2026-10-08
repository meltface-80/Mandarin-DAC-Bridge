"use strict";
/*
 * ssdp.js — how Audirvana and Mandarin find the DACs: every DAC is a UPnP
 * root device, announced on 239.255.255.250:1900 when it appears (and every
 * minute after), withdrawn when it goes, and described to any M-SEARCH that
 * asks for it. The address in each answer is the one on the asker's network.
 */
const dgram = require("dgram");
const os = require("os");

const ADDR = "239.255.255.250";
const PORT = 1900;
const MAX_AGE = 1800;
const RENDERER = "urn:schemas-upnp-org:device:MediaRenderer:1";
const SERVICE_TYPES = [
  "urn:schemas-upnp-org:service:AVTransport:1",
  "urn:schemas-upnp-org:service:RenderingControl:1",
  "urn:schemas-upnp-org:service:ConnectionManager:1"
];
const SKIP_IF = /^(docker|veth|br-|virbr|lo|utun|awdl|llw|bridge|tailscale|zt)/i;

/* This machine's IPv4 addresses on real networks: [{ name, address, netmask }]. */
function interfaces(pinned) {
  const out = [];
  for (const [name, list] of Object.entries(os.networkInterfaces())) {
    for (const a of list || []) {
      if (a.family !== "IPv4" && a.family !== 4) continue;
      if (a.internal) continue;
      if (pinned ? a.address !== pinned : SKIP_IF.test(name)) continue;
      out.push({ name, address: a.address, netmask: a.netmask });
    }
  }
  if (!out.length && !pinned) {
    // Only odd interfaces (a container, a VPN): use them rather than none.
    for (const [name, list] of Object.entries(os.networkInterfaces()))
      for (const a of list || []) if ((a.family === "IPv4" || a.family === 4) && !a.internal) out.push({ name, address: a.address, netmask: a.netmask });
  }
  return out;
}

const ipNum = (ip) => ip.split(".").reduce((n, x) => (n << 8) + (Number(x) & 255), 0) >>> 0;

/* Our address on the network `remote` is on. */
function addressFor(remote, ifs) {
  if (!ifs.length) return "127.0.0.1";
  for (const i of ifs) if (i.address === remote) return i.address;
  for (const i of ifs) {
    const m = ipNum(i.netmask);
    if ((ipNum(i.address) & m) === (ipNum(remote) & m)) return i.address;
  }
  return ifs[0].address;
}

class Ssdp {
  /*
   * devices(): [{ udn, path }] — every DAC offered now; the LOCATION is
   * http://<our address>:<port><path>.
   */
  constructor({ port, devices, bindIp = "", server, log = () => {} }) {
    this.port = port;
    this.devices = devices;
    this.bindIp = bindIp;
    this.server = server;
    this.log = log;
    this.sock = null;
    this.timer = null;
    this.ifs = [];
  }

  start() {
    this.ifs = interfaces(this.bindIp);
    const sock = dgram.createSocket({ type: "udp4", reuseAddr: true });
    this.sock = sock;
    sock.on("error", (e) => this.log("SSDP: " + e.message));
    sock.on("message", (msg, rinfo) => this.onMessage(msg, rinfo));
    sock.bind(PORT, () => {
      try { sock.setMulticastTTL(4); sock.setMulticastLoopback(true); } catch (e) { /* best effort */ }
      this.join();
      this.announceAll();
    });
    // Interfaces come and go (Wi-Fi, DHCP); look again with each round.
    this.timer = setInterval(() => { this.join(); this.announceAll(); }, 60 * 1000);
    this.timer.unref();
  }

  join() {
    this.ifs = interfaces(this.bindIp);
    this.joined = this.joined || new Set();
    for (const i of this.ifs) {
      if (this.joined.has(i.address)) continue;
      try { this.sock.addMembership(ADDR, i.address); } catch (e) { /* not multicast-capable */ }
      this.joined.add(i.address);
    }
  }

  location(udn, path, address) { return `http://${address}:${this.port}${path}`; }

  targets(d) {
    return [["upnp:rootdevice", `${d.udn}::upnp:rootdevice`], [d.udn, d.udn], [RENDERER, `${d.udn}::${RENDERER}`],
      ...SERVICE_TYPES.map((t) => [t, `${d.udn}::${t}`])];
  }

  notify(d, nts) {
    if (!this.sock) return;
    for (const i of this.ifs) {
      for (const [nt, usn] of this.targets(d)) {
        const lines = ["NOTIFY * HTTP/1.1", `HOST: ${ADDR}:${PORT}`, `NT: ${nt}`, `NTS: ${nts}`, `USN: ${usn}`];
        if (nts === "ssdp:alive") lines.push(`CACHE-CONTROL: max-age=${MAX_AGE}`, `LOCATION: ${this.location(d.udn, d.path, i.address)}`, `SERVER: ${this.server}`);
        const buf = Buffer.from(lines.join("\r\n") + "\r\n\r\n");
        try {
          this.sock.setMulticastInterface(i.address);
          this.sock.send(buf, PORT, ADDR);
        } catch (e) { /* the interface went */ }
      }
    }
  }

  announce(d) { this.notify(d, "ssdp:alive"); setTimeout(() => this.notify(d, "ssdp:alive"), 1000).unref(); }
  announceAll() { for (const d of this.devices()) this.notify(d, "ssdp:alive"); }
  byebye(d) { this.notify(d, "ssdp:byebye"); }

  onMessage(msg, rinfo) {
    const text = msg.toString("utf8");
    if (!/^M-SEARCH \* HTTP\/1\.1/i.test(text)) return;
    const h = {};
    for (const line of text.split(/\r?\n/).slice(1)) {
      const i = line.indexOf(":");
      if (i > 0) h[line.slice(0, i).trim().toLowerCase()] = line.slice(i + 1).trim();
    }
    if (!/ssdp:discover/i.test(h.man || "")) return;
    const st = h.st || "";
    const mx = Math.max(0, Math.min(Number(h.mx) || 0, 3));
    const address = addressFor(rinfo.address, this.ifs);
    for (const d of this.devices()) {
      for (const [nt, usn] of this.targets(d)) {
        if (!(st === "ssdp:all" || st === nt || (st.startsWith("urn:") && sameTypeOlder(st, nt)))) continue;
        const reply = [
          "HTTP/1.1 200 OK", `CACHE-CONTROL: max-age=${MAX_AGE}`, `DATE: ${new Date().toUTCString()}`, "EXT:",
          `LOCATION: ${this.location(d.udn, d.path, address)}`, `SERVER: ${this.server}`, `ST: ${st === "ssdp:all" ? nt : st}`,
          `USN: ${st === "ssdp:all" || st === nt ? usn : `${d.udn}::${st}`}`, "Content-Length: 0"
        ].join("\r\n") + "\r\n\r\n";
        setTimeout(() => { try { this.sock.send(Buffer.from(reply), rinfo.port, rinfo.address); } catch (e) { /* closed */ } }, Math.random() * mx * 300);
      }
    }
  }

  stop() {
    for (const d of this.devices()) this.byebye(d);
    clearInterval(this.timer);
    const s = this.sock;
    this.sock = null;
    if (s) setTimeout(() => { try { s.close(); } catch (e) { /* closed */ } }, 200);
  }
}

/* "…:MediaRenderer:1" asked, "…:MediaRenderer:1" or a later version offered. */
function sameTypeOlder(asked, offered) {
  const a = /^(.*):(\d+)$/.exec(asked), o = /^(.*):(\d+)$/.exec(offered);
  return !!(a && o && a[1] === o[1] && Number(a[2]) <= Number(o[2]));
}

module.exports = { Ssdp, interfaces, addressFor, RENDERER };
