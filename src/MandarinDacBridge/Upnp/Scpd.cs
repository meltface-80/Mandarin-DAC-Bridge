// Scpd.cs — the three services a UPnP AV media renderer offers, as data:
// their state variables and actions. The SCPD documents controllers read are
// made from this, and so is the check that an action exists.
using System.Text;

namespace MandarinDacBridge.Upnp;

internal sealed record StateVar(string Name, string Type, bool Evented = false, string[]? Allowed = null, (int Min, int Max, int? Step)? Range = null);

internal sealed record Service(string Name, string Type, string Id, StateVar[] Vars, Dictionary<string, (string Name, string Dir, string Var)[]> Actions);

internal static class Scpd
{
    public const string AvtType = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string RcsType = "urn:schemas-upnp-org:service:RenderingControl:1";
    public const string CmsType = "urn:schemas-upnp-org:service:ConnectionManager:1";

    private static (string, string, string) In(string n, string v) => (n, "in", v);
    private static (string, string, string) Out(string n, string v) => (n, "out", v);
    private static readonly (string, string, string) Instance = In("InstanceID", "A_ARG_TYPE_InstanceID");
    private static readonly (string, string, string) Channel = In("Channel", "A_ARG_TYPE_Channel");

    public static readonly Dictionary<string, Service> Services = new()
    {
        ["AVTransport"] = new Service("AVTransport", AvtType, "urn:upnp-org:serviceId:AVTransport",
        [
            new("TransportState", "string", Allowed: ["STOPPED", "PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING", "NO_MEDIA_PRESENT"]),
            new("TransportStatus", "string", Allowed: ["OK", "ERROR_OCCURRED"]),
            new("PlaybackStorageMedium", "string", Allowed: ["NETWORK", "NONE"]),
            new("RecordStorageMedium", "string", Allowed: ["NOT_IMPLEMENTED"]),
            new("PossiblePlaybackStorageMedia", "string"),
            new("PossibleRecordStorageMedia", "string"),
            new("CurrentPlayMode", "string", Allowed: ["NORMAL"]),
            new("TransportPlaySpeed", "string", Allowed: ["1"]),
            new("RecordMediumWriteStatus", "string", Allowed: ["NOT_IMPLEMENTED"]),
            new("CurrentRecordQualityMode", "string", Allowed: ["NOT_IMPLEMENTED"]),
            new("PossibleRecordQualityModes", "string"),
            new("NumberOfTracks", "ui4", Range: (0, 1, null)),
            new("CurrentTrack", "ui4", Range: (0, 1, 1)),
            new("CurrentTrackDuration", "string"),
            new("CurrentMediaDuration", "string"),
            new("CurrentTrackMetaData", "string"),
            new("CurrentTrackURI", "string"),
            new("AVTransportURI", "string"),
            new("AVTransportURIMetaData", "string"),
            new("NextAVTransportURI", "string"),
            new("NextAVTransportURIMetaData", "string"),
            new("RelativeTimePosition", "string"),
            new("AbsoluteTimePosition", "string"),
            new("RelativeCounterPosition", "i4"),
            new("AbsoluteCounterPosition", "i4"),
            new("CurrentTransportActions", "string"),
            new("LastChange", "string", Evented: true),
            new("A_ARG_TYPE_SeekMode", "string", Allowed: ["REL_TIME", "ABS_TIME", "TRACK_NR"]),
            new("A_ARG_TYPE_SeekTarget", "string"),
            new("A_ARG_TYPE_InstanceID", "ui4")
        ], new()
        {
            ["SetAVTransportURI"] = [Instance, In("CurrentURI", "AVTransportURI"), In("CurrentURIMetaData", "AVTransportURIMetaData")],
            ["SetNextAVTransportURI"] = [Instance, In("NextURI", "NextAVTransportURI"), In("NextURIMetaData", "NextAVTransportURIMetaData")],
            ["GetMediaInfo"] = [Instance, Out("NrTracks", "NumberOfTracks"), Out("MediaDuration", "CurrentMediaDuration"), Out("CurrentURI", "AVTransportURI"),
                Out("CurrentURIMetaData", "AVTransportURIMetaData"), Out("NextURI", "NextAVTransportURI"), Out("NextURIMetaData", "NextAVTransportURIMetaData"),
                Out("PlayMedium", "PlaybackStorageMedium"), Out("RecordMedium", "RecordStorageMedium"), Out("WriteStatus", "RecordMediumWriteStatus")],
            ["GetTransportInfo"] = [Instance, Out("CurrentTransportState", "TransportState"), Out("CurrentTransportStatus", "TransportStatus"), Out("CurrentSpeed", "TransportPlaySpeed")],
            ["GetPositionInfo"] = [Instance, Out("Track", "CurrentTrack"), Out("TrackDuration", "CurrentTrackDuration"), Out("TrackMetaData", "CurrentTrackMetaData"),
                Out("TrackURI", "CurrentTrackURI"), Out("RelTime", "RelativeTimePosition"), Out("AbsTime", "AbsoluteTimePosition"),
                Out("RelCount", "RelativeCounterPosition"), Out("AbsCount", "AbsoluteCounterPosition")],
            ["GetDeviceCapabilities"] = [Instance, Out("PlayMedia", "PossiblePlaybackStorageMedia"), Out("RecMedia", "PossibleRecordStorageMedia"), Out("RecQualityModes", "PossibleRecordQualityModes")],
            ["GetTransportSettings"] = [Instance, Out("PlayMode", "CurrentPlayMode"), Out("RecQualityMode", "CurrentRecordQualityMode")],
            ["GetCurrentTransportActions"] = [Instance, Out("Actions", "CurrentTransportActions")],
            ["Stop"] = [Instance],
            ["Play"] = [Instance, In("Speed", "TransportPlaySpeed")],
            ["Pause"] = [Instance],
            ["Seek"] = [Instance, In("Unit", "A_ARG_TYPE_SeekMode"), In("Target", "A_ARG_TYPE_SeekTarget")],
            ["Next"] = [Instance],
            ["Previous"] = [Instance],
            ["SetPlayMode"] = [Instance, In("NewPlayMode", "CurrentPlayMode")]
        }),

        ["RenderingControl"] = new Service("RenderingControl", RcsType, "urn:upnp-org:serviceId:RenderingControl",
        [
            new("PresetNameList", "string"),
            new("Mute", "boolean"),
            new("Volume", "ui2", Range: (0, 100, 1)),
            new("VolumeDB", "i2", Range: (-32767, 0, 1)),
            new("LastChange", "string", Evented: true),
            new("A_ARG_TYPE_Channel", "string", Allowed: ["Master"]),
            new("A_ARG_TYPE_InstanceID", "ui4"),
            new("A_ARG_TYPE_PresetName", "string", Allowed: ["FactoryDefaults"])
        ], new()
        {
            ["ListPresets"] = [Instance, Out("CurrentPresetNameList", "PresetNameList")],
            ["SelectPreset"] = [Instance, In("PresetName", "A_ARG_TYPE_PresetName")],
            ["GetMute"] = [Instance, Channel, Out("CurrentMute", "Mute")],
            ["SetMute"] = [Instance, Channel, In("DesiredMute", "Mute")],
            ["GetVolume"] = [Instance, Channel, Out("CurrentVolume", "Volume")],
            ["SetVolume"] = [Instance, Channel, In("DesiredVolume", "Volume")],
            ["GetVolumeDB"] = [Instance, Channel, Out("CurrentVolume", "VolumeDB")],
            ["GetVolumeDBRange"] = [Instance, Channel, Out("MinValue", "VolumeDB"), Out("MaxValue", "VolumeDB")]
        }),

        ["ConnectionManager"] = new Service("ConnectionManager", CmsType, "urn:upnp-org:serviceId:ConnectionManager",
        [
            new("SourceProtocolInfo", "string", Evented: true),
            new("SinkProtocolInfo", "string", Evented: true),
            new("CurrentConnectionIDs", "string", Evented: true),
            new("A_ARG_TYPE_ConnectionStatus", "string", Allowed: ["OK", "ContentFormatMismatch", "InsufficientBandwidth", "UnreliableChannel", "Unknown"]),
            new("A_ARG_TYPE_ConnectionManager", "string"),
            new("A_ARG_TYPE_Direction", "string", Allowed: ["Input", "Output"]),
            new("A_ARG_TYPE_ProtocolInfo", "string"),
            new("A_ARG_TYPE_ConnectionID", "i4"),
            new("A_ARG_TYPE_AVTransportID", "i4"),
            new("A_ARG_TYPE_RcsID", "i4")
        ], new()
        {
            ["GetProtocolInfo"] = [Out("Source", "SourceProtocolInfo"), Out("Sink", "SinkProtocolInfo")],
            ["GetCurrentConnectionIDs"] = [Out("ConnectionIDs", "CurrentConnectionIDs")],
            ["GetCurrentConnectionInfo"] = [In("ConnectionID", "A_ARG_TYPE_ConnectionID"), Out("RcsID", "A_ARG_TYPE_RcsID"),
                Out("AVTransportID", "A_ARG_TYPE_AVTransportID"), Out("ProtocolInfo", "A_ARG_TYPE_ProtocolInfo"),
                Out("PeerConnectionManager", "A_ARG_TYPE_ConnectionManager"), Out("PeerConnectionID", "A_ARG_TYPE_ConnectionID"),
                Out("Direction", "A_ARG_TYPE_Direction"), Out("Status", "A_ARG_TYPE_ConnectionStatus")]
        })
    };

    public static string? Document(string name)
    {
        if (!Services.TryGetValue(name, out var s)) return null;
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion><actionList>");
        foreach (var (action, args) in s.Actions)
        {
            sb.Append($"<action><name>{action}</name><argumentList>");
            foreach (var (n, d, v) in args)
                sb.Append($"<argument><name>{n}</name><direction>{d}</direction><relatedStateVariable>{v}</relatedStateVariable></argument>");
            sb.Append("</argumentList></action>");
        }
        sb.Append("</actionList><serviceStateTable>");
        foreach (var v in s.Vars)
        {
            sb.Append($"<stateVariable sendEvents=\"{(v.Evented ? "yes" : "no")}\"><name>{v.Name}</name><dataType>{v.Type}</dataType>");
            if (v.Allowed != null) sb.Append("<allowedValueList>").Append(string.Concat(v.Allowed.Select(a => $"<allowedValue>{a}</allowedValue>"))).Append("</allowedValueList>");
            if (v.Range is { } r) sb.Append($"<allowedValueRange><minimum>{r.Min}</minimum><maximum>{r.Max}</maximum>{(r.Step is { } st ? $"<step>{st}</step>" : "")}</allowedValueRange>");
            sb.Append("</stateVariable>");
        }
        return sb.Append("</serviceStateTable></scpd>").ToString();
    }
}
