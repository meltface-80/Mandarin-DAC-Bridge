"use strict";
/*
 * arbiter.js — who is in charge of a DAC, and keeping everyone else out.
 *
 * Every control request (SOAP) comes from a controller: Audirvana, Mandarin,
 * or anything else that speaks UPnP. The first to start playing owns the
 * DAC. While it plays, and for a short grace after it stops or pauses (so a
 * controller that loads each track with Stop → SetAVTransportURI → Play
 * keeps it between tracks), any other controller's request to change
 * anything is refused with UPnP error 705, "Transport is locked". Reading
 * the state is open to all.
 *
 * Controllers are told apart by address and User-Agent, and named from the
 * User-Agent or, for Mandarin (which sends none), by its stream URLs.
 */

const GENERIC_UA = /^(cfnetwork|darwin|java|dalvik|okhttp|python|go-http|curl|wget|mozilla|node|libupnp|portable sdk|upnp|dlna)/i;

class Arbiter {
  constructor({ graceMs = 10000, mandarinPort = 3500 } = {}) {
    this.graceMs = graceMs;
    this.mandarinPort = mandarinPort;
    this.owner = null;        // { key, name }
    this.lastActive = 0;
    this.blocked = null;      // { name, action, at }
    this.names = new Map();   // key → name, once known
  }

  /* A request (and the URI it sets, if any) → { key, name, ip }. */
  identify(req, uri) {
    const ip = String((req.socket && req.socket.remoteAddress) || "").replace(/^::ffff:/, "") || "?";
    const ua = String((req.headers && req.headers["user-agent"]) || "").trim();
    const key = ip + "|" + ua;
    let name = nameFromUa(ua);
    if (!name && uri) name = this.nameFromUri(uri);
    if (name) this.names.set(key, name);
    else name = this.names.get(key) || (ua && !GENERIC_UA.test(ua) ? ua.split(/[\/\s(]/)[0] : "") || `a controller at ${ip}`;
    return { key, name, ip };
  }

  nameFromUri(uri) {
    try {
      const u = new URL(uri);
      if (Number(u.port) === this.mandarinPort) return "Mandarin";
      if (/audirvana/i.test(u.pathname)) return "Audirvana";
    } catch (e) { /* not a URL */ }
    return "";
  }

  /* Is the DAC someone else's right now? active: it is playing. */
  lockedFor(caller, active) {
    if (!this.owner || this.owner.key === caller.key) return false;
    if (active) return true;
    return Date.now() - this.lastActive < this.graceMs;
  }

  /* A change request: null if allowed (and the caller now owns the DAC), else the refusal. */
  claim(caller, action, active) {
    if (this.lockedFor(caller, active)) {
      this.blocked = { name: caller.name, action, at: Date.now() };
      return { code: 705, description: `Transport is locked: ${this.owner.name} is using this DAC` };
    }
    if (!this.owner || this.owner.key !== caller.key) this.owner = { key: caller.key, name: caller.name };
    else this.owner.name = caller.name;
    this.lastActive = Date.now();
    return null;
  }

  touch() { this.lastActive = Date.now(); }

  release() { this.owner = null; this.lastActive = 0; }

  view(active) {
    const held = !!this.owner && (active || Date.now() - this.lastActive < this.graceMs);
    return {
      owner: this.owner ? this.owner.name : null,
      locked: held,
      blocked: this.blocked && Date.now() - this.blocked.at < 5 * 60 * 1000 ? this.blocked : null
    };
  }
}

function nameFromUa(ua) {
  if (/audirvana/i.test(ua)) return "Audirvana";
  if (/mandarin|musicd/i.test(ua)) return "Mandarin";
  if (/bubbleupnp/i.test(ua)) return "BubbleUPnP";
  if (/roon/i.test(ua)) return "Roon";
  if (/jriver|media\s*center/i.test(ua)) return "JRiver";
  if (/foobar/i.test(ua)) return "foobar2000";
  if (/kazoo|lumin|linn/i.test(ua)) return ua.split(/[\/\s]/)[0];
  if (/mconnect/i.test(ua)) return "mconnect";
  return "";
}

module.exports = { Arbiter, nameFromUa };
