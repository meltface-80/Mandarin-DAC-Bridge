"use strict";
/*
 * control.js — the SOAP actions of one DAC's renderer, and the state its
 * events carry. Every request that changes something goes past the arbiter
 * first (lib/arbiter.js): a controller that doesn't own the DAC while it is
 * in use is refused with 705.
 */
const XML = require("../xml");
const { SERVICES } = require("./scpd");
const { UPnPError } = require("../renderer");

const CHANGES = new Set(["SetAVTransportURI", "SetNextAVTransportURI", "Play", "Pause", "Stop", "Seek", "Next", "Previous",
  "SetPlayMode", "SetVolume", "SetMute", "SelectPreset"]);

/* What the renderer says it plays: the formats ffmpeg reads, DSD, and raw PCM at the DAC's rates. */
function protocolInfo(rates) {
  const mimes = ["audio/flac", "audio/x-flac", "audio/wav", "audio/wave", "audio/x-wav", "audio/aiff", "audio/x-aiff",
    "audio/dsf", "audio/x-dsf", "audio/dff", "audio/x-dff", "audio/x-dsd", "audio/mp4", "audio/x-m4a", "audio/m4a",
    "audio/mpeg", "audio/mp3", "audio/aac", "audio/ogg", "audio/x-ogg", "audio/opus", "application/ogg"];
  const out = mimes.map((m) => `http-get:*:${m}:*`);
  for (const r of rates && rates.length ? rates : [44100, 48000, 88200, 96000, 176400, 192000]) {
    for (const b of [16, 24]) out.push(`http-get:*:audio/L${b};rate=${r};channels=2:${b === 16 ? "DLNA.ORG_PN=LPCM" : "*"}`);
  }
  return out.join(",");
}

function actionsFor(r) {
  switch (r.transport) {
    case "PLAYING": return "Pause,Stop,Seek";
    case "PAUSED_PLAYBACK": return "Play,Stop,Seek";
    case "TRANSITIONING": return "Stop";
    case "STOPPED": return "Play,Seek";
    default: return "";
  }
}

/* Evented AVTransport variables. */
function avtState(r) {
  const cur = r.cur;
  return {
    TransportState: r.transport,
    TransportStatus: r.status,
    PlaybackStorageMedium: cur ? "NETWORK" : "NONE",
    RecordStorageMedium: "NOT_IMPLEMENTED",
    PossiblePlaybackStorageMedia: "NETWORK",
    PossibleRecordStorageMedia: "NOT_IMPLEMENTED",
    CurrentPlayMode: "NORMAL",
    TransportPlaySpeed: "1",
    RecordMediumWriteStatus: "NOT_IMPLEMENTED",
    CurrentRecordQualityMode: "NOT_IMPLEMENTED",
    PossibleRecordQualityModes: "NOT_IMPLEMENTED",
    NumberOfTracks: cur ? "1" : "0",
    CurrentTrack: cur ? "1" : "0",
    CurrentTrackDuration: XML.secondsToHms(cur ? cur.duration : 0),
    CurrentMediaDuration: XML.secondsToHms(cur ? cur.duration : 0),
    CurrentTrackMetaData: cur ? cur.meta : "",
    CurrentTrackURI: cur ? cur.uri : "",
    AVTransportURI: cur ? cur.uri : "",
    AVTransportURIMetaData: cur ? cur.meta : "",
    NextAVTransportURI: r.next ? r.next.uri : "",
    NextAVTransportURIMetaData: r.next ? r.next.meta : "",
    CurrentTransportActions: actionsFor(r)
  };
}

function rcsState(r) {
  return { Volume: "100", Mute: r.muted ? "1" : "0", VolumeDB: "0", PresetNameList: "FactoryDefaults" };
}

function cmsState(bridge) {
  return { SourceProtocolInfo: "", SinkProtocolInfo: bridge.protocolInfo(), CurrentConnectionIDs: "0" };
}

function seekTarget(unit, target) {
  const u = String(unit || "").toUpperCase();
  if (u === "REL_TIME" || u === "ABS_TIME") {
    const s = XML.hmsToSeconds(target);
    if (!/\d/.test(String(target))) throw new UPnPError(711, "Illegal seek target");
    return s;
  }
  if (u === "TRACK_NR") {
    if (Number(target) !== 1) throw new UPnPError(711, "Illegal seek target");
    return 0;
  }
  throw new UPnPError(710, "Seek mode not supported");
}

/*
 * One action → its output arguments ({ name: value }), or a thrown
 * UPnPError. ctx.req is the HTTP request (who is asking).
 */
async function handle(bridge, service, action, args, req) {
  const svc = SERVICES[service];
  if (!svc || !svc.actions[action]) throw new UPnPError(401, "Invalid Action");
  if ("InstanceID" in args && String(args.InstanceID).trim() !== "0") throw new UPnPError(718, "Invalid InstanceID");
  const r = bridge.renderer;

  if (CHANGES.has(action)) {
    const uri = action === "SetAVTransportURI" ? args.CurrentURI : action === "SetNextAVTransportURI" ? args.NextURI : "";
    const caller = bridge.arbiter.identify(req, uri);
    const refusal = bridge.arbiter.claim(caller, action, r.isActive());
    if (refusal) {
      bridge.log(`refused ${caller.name}'s ${action}: ${bridge.arbiter.owner.name} is using the DAC`);
      bridge.emit("change");
      throw new UPnPError(refusal.code, refusal.description);
    }
    if (action === "SetAVTransportURI" || action === "Play") bridge.log(`${caller.name}: ${action}${uri ? " " + uri.slice(0, 120) : ""}`);
  }

  switch (action) {
    // ---- AVTransport
    case "SetAVTransportURI": await r.setUri(String(args.CurrentURI || "").trim(), args.CurrentURIMetaData || ""); return {};
    case "SetNextAVTransportURI": await r.setNext(String(args.NextURI || "").trim(), args.NextURIMetaData || ""); return {};
    case "Play": await r.play(); return {};
    case "Pause": await r.pause(); return {};
    case "Stop": await r.stop(); return {};
    case "Seek": await r.seek(seekTarget(args.Unit, args.Target)); return {};
    case "Next":
    case "Previous": throw new UPnPError(701, "Transition not available");
    case "SetPlayMode":
      if (String(args.NewPlayMode || "NORMAL").toUpperCase() !== "NORMAL") throw new UPnPError(712, "Play mode not supported");
      return {};
    case "GetMediaInfo": {
      const s = avtState(r);
      return { NrTracks: s.NumberOfTracks, MediaDuration: s.CurrentMediaDuration, CurrentURI: s.AVTransportURI,
        CurrentURIMetaData: s.AVTransportURIMetaData, NextURI: s.NextAVTransportURI, NextURIMetaData: s.NextAVTransportURIMetaData,
        PlayMedium: s.PlaybackStorageMedium, RecordMedium: "NOT_IMPLEMENTED", WriteStatus: "NOT_IMPLEMENTED" };
    }
    case "GetTransportInfo": return { CurrentTransportState: r.transport, CurrentTransportStatus: r.status, CurrentSpeed: "1" };
    case "GetPositionInfo": {
      const cur = r.cur;
      const rel = XML.secondsToHms(r.position());
      return { Track: cur ? "1" : "0", TrackDuration: XML.secondsToHms(cur ? cur.duration : 0), TrackMetaData: cur ? cur.meta : "",
        TrackURI: cur ? cur.uri : "", RelTime: rel, AbsTime: rel, RelCount: "2147483647", AbsCount: "2147483647" };
    }
    case "GetDeviceCapabilities": return { PlayMedia: "NETWORK", RecMedia: "NOT_IMPLEMENTED", RecQualityModes: "NOT_IMPLEMENTED" };
    case "GetTransportSettings": return { PlayMode: "NORMAL", RecQualityMode: "NOT_IMPLEMENTED" };
    case "GetCurrentTransportActions": return { Actions: actionsFor(r) };

    // ---- RenderingControl: the volume is fixed at 100 (bit-perfect); mute works.
    case "ListPresets": return { CurrentPresetNameList: "FactoryDefaults" };
    case "SelectPreset": return {};
    case "GetMute": return { CurrentMute: r.muted ? "1" : "0" };
    case "SetMute": r.setMute(/^(1|true|yes)$/i.test(String(args.DesiredMute))); return {};
    case "GetVolume": return { CurrentVolume: "100" };
    case "SetVolume": r.setVolume(Number(args.DesiredVolume)); return {};
    case "GetVolumeDB": return { CurrentVolume: "0" };
    case "GetVolumeDBRange": return { MinValue: "0", MaxValue: "0" };

    // ---- ConnectionManager
    case "GetProtocolInfo": return { Source: "", Sink: bridge.protocolInfo() };
    case "GetCurrentConnectionIDs": return { ConnectionIDs: "0" };
    case "GetCurrentConnectionInfo":
      if (String(args.ConnectionID || "0").trim() !== "0") throw new UPnPError(706, "Invalid connection reference");
      return { RcsID: "0", AVTransportID: "0", ProtocolInfo: "", PeerConnectionManager: "", PeerConnectionID: "-1", Direction: "Input", Status: "OK" };
    default: throw new UPnPError(401, "Invalid Action");
  }
}

function envelope(inner) {
  return `<?xml version="1.0" encoding="utf-8"?>\n<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" ` +
    `s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body>${inner}</s:Body></s:Envelope>`;
}

function response(serviceType, action, out) {
  const args = Object.entries(out).map(([k, v]) => `<${k}>${XML.esc(v)}</${k}>`).join("");
  return envelope(`<u:${action}Response xmlns:u="${serviceType}">${args}</u:${action}Response>`);
}

function fault(code, description) {
  return envelope(`<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>` +
    `<UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>${code}</errorCode>` +
    `<errorDescription>${XML.esc(description)}</errorDescription></UPnPError></detail></s:Fault>`);
}

module.exports = { handle, response, fault, protocolInfo, avtState, rcsState, cmsState, actionsFor, CHANGES };
