// Json.cs — what the page reads (/api/dacs) and the settings file, with
// System.Text.Json's source generator (no reflection: Native AOT).
using System.Text.Json.Serialization;

namespace MandarinDacBridge;

internal sealed class BridgeView
{
    public List<DacView> Dacs { get; set; } = [];
    public string Error { get; set; } = "";
    public string Version { get; set; } = "";
    public string Host { get; set; } = "";
    public string Platform { get; set; } = "";
    public List<string> Others { get; set; } = [];
    public ServicesView Services { get; set; } = new();
}

internal sealed class ServicesView
{
    public bool Squeezelite { get; set; }
    public string LmsServer { get; set; } = "";
    public bool Spotify { get; set; }
    // Spotify Soloist: Linux on x86-64 or ARM; the key only as "set" (it is never sent back).
    public bool SpotifyPossible { get; set; }
    public bool SoloistKey { get; set; }
    public string Soloist { get; set; } = "";
    public string SoloistVersion { get; set; } = "";
    public string SoloistExpires { get; set; } = "";     // yyyy-MM-dd
    public string SoloistDownload { get; set; } = "";
    public bool PulseAudio { get; set; }
    // Caldera Headless: Linux; the Plex token only as "signed in".
    public bool Caldera { get; set; }
    public bool CalderaPossible { get; set; }
    public string CalderaVersion { get; set; } = "";
    public string CalderaDownload { get; set; } = "";
    public bool PlexSignedIn { get; set; }
    public string PlexCode { get; set; } = "";
    public string PlexMessage { get; set; } = "";
    // Qobuz Connect through QobuzProxy.
    public bool Qobuz { get; set; }
    public bool QobuzInstalled { get; set; }
    public string QobuzInstall { get; set; } = "";
    public string QobuzStatus { get; set; } = "";
    public bool QobuzSignedIn { get; set; }
    public int QobuzWebPort { get; set; }
    public string RoonBridge { get; set; } = "";
}

internal sealed class DacView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Transport { get; set; } = "";
    public string? Usb { get; set; }
    public int[] Rates { get; set; } = [];
    public int[] Bits { get; set; } = [];
    public int Channels { get; set; }
    public string[] Formats { get; set; } = [];
    public int[] DsdNative { get; set; } = [];
    public int[] DopRates { get; set; } = [];
    public int CurrentRate { get; set; }
    public double? Volume { get; set; }
    public bool Enabled { get; set; }
    public string Dsd { get; set; } = "auto";
    public string Platform { get; set; } = "";
    public string? UpnpName { get; set; }
    public string? DsdMode { get; set; }
    public bool Exclusive { get; set; }
    public string? Waiting { get; set; }
    public string Holder { get; set; } = "";
    public NowPlaying? Player { get; set; }
    public ControlView? Control { get; set; }
    public bool Share { get; set; }
    public string? Squeezebox { get; set; }
    public string? SqueezeboxId { get; set; }
    public string? Spotify { get; set; }
    public string? Caldera { get; set; }
}

internal sealed class ControlView
{
    public string? Owner { get; set; }
    public bool Locked { get; set; }
    public Blocked? Blocked { get; set; }
}

internal sealed record Health(bool Ok, string Version, int Dacs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BridgeView))]
[JsonSerializable(typeof(SettingsFile))]
[JsonSerializable(typeof(SettingsPatch))]
[JsonSerializable(typeof(DacSettings))]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(List<DacDevice>))]
[JsonSerializable(typeof(ServiceSettings))]
[JsonSerializable(typeof(ServicesView))]
internal sealed partial class BridgeJson : JsonSerializerContext;
