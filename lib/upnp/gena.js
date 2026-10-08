"use strict";
/*
 * gena.js — UPnP eventing: controllers SUBSCRIBE to a service and are sent
 * a NOTIFY whenever its state changes (AVTransport and RenderingControl in
 * a LastChange document, ConnectionManager plainly). This is how Audirvana
 * and Mandarin see a track end or the next one begin without asking.
 */
const http = require("http");
const crypto = require("crypto");
const XML = require("../xml");

const TIMEOUT_S = 1800;
const LASTCHANGE_NS = { AVTransport: "urn:schemas-upnp-org:metadata-1-0/AVT/", RenderingControl: "urn:schemas-upnp-org:metadata-1-0/RCS/" };

function lastChange(service, vars) {
  const inner = Object.entries(vars).map(([k, v]) => {
    const ch = service === "RenderingControl" && /^(Volume|Mute|VolumeDB)$/.test(k) ? ' channel="Master"' : "";
    return `<${k}${ch} val="${XML.esc(v)}"/>`;
  }).join("");
  return `<Event xmlns="${LASTCHANGE_NS[service]}"><InstanceID val="0">${inner}</InstanceID></Event>`;
}

function propertySet(service, vars) {
  const props = LASTCHANGE_NS[service]
    ? `<e:property><LastChange>${XML.esc(lastChange(service, vars))}</LastChange></e:property>`
    : Object.entries(vars).map(([k, v]) => `<e:property><${k}>${XML.esc(v)}</${k}></e:property>`).join("");
  return `<?xml version="1.0" encoding="utf-8"?>\n<e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0">${props}</e:propertyset>`;
}

class Events {
  /* state(bridgeId, service) → the evented variables now. */
  constructor({ state, log = () => {} }) {
    this.state = state;
    this.log = log;
    this.subs = new Map();      // sid → { bridge, service, urls, seq, expires, last }
    this.pending = new Map();   // bridge|service → timer
  }

  /* SUBSCRIBE / UNSUBSCRIBE on bridge/service's event URL. */
  request(req, res, bridgeId, service) {
    const h = req.headers;
    if (req.method === "UNSUBSCRIBE") {
      const ok = this.subs.delete(String(h.sid || ""));
      res.writeHead(ok ? 200 : 412, { "Content-Length": 0 });
      return res.end();
    }
    const timeout = Math.min(TIMEOUT_S, Number((/Second-(\d+)/i.exec(String(h.timeout || "")) || [])[1]) || TIMEOUT_S);
    if (h.sid) {
      const s = this.subs.get(String(h.sid));
      if (!s || s.bridge !== bridgeId || s.service !== service) { res.writeHead(412, { "Content-Length": 0 }); return res.end(); }
      s.expires = Date.now() + timeout * 1000;
      res.writeHead(200, { SID: h.sid, TIMEOUT: `Second-${timeout}`, "Content-Length": 0 });
      return res.end();
    }
    const urls = [...String(h.callback || "").matchAll(/<([^>]+)>/g)].map((m) => m[1]).filter((u) => /^http:\/\//i.test(u));
    if (!urls.length || String(h.nt || "").toLowerCase() !== "upnp:event") { res.writeHead(412, { "Content-Length": 0 }); return res.end(); }
    const sid = "uuid:" + crypto.randomUUID();
    const sub = { bridge: bridgeId, service, urls, seq: 0, expires: Date.now() + timeout * 1000, last: {} };
    this.subs.set(sid, sub);
    res.writeHead(200, { SID: sid, TIMEOUT: `Second-${timeout}`, "Content-Length": 0 });
    res.end();
    // The first event carries everything.
    setTimeout(() => this.send(sid, sub, this.state(bridgeId, service), true), 50);
  }

  /* Something about this bridge changed: tell its subscribers what, a moment later (changes come in bursts). */
  changed(bridgeId) {
    for (const service of ["AVTransport", "RenderingControl", "ConnectionManager"]) {
      const key = bridgeId + "|" + service;
      if (this.pending.has(key)) continue;
      this.pending.set(key, setTimeout(() => {
        this.pending.delete(key);
        const now = this.state(bridgeId, service);
        if (!now) return;
        for (const [sid, sub] of this.subs) {
          if (sub.bridge !== bridgeId || sub.service !== service) continue;
          if (sub.expires < Date.now()) { this.subs.delete(sid); continue; }
          const diff = {};
          for (const [k, v] of Object.entries(now)) if (sub.last[k] !== v) diff[k] = v;
          if (Object.keys(diff).length) this.send(sid, sub, diff, false);
        }
      }, 150));
    }
  }

  /* The bridge has gone: its subscriptions too. */
  drop(bridgeId) {
    for (const [sid, sub] of this.subs) if (sub.bridge === bridgeId) this.subs.delete(sid);
  }

  send(sid, sub, vars, initial) {
    if (!vars || !this.subs.has(sid)) return;
    Object.assign(sub.last, vars);
    const body = Buffer.from(propertySet(sub.service, vars), "utf8");
    const seq = sub.seq;
    sub.seq = sub.seq >= 4294967295 ? 1 : sub.seq + 1;
    const tryUrl = (i) => {
      if (i >= sub.urls.length) return;
      let u;
      try { u = new URL(sub.urls[i]); } catch (e) { return tryUrl(i + 1); }
      const req = http.request({ hostname: u.hostname, port: u.port || 80, path: u.pathname + u.search, method: "NOTIFY", timeout: 4000,
        headers: { "Content-Type": 'text/xml; charset="utf-8"', NT: "upnp:event", NTS: "upnp:propchange", SID: sid, SEQ: String(seq), "Content-Length": body.length } },
      (res) => {
        res.resume();
        if (res.statusCode === 412) this.subs.delete(sid);
      });
      req.on("timeout", () => req.destroy());
      req.on("error", () => { if (i + 1 < sub.urls.length) tryUrl(i + 1); else if (!initial) this.log(`event to ${u.host} failed`); });
      req.end(body);
    };
    tryUrl(0);
  }
}

module.exports = { Events, lastChange, propertySet };
