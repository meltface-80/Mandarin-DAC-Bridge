"use strict";
/*
 * xml.js — the little XML the bridge needs: escaping, and reading the
 * arguments of a SOAP request and the fields of a DIDL-Lite item. No parser
 * library: UPnP's documents are flat enough for these few patterns.
 */

const ESC = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&apos;" };
const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) => ESC[c]);

function unesc(s) {
  return String(s == null ? "" : s)
    .replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, "$1")
    .replace(/&(#x[0-9a-f]+|#\d+|lt|gt|quot|apos|amp);/gi, (m, e) => {
      const k = e.toLowerCase();
      if (k === "lt") return "<";
      if (k === "gt") return ">";
      if (k === "quot") return '"';
      if (k === "apos") return "'";
      if (k === "amp") return "&";
      const n = k[1] === "x" ? parseInt(k.slice(2), 16) : parseInt(k.slice(1), 10);
      return Number.isFinite(n) ? String.fromCodePoint(n) : m;
    });
}

/* The direct children of an element's inner text: { name: text }. */
function children(inner) {
  const out = {};
  const re = /<(?:[A-Za-z_][\w.-]*:)?([A-Za-z_][\w.-]*)(?:\s[^>]*?)?(?:\/>|>([\s\S]*?)<\/(?:[A-Za-z_][\w.-]*:)?\1\s*>)/g;
  let m;
  while ((m = re.exec(inner))) out[m[1]] = m[2] == null ? "" : unesc(m[2]);
  return out;
}

/* A SOAP request body → { action, args }. */
function parseSoap(body, soapActionHeader) {
  const text = String(body || "");
  let action = "";
  const h = /#([\w-]+)"?\s*$/.exec(String(soapActionHeader || ""));
  if (h) action = h[1];
  const bm = /<(?:[\w-]+:)?Body[^>]*>([\s\S]*)<\/(?:[\w-]+:)?Body\s*>/i.exec(text);
  const inner = bm ? bm[1] : text;
  const am = /<(?:([\w-]+):)?([A-Za-z_][\w-]*)(?:\s[^>]*)?>([\s\S]*)<\/(?:\1:)?\2\s*>/.exec(inner);
  if (am) {
    if (!action) action = am[2];
    return { action, args: children(am[3]) };
  }
  const empty = /<(?:[\w-]+:)?([A-Za-z_][\w-]*)(?:\s[^>]*)?\/>/.exec(inner);
  if (empty && !action) action = empty[1];
  return { action, args: {} };
}

function attr(tag, name) {
  const m = new RegExp(`\\s${name}\\s*=\\s*("([^"]*)"|'([^']*)')`, "i").exec(tag);
  return m ? unesc(m[2] != null ? m[2] : m[3]) : "";
}

/*
 * DIDL-Lite metadata → what the bridge uses of it: the resource's MIME and
 * duration (for choosing the decoder and showing the time), and the title,
 * artist and album for the page.
 */
function parseDidl(meta, uri) {
  const s = String(meta || "");
  const out = { title: "", artist: "", album: "", art: "", mime: "", duration: 0, protocolInfo: "" };
  if (!s) return out;
  const pick = (re) => { const m = re.exec(s); return m ? unesc(m[1]).trim() : ""; };
  out.title = pick(/<dc:title[^>]*>([\s\S]*?)<\/dc:title>/i);
  out.artist = pick(/<upnp:artist[^>]*>([\s\S]*?)<\/upnp:artist>/i) || pick(/<dc:creator[^>]*>([\s\S]*?)<\/dc:creator>/i);
  out.album = pick(/<upnp:album[^>]*>([\s\S]*?)<\/upnp:album>/i);
  out.art = pick(/<upnp:albumArtURI[^>]*>([\s\S]*?)<\/upnp:albumArtURI>/i);
  // The res that is this URI, else the first.
  const resRe = /<res\b([^>]*)>([\s\S]*?)<\/res>/gi;
  let m, chosen = null;
  while ((m = resRe.exec(s))) {
    if (!chosen) chosen = m;
    if (uri && unesc(m[2]).trim() === uri) { chosen = m; break; }
  }
  if (chosen) {
    out.protocolInfo = attr(chosen[1], "protocolInfo");
    out.mime = (out.protocolInfo.split(":")[2] || "").trim();
    out.duration = hmsToSeconds(attr(chosen[1], "duration"));
  }
  return out;
}

/* "1:02:03.500" → 3723.5; "" → 0. */
function hmsToSeconds(t) {
  const m = /^\s*(?:(\d+):)?(\d+):(\d+(?:\.\d+)?)\s*$/.exec(String(t || ""));
  if (!m) return 0;
  return Number(m[1] || 0) * 3600 + Number(m[2]) * 60 + Number(m[3]);
}

/* 3723.5 → "1:02:03" (UPnP's H+:MM:SS). */
function secondsToHms(s) {
  const t = Math.max(0, Math.floor(Number(s) || 0));
  const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), sec = t % 60;
  return `${h}:${String(m).padStart(2, "0")}:${String(sec).padStart(2, "0")}`;
}

module.exports = { esc, unesc, children, parseSoap, parseDidl, hmsToSeconds, secondsToHms, attr };
