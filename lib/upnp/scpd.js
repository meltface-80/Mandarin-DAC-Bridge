"use strict";
/*
 * scpd.js — the three services a UPnP AV media renderer offers, as data:
 * their state variables and actions. The SCPD documents controllers read
 * are made from this, and so is the check that an action exists.
 */

const AVT = "urn:schemas-upnp-org:service:AVTransport:1";
const RCS = "urn:schemas-upnp-org:service:RenderingControl:1";
const CMS = "urn:schemas-upnp-org:service:ConnectionManager:1";

const ST = (name, type, evented, allowed, extra) => ({ name, type, evented: !!evented, allowed: allowed || null, extra: extra || null });

const SERVICES = {
  AVTransport: {
    type: AVT,
    id: "urn:upnp-org:serviceId:AVTransport",
    vars: [
      ST("TransportState", "string", false, ["STOPPED", "PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING", "NO_MEDIA_PRESENT"]),
      ST("TransportStatus", "string", false, ["OK", "ERROR_OCCURRED"]),
      ST("PlaybackStorageMedium", "string", false, ["NETWORK", "NONE"]),
      ST("RecordStorageMedium", "string", false, ["NOT_IMPLEMENTED"]),
      ST("PossiblePlaybackStorageMedia", "string"),
      ST("PossibleRecordStorageMedia", "string"),
      ST("CurrentPlayMode", "string", false, ["NORMAL"]),
      ST("TransportPlaySpeed", "string", false, ["1"]),
      ST("RecordMediumWriteStatus", "string", false, ["NOT_IMPLEMENTED"]),
      ST("CurrentRecordQualityMode", "string", false, ["NOT_IMPLEMENTED"]),
      ST("PossibleRecordQualityModes", "string"),
      ST("NumberOfTracks", "ui4", false, null, { min: 0, max: 1 }),
      ST("CurrentTrack", "ui4", false, null, { min: 0, max: 1, step: 1 }),
      ST("CurrentTrackDuration", "string"),
      ST("CurrentMediaDuration", "string"),
      ST("CurrentTrackMetaData", "string"),
      ST("CurrentTrackURI", "string"),
      ST("AVTransportURI", "string"),
      ST("AVTransportURIMetaData", "string"),
      ST("NextAVTransportURI", "string"),
      ST("NextAVTransportURIMetaData", "string"),
      ST("RelativeTimePosition", "string"),
      ST("AbsoluteTimePosition", "string"),
      ST("RelativeCounterPosition", "i4"),
      ST("AbsoluteCounterPosition", "i4"),
      ST("CurrentTransportActions", "string"),
      ST("LastChange", "string", true),
      ST("A_ARG_TYPE_SeekMode", "string", false, ["REL_TIME", "ABS_TIME", "TRACK_NR"]),
      ST("A_ARG_TYPE_SeekTarget", "string"),
      ST("A_ARG_TYPE_InstanceID", "ui4")
    ],
    actions: {
      SetAVTransportURI: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["CurrentURI", "in", "AVTransportURI"], ["CurrentURIMetaData", "in", "AVTransportURIMetaData"]],
      SetNextAVTransportURI: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["NextURI", "in", "NextAVTransportURI"], ["NextURIMetaData", "in", "NextAVTransportURIMetaData"]],
      GetMediaInfo: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["NrTracks", "out", "NumberOfTracks"], ["MediaDuration", "out", "CurrentMediaDuration"],
        ["CurrentURI", "out", "AVTransportURI"], ["CurrentURIMetaData", "out", "AVTransportURIMetaData"], ["NextURI", "out", "NextAVTransportURI"],
        ["NextURIMetaData", "out", "NextAVTransportURIMetaData"], ["PlayMedium", "out", "PlaybackStorageMedium"], ["RecordMedium", "out", "RecordStorageMedium"],
        ["WriteStatus", "out", "RecordMediumWriteStatus"]],
      GetTransportInfo: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["CurrentTransportState", "out", "TransportState"],
        ["CurrentTransportStatus", "out", "TransportStatus"], ["CurrentSpeed", "out", "TransportPlaySpeed"]],
      GetPositionInfo: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Track", "out", "CurrentTrack"], ["TrackDuration", "out", "CurrentTrackDuration"],
        ["TrackMetaData", "out", "CurrentTrackMetaData"], ["TrackURI", "out", "CurrentTrackURI"], ["RelTime", "out", "RelativeTimePosition"],
        ["AbsTime", "out", "AbsoluteTimePosition"], ["RelCount", "out", "RelativeCounterPosition"], ["AbsCount", "out", "AbsoluteCounterPosition"]],
      GetDeviceCapabilities: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["PlayMedia", "out", "PossiblePlaybackStorageMedia"],
        ["RecMedia", "out", "PossibleRecordStorageMedia"], ["RecQualityModes", "out", "PossibleRecordQualityModes"]],
      GetTransportSettings: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["PlayMode", "out", "CurrentPlayMode"], ["RecQualityMode", "out", "CurrentRecordQualityMode"]],
      GetCurrentTransportActions: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Actions", "out", "CurrentTransportActions"]],
      Stop: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"]],
      Play: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Speed", "in", "TransportPlaySpeed"]],
      Pause: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"]],
      Seek: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Unit", "in", "A_ARG_TYPE_SeekMode"], ["Target", "in", "A_ARG_TYPE_SeekTarget"]],
      Next: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"]],
      Previous: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"]],
      SetPlayMode: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["NewPlayMode", "in", "CurrentPlayMode"]]
    }
  },

  RenderingControl: {
    type: RCS,
    id: "urn:upnp-org:serviceId:RenderingControl",
    vars: [
      ST("PresetNameList", "string"),
      ST("Mute", "boolean"),
      ST("Volume", "ui2", false, null, { min: 0, max: 100, step: 1 }),
      ST("VolumeDB", "i2", false, null, { min: 0, max: 0, step: 1 }),
      ST("LastChange", "string", true),
      ST("A_ARG_TYPE_Channel", "string", false, ["Master"]),
      ST("A_ARG_TYPE_InstanceID", "ui4"),
      ST("A_ARG_TYPE_PresetName", "string", false, ["FactoryDefaults"])
    ],
    actions: {
      ListPresets: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["CurrentPresetNameList", "out", "PresetNameList"]],
      SelectPreset: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["PresetName", "in", "A_ARG_TYPE_PresetName"]],
      GetMute: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Channel", "in", "A_ARG_TYPE_Channel"], ["CurrentMute", "out", "Mute"]],
      SetMute: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Channel", "in", "A_ARG_TYPE_Channel"], ["DesiredMute", "in", "Mute"]],
      GetVolume: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Channel", "in", "A_ARG_TYPE_Channel"], ["CurrentVolume", "out", "Volume"]],
      SetVolume: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Channel", "in", "A_ARG_TYPE_Channel"], ["DesiredVolume", "in", "Volume"]],
      GetVolumeDB: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Channel", "in", "A_ARG_TYPE_Channel"], ["CurrentVolume", "out", "VolumeDB"]],
      GetVolumeDBRange: [["InstanceID", "in", "A_ARG_TYPE_InstanceID"], ["Channel", "in", "A_ARG_TYPE_Channel"], ["MinValue", "out", "VolumeDB"], ["MaxValue", "out", "VolumeDB"]]
    }
  },

  ConnectionManager: {
    type: CMS,
    id: "urn:upnp-org:serviceId:ConnectionManager",
    vars: [
      ST("SourceProtocolInfo", "string", true),
      ST("SinkProtocolInfo", "string", true),
      ST("CurrentConnectionIDs", "string", true),
      ST("A_ARG_TYPE_ConnectionStatus", "string", false, ["OK", "ContentFormatMismatch", "InsufficientBandwidth", "UnreliableChannel", "Unknown"]),
      ST("A_ARG_TYPE_ConnectionManager", "string"),
      ST("A_ARG_TYPE_Direction", "string", false, ["Input", "Output"]),
      ST("A_ARG_TYPE_ProtocolInfo", "string"),
      ST("A_ARG_TYPE_ConnectionID", "i4"),
      ST("A_ARG_TYPE_AVTransportID", "i4"),
      ST("A_ARG_TYPE_RcsID", "i4")
    ],
    actions: {
      GetProtocolInfo: [["Source", "out", "SourceProtocolInfo"], ["Sink", "out", "SinkProtocolInfo"]],
      GetCurrentConnectionIDs: [["ConnectionIDs", "out", "CurrentConnectionIDs"]],
      GetCurrentConnectionInfo: [["ConnectionID", "in", "A_ARG_TYPE_ConnectionID"], ["RcsID", "out", "A_ARG_TYPE_RcsID"],
        ["AVTransportID", "out", "A_ARG_TYPE_AVTransportID"], ["ProtocolInfo", "out", "A_ARG_TYPE_ProtocolInfo"],
        ["PeerConnectionManager", "out", "A_ARG_TYPE_ConnectionManager"], ["PeerConnectionID", "out", "A_ARG_TYPE_ConnectionID"],
        ["Direction", "out", "A_ARG_TYPE_Direction"], ["Status", "out", "A_ARG_TYPE_ConnectionStatus"]]
    }
  }
};

function scpd(name) {
  const s = SERVICES[name];
  if (!s) return null;
  const actions = Object.entries(s.actions).map(([a, args]) =>
    `<action><name>${a}</name><argumentList>${args.map(([n, d, v]) =>
      `<argument><name>${n}</name><direction>${d}</direction><relatedStateVariable>${v}</relatedStateVariable></argument>`).join("")}</argumentList></action>`).join("");
  const vars = s.vars.map((v) => {
    let x = `<stateVariable sendEvents="${v.evented ? "yes" : "no"}"><name>${v.name}</name><dataType>${v.type}</dataType>`;
    if (v.allowed) x += `<allowedValueList>${v.allowed.map((a) => `<allowedValue>${a}</allowedValue>`).join("")}</allowedValueList>`;
    if (v.extra) x += `<allowedValueRange><minimum>${v.extra.min}</minimum><maximum>${v.extra.max}</maximum>${v.extra.step != null ? `<step>${v.extra.step}</step>` : ""}</allowedValueRange>`;
    return x + "</stateVariable>";
  }).join("");
  return `<?xml version="1.0" encoding="utf-8"?>\n<scpd xmlns="urn:schemas-upnp-org:service-1-0"><specVersion><major>1</major><minor>0</minor></specVersion>` +
    `<actionList>${actions}</actionList><serviceStateTable>${vars}</serviceStateTable></scpd>`;
}

module.exports = { SERVICES, scpd, AVT, RCS, CMS };
