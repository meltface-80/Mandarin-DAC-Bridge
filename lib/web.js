"use strict";
/*
 * web.js — one HTTP server on port 55500 for both faces of the bridge:
 *
 *   /                       the page: a tile per DAC, tap for its capabilities
 *   /api/dacs               what the page shows (JSON)
 *   /api/dacs/<id>/settings POST { enabled, dsd, name }
 *   /api/dacs/<id>/release  POST: let another controller take the DAC now
 *   /api/health
 *   /upnp/<id>/…            each DAC's UPnP device: description, SCPDs,
 *                           control (SOAP) and events (GENA)
 */
const fs = require("fs");
const path = require("path");
const http = require("http");
const XML = require("./xml");
const { scpd, SERVICES } = require("./upnp/scpd");
const { description } = require("./upnp/description");
const Control = require("./upnp/control");

const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
  ".png": "image/png", ".woff2": "font/woff2", ".txt": "text/plain; charset=utf-8", ".svg": "image/svg+xml" };

function body(req, limit = 1 << 20) {
  return new Promise((resolve, reject) => {
    const parts = [];
    let n = 0;
    req.on("data", (c) => { n += c.length; if (n > limit) { reject(new Error("too large")); req.destroy(); } else parts.push(c); });
    req.on("end", () => resolve(Buffer.concat(parts).toString("utf8")));
    req.on("error", reject);
  });
}

function send(res, status, type, text, extra = {}) {
  const b = Buffer.from(text, "utf8");
  res.writeHead(status, Object.assign({ "Content-Type": type, "Content-Length": b.length }, extra));
  res.end(b);
}
const json = (res, status, obj) => send(res, status, "application/json; charset=utf-8", JSON.stringify(obj), { "Cache-Control": "no-store" });

function createServer({ manager, events, config, log = () => {} }) {
  const pub = path.join(config.root, "public");
  const server = http.createServer(async (req, res) => {
    let url;
    try { url = new URL(req.url, "http://x"); } catch (e) { return send(res, 400, "text/plain", "bad request"); }
    const p = url.pathname;
    try {
      // ---------------------------------------------------------------- UPnP
      let m;
      if ((m = /^\/upnp\/([\w-]+)\/description\.xml$/.exec(p))) {
        const b = manager.bridges.get(m[1]);
        if (!b) return send(res, 404, "text/plain", "no such DAC");
        return send(res, 200, 'text/xml; charset="utf-8"', description({
          id: b.id, udn: b.udn, friendlyName: b.friendlyName(), manufacturer: b.dev.manufacturer, modelName: b.dev.name,
          serial: b.id, version: config.version, presentationUrl: "/"
        }));
      }
      if ((m = /^\/upnp\/([\w-]+)\/(\w+)\/(scpd\.xml|control|event)$/.exec(p))) {
        const [, id, service, what] = m;
        const b = manager.bridges.get(id);
        if (!b || !SERVICES[service]) return send(res, 404, "text/plain", "not found");
        if (what === "scpd.xml") return send(res, 200, 'text/xml; charset="utf-8"', scpd(service));
        if (what === "event") {
          if (req.method !== "SUBSCRIBE" && req.method !== "UNSUBSCRIBE") return send(res, 405, "text/plain", "SUBSCRIBE only");
          return events.request(req, res, id, service);
        }
        if (req.method !== "POST") return send(res, 405, "text/plain", "POST only");
        const text = await body(req);
        const { action, args } = XML.parseSoap(text, req.headers.soapaction);
        try {
          const out = await Control.handle(b, service, action, args, req);
          return send(res, 200, 'text/xml; charset="utf-8"', Control.response(SERVICES[service].type, action, out), { EXT: "" });
        } catch (e) {
          const code = e.code || 501;
          if (!e.code) log(`${service}.${action}: ${e.stack || e.message}`);
          return send(res, 500, 'text/xml; charset="utf-8"', Control.fault(code, e.description || e.message || "Action failed"), { EXT: "" });
        }
      }

      // ---------------------------------------------------------------- API
      if (p === "/api/health") return json(res, 200, { ok: true, version: config.version, dacs: manager.devices.size });
      if (p === "/api/dacs" && req.method === "GET") return json(res, 200, manager.view());
      if ((m = /^\/api\/dacs\/([\w-]+)\/(settings|release)$/.exec(p)) && req.method === "POST") {
        if (!manager.devices.has(m[1])) return json(res, 404, { error: "no such DAC" });
        if (m[2] === "release") { manager.release(m[1]); return json(res, 200, { ok: true }); }
        let patch = {};
        try { patch = JSON.parse((await body(req, 4096)) || "{}"); } catch (e) { return json(res, 400, { error: "bad JSON" }); }
        return json(res, 200, manager.setSettings(m[1], patch));
      }

      // ---------------------------------------------------------------- the page
      if (req.method !== "GET" && req.method !== "HEAD") return send(res, 405, "text/plain", "not allowed");
      const rel = p === "/" ? "index.html" : p.replace(/^\/+/, "");
      const file = path.join(pub, rel);
      if (!file.startsWith(pub + path.sep) || rel.includes("..")) return send(res, 404, "text/plain", "not found");
      fs.readFile(file, (err, data) => {
        if (err) return send(res, 404, "text/plain", "not found");
        res.writeHead(200, { "Content-Type": TYPES[path.extname(file)] || "application/octet-stream", "Content-Length": data.length,
          "Cache-Control": /\.(woff2|png)$/.test(file) ? "max-age=86400" : "no-cache" });
        res.end(req.method === "HEAD" ? undefined : data);
      });
    } catch (e) {
      log(`${req.method} ${p}: ${e.message}`);
      if (!res.headersSent) send(res, 500, "text/plain", "error");
    }
  });
  server.keepAliveTimeout = 30000;
  return server;
}

module.exports = { createServer };
