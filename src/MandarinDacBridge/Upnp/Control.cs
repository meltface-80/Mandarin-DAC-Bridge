// Control.cs — the SOAP actions of one DAC's renderer, and the state its
// events carry. Every request that changes something goes past the arbiter
// first: a controller that doesn't own the DAC while it is in use is refused
// with 705.
using System.Text;

namespace MandarinDacBridge.Upnp;

internal static class Control
{
    private static readonly HashSet<string> Changes =
        ["SetAVTransportURI", "SetNextAVTransportURI", "Play", "Pause", "Stop", "Seek", "Next", "Previous", "SetPlayMode", "SetVolume", "SetMute", "SelectPreset"];

    private static readonly string[] Mimes =
    [
        "audio/flac", "audio/x-flac", "audio/wav", "audio/wave", "audio/x-wav", "audio/aiff", "audio/x-aiff",
        "audio/dsf", "audio/x-dsf", "audio/dff", "audio/x-dff", "audio/x-dsd", "audio/mp4", "audio/x-m4a", "audio/m4a",
        "audio/mpeg", "audio/mp3", "audio/aac", "audio/ogg", "audio/x-ogg", "audio/opus", "application/ogg"
    ];

    // What the renderer says it plays: what ffmpeg reads, DSD, and raw PCM at the DAC's rates.
    public static string ProtocolInfo(int[] rates)
    {
        var o = Mimes.Select(m => $"http-get:*:{m}:*").ToList();
        foreach (var r in rates.Length > 0 ? rates : [44100, 48000, 88200, 96000, 176400, 192000])
            foreach (var b in new[] { 16, 24 })
                o.Add($"http-get:*:audio/L{b};rate={r};channels=2:{(b == 16 ? "DLNA.ORG_PN=LPCM" : "*")}");
        return string.Join(",", o);
    }

    public static string ActionsFor(Renderer r) => r.Transport switch
    {
        "PLAYING" => "Pause,Stop,Seek",
        "PAUSED_PLAYBACK" => "Play,Stop,Seek",
        "TRANSITIONING" => "Stop",
        "STOPPED" => "Play,Seek",
        _ => ""
    };

    // Evented AVTransport variables.
    public static Dictionary<string, string> AvtState(Renderer r)
    {
        var cur = r.Current;
        var next = r.Next;
        var dur = Xml.SecondsToHms(cur?.Duration ?? 0);
        return new()
        {
            ["TransportState"] = r.Transport,
            ["TransportStatus"] = r.Status,
            ["PlaybackStorageMedium"] = cur != null ? "NETWORK" : "NONE",
            ["RecordStorageMedium"] = "NOT_IMPLEMENTED",
            ["PossiblePlaybackStorageMedia"] = "NETWORK",
            ["PossibleRecordStorageMedia"] = "NOT_IMPLEMENTED",
            ["CurrentPlayMode"] = "NORMAL",
            ["TransportPlaySpeed"] = "1",
            ["RecordMediumWriteStatus"] = "NOT_IMPLEMENTED",
            ["CurrentRecordQualityMode"] = "NOT_IMPLEMENTED",
            ["PossibleRecordQualityModes"] = "NOT_IMPLEMENTED",
            ["NumberOfTracks"] = cur != null ? "1" : "0",
            ["CurrentTrack"] = cur != null ? "1" : "0",
            ["CurrentTrackDuration"] = dur,
            ["CurrentMediaDuration"] = dur,
            ["CurrentTrackMetaData"] = cur?.Meta ?? "",
            ["CurrentTrackURI"] = cur?.Uri ?? "",
            ["AVTransportURI"] = cur?.Uri ?? "",
            ["AVTransportURIMetaData"] = cur?.Meta ?? "",
            ["NextAVTransportURI"] = next?.Uri ?? "",
            ["NextAVTransportURIMetaData"] = next?.Meta ?? "",
            ["CurrentTransportActions"] = ActionsFor(r)
        };
    }

    public static Dictionary<string, string> RcsState(Renderer r) =>
        new() { ["Volume"] = "100", ["Mute"] = r.Muted ? "1" : "0", ["VolumeDB"] = "0", ["PresetNameList"] = "FactoryDefaults" };

    public static Dictionary<string, string> CmsState(Bridge b) =>
        new() { ["SourceProtocolInfo"] = "", ["SinkProtocolInfo"] = b.ProtocolInfo(), ["CurrentConnectionIDs"] = "0" };

    private static double SeekTarget(string unit, string target)
    {
        switch (unit.ToUpperInvariant())
        {
            case "REL_TIME":
            case "ABS_TIME":
                if (!target.Any(char.IsDigit)) throw new UpnpException(711, "Illegal seek target");
                return Xml.HmsToSeconds(target);
            case "TRACK_NR":
                if (target.Trim() != "1") throw new UpnpException(711, "Illegal seek target");
                return 0;
            default: throw new UpnpException(710, "Seek mode not supported");
        }
    }

    // One action → its output arguments, or an UpnpException.
    public static async Task<Dictionary<string, string>> Handle(Bridge b, string service, string action, Dictionary<string, string> args, string ip, string userAgent)
    {
        if (!Scpd.Services.TryGetValue(service, out var svc) || !svc.Actions.ContainsKey(action)) throw new UpnpException(401, "Invalid Action");
        string Arg(string k) => args.TryGetValue(k, out var v) ? v : "";
        if (args.ContainsKey("InstanceID") && Arg("InstanceID").Trim() != "0") throw new UpnpException(718, "Invalid InstanceID");
        var r = b.Renderer;

        if (Changes.Contains(action))
        {
            var uri = action == "SetAVTransportURI" ? Arg("CurrentURI") : action == "SetNextAVTransportURI" ? Arg("NextURI") : null;
            var caller = b.Arbiter.Identify(ip, userAgent, uri);
            var refusal = b.Arbiter.Claim(caller, action, r.IsActive);
            if (refusal != null)
            {
                b.Log($"refused {caller.Name}'s {action}: {b.Arbiter.Owner?.Name} is using the DAC");
                b.Notify();
                throw new UpnpException(705, refusal);
            }
            if (action is "SetAVTransportURI" or "Play") b.Log($"{caller.Name}: {action}{(uri != null ? " " + uri[..Math.Min(120, uri.Length)] : "")}");
        }

        var none = new Dictionary<string, string>();
        switch (action)
        {
            // ---- AVTransport
            case "SetAVTransportURI": await r.SetUri(Arg("CurrentURI").Trim(), Arg("CurrentURIMetaData")); return none;
            case "SetNextAVTransportURI": await r.SetNext(Arg("NextURI").Trim(), Arg("NextURIMetaData")); return none;
            case "Play": await r.Play(); return none;
            case "Pause": await r.Pause(); return none;
            case "Stop": await r.Stop(); return none;
            case "Seek": await r.Seek(SeekTarget(Arg("Unit"), Arg("Target"))); return none;
            case "Next":
            case "Previous": throw new UpnpException(701, "Transition not available");
            case "SetPlayMode":
                if (!string.Equals(Arg("NewPlayMode") is "" ? "NORMAL" : Arg("NewPlayMode"), "NORMAL", StringComparison.OrdinalIgnoreCase))
                    throw new UpnpException(712, "Play mode not supported");
                return none;
            case "GetMediaInfo":
            {
                var s = AvtState(r);
                return new()
                {
                    ["NrTracks"] = s["NumberOfTracks"], ["MediaDuration"] = s["CurrentMediaDuration"], ["CurrentURI"] = s["AVTransportURI"],
                    ["CurrentURIMetaData"] = s["AVTransportURIMetaData"], ["NextURI"] = s["NextAVTransportURI"], ["NextURIMetaData"] = s["NextAVTransportURIMetaData"],
                    ["PlayMedium"] = s["PlaybackStorageMedium"], ["RecordMedium"] = "NOT_IMPLEMENTED", ["WriteStatus"] = "NOT_IMPLEMENTED"
                };
            }
            case "GetTransportInfo": return new() { ["CurrentTransportState"] = r.Transport, ["CurrentTransportStatus"] = r.Status, ["CurrentSpeed"] = "1" };
            case "GetPositionInfo":
            {
                var cur = r.Current;
                var rel = Xml.SecondsToHms(r.Position());
                return new()
                {
                    ["Track"] = cur != null ? "1" : "0", ["TrackDuration"] = Xml.SecondsToHms(cur?.Duration ?? 0), ["TrackMetaData"] = cur?.Meta ?? "",
                    ["TrackURI"] = cur?.Uri ?? "", ["RelTime"] = rel, ["AbsTime"] = rel, ["RelCount"] = "2147483647", ["AbsCount"] = "2147483647"
                };
            }
            case "GetDeviceCapabilities": return new() { ["PlayMedia"] = "NETWORK", ["RecMedia"] = "NOT_IMPLEMENTED", ["RecQualityModes"] = "NOT_IMPLEMENTED" };
            case "GetTransportSettings": return new() { ["PlayMode"] = "NORMAL", ["RecQualityMode"] = "NOT_IMPLEMENTED" };
            case "GetCurrentTransportActions": return new() { ["Actions"] = ActionsFor(r) };

            // ---- RenderingControl: the volume is fixed at 100 (bit-perfect); mute works.
            case "ListPresets": return new() { ["CurrentPresetNameList"] = "FactoryDefaults" };
            case "SelectPreset": return none;
            case "GetMute": return new() { ["CurrentMute"] = r.Muted ? "1" : "0" };
            case "SetMute": r.SetMute(Arg("DesiredMute").Trim().ToLowerInvariant() is "1" or "true" or "yes"); return none;
            case "GetVolume": return new() { ["CurrentVolume"] = "100" };
            case "SetVolume": b.Notify(); return none;
            case "GetVolumeDB": return new() { ["CurrentVolume"] = "0" };
            case "GetVolumeDBRange": return new() { ["MinValue"] = "0", ["MaxValue"] = "0" };

            // ---- ConnectionManager
            case "GetProtocolInfo": return new() { ["Source"] = "", ["Sink"] = b.ProtocolInfo() };
            case "GetCurrentConnectionIDs": return new() { ["ConnectionIDs"] = "0" };
            case "GetCurrentConnectionInfo":
                if ((Arg("ConnectionID").Trim() is var id) && id != "" && id != "0") throw new UpnpException(706, "Invalid connection reference");
                return new()
                {
                    ["RcsID"] = "0", ["AVTransportID"] = "0", ["ProtocolInfo"] = "", ["PeerConnectionManager"] = "",
                    ["PeerConnectionID"] = "-1", ["Direction"] = "Input", ["Status"] = "OK"
                };
            default: throw new UpnpException(401, "Invalid Action");
        }
    }

    private static string Envelope(string inner) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
        $"s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>{inner}</s:Body></s:Envelope>";

    public static string Response(string serviceType, string action, Dictionary<string, string> outArgs)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in outArgs) sb.Append($"<{k}>{Xml.Esc(v)}</{k}>");
        return Envelope($"<u:{action}Response xmlns:u=\"{serviceType}\">{sb}</u:{action}Response>");
    }

    public static string Fault(int code, string description) =>
        Envelope("<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
                 $"<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{code}</errorCode>" +
                 $"<errorDescription>{Xml.Esc(description)}</errorDescription></UPnPError></detail></s:Fault>");
}
