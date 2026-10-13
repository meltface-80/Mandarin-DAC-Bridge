// Config.cs — every setting, from the environment, with a default that needs
// no changing.
//
//   PORT                 the page and the UPnP devices (55500)
//   DATA_DIR             settings.json lives here (data/ beside the program)
//   FFMPEG               the decoder (ffmpeg on the PATH, or Homebrew's)
//   BRIDGE_IP            the address to advertise, when the machine has several
//   BRIDGE_HOSTNAMES     other names the page is reached by (your own DNS names), comma-separated
//   LOCK_GRACE_S         how long a controller keeps a DAC after it stops (10)
//   MANDARIN_PORT        Mandarin's own port, to recognise its streams (3500)
//   BRIDGE_ALL_OUTPUTS   1 = offer every output, not only USB DACs
//   BRIDGE_NAME_SUFFIX   added to each DAC's name on the network (" (Bridge)")
//   SQUEEZELITE          1 = each DAC is a Squeezebox player too (also a switch on the page)
//   LMS_SERVER           the Squeezebox server (Lyrion, Roon) to use, instead of looking for one
//   LMS_JSON_PORT        that server's web port, if it isn't 9000 (for its JSON-RPC: what it plays, its volume)
//   SPOTIFY              1 = each DAC is a Spotify Connect speaker too, through Spotify Soloist (Linux; also on the page)
//   SOLOIST_API_KEY      your Soloist API key (or typed on the page; kept in settings.json, never shown again)
//   SOLOIST              the soloist program (else on the PATH, or downloaded by the page into DATA_DIR/soloist)
//   CALDERA              1 = each DAC is a Plex player too, through Caldera Headless (Linux; also on the page)
//   QOBUZ                1 = each DAC is a Qobuz Connect speaker too, through QobuzProxy (also on the page)
//   MAC_APPS             1 = the Music and Spotify apps on this Mac play through the bridge (macOS; also on the page)
using System.Reflection;

namespace MandarinDacBridge;

internal sealed class Config
{
    public static string Version { get; } =
        typeof(Config).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public int Port { get; init; } = 55500;
    public string DataDir { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");
    // FFMPEG as set, else found when first needed: asking each ffmpeg found whether it has SoX takes a
    // moment, and --list, --diagnose and --loopback-test never need one.
    private readonly string ffmpeg = "";
    public string Ffmpeg { get => ffmpeg.Length > 0 ? ffmpeg : Found.Value; init => ffmpeg = value; }
    private static readonly Lazy<string> Found = new(FindFfmpeg);
    public string BindIp { get; init; } = "";
    public string[] HostNames { get; init; } = [];
    public TimeSpan LockGrace { get; init; } = TimeSpan.FromSeconds(10);
    public int MandarinPort { get; init; } = 3500;
    public bool AllOutputs { get; init; }
    public string NameSuffix { get; init; } = " (Bridge)";
    // Defaults for the page's switches (the page's choice, once made, is kept in settings.json).
    public bool Squeezelite { get; init; }
    public string LmsServer { get; init; } = "";
    public int LmsJsonPort { get; init; } = 9000;
    public bool Spotify { get; init; }
    public string Soloist { get; init; } = "";
    public string SoloistKey { get; init; } = "";
    public bool Caldera { get; init; }
    public bool Qobuz { get; init; }
    public bool MacApps { get; init; }
    public bool Roon { get; init; }
    public bool RoonApi { get; init; }
    public string RoonCore { get; init; } = "";
    public TimeSpan ScanEvery { get; init; } = TimeSpan.FromSeconds(3);
    public string Hostname { get; init; } = System.Net.Dns.GetHostName().Replace(".local", "");
    // The names a browser may reach the bridge by for a change to go ahead (SameOrigin.cs), besides addresses.
    public string[] Names() => [Hostname, BindIp, .. HostNames];
    public string Platform { get; init; } = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other";
    // Tests: DACs given rather than found (JSON), and played to a clock rather than a device.
    public string? TestDevices { get; init; }
    public bool TestSink { get; init; }
    public bool TestSinkBusy { get; init; }

    public static Config FromEnvironment()
    {
        static string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
        var c = new Config
        {
            Port = int.TryParse(Env("PORT"), out var p) ? p : 55500,
            DataDir = Env("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"),
            Ffmpeg = Env("FFMPEG") ?? "",
            BindIp = Env("BRIDGE_IP") ?? "",
            HostNames = (Env("BRIDGE_HOSTNAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LockGrace = TimeSpan.FromSeconds(double.TryParse(Env("LOCK_GRACE_S"), System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 10),
            MandarinPort = int.TryParse(Env("MANDARIN_PORT"), out var mp) ? mp : 3500,
            AllOutputs = Env("BRIDGE_ALL_OUTPUTS") == "1",
            NameSuffix = Environment.GetEnvironmentVariable("BRIDGE_NAME_SUFFIX") ?? " (Bridge)",
            Squeezelite = Env("SQUEEZELITE") == "1",
            LmsServer = Env("LMS_SERVER") ?? "",
            LmsJsonPort = int.TryParse(Env("LMS_JSON_PORT"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var jp) && jp is > 0 and < 65536 ? jp : 9000,
            Spotify = Env("SPOTIFY") == "1",
            Soloist = Env("SOLOIST") ?? "",
            SoloistKey = Env("SOLOIST_API_KEY") ?? "",
            Caldera = Env("CALDERA") == "1",
            Qobuz = Env("QOBUZ") == "1",
            MacApps = Env("MAC_APPS") == "1",
            Roon = Env("ROON_BRIDGE") == "1",
            RoonApi = Env("ROON_API") == "1",
            RoonCore = Env("ROON_CORE") ?? "",
            TestDevices = Env("BRIDGE_TEST_DEVICES"),
            TestSink = Env("BRIDGE_TEST_SINK") is "fake" or "busy",
            TestSinkBusy = Env("BRIDGE_TEST_SINK") == "busy"
        };
        try { Directory.CreateDirectory(c.DataDir); } catch (Exception) { /* reported when settings are saved */ }
        return c;
    }

    // Where the page's Download puts Spotify Soloist.
    public string SoloistDir => Path.Combine(DataDir, "soloist");

    // soloist as set, else downloaded by the page, else on the PATH; "" if none.
    public string FindSoloist() =>
        Soloist != "" ? (File.Exists(Soloist) ? Soloist : "") : Find("soloist", [SoloistDir]);

    // The PulseAudio server, which gives Soloist a private sound server to play into.
    public static string FindPulseAudio() => Find("pulseaudio", []);

    // A program in these folders, then on the PATH, then where package managers put it ("" if nowhere).
    // On Windows with .exe, and winget's, Scoop's and Chocolatey's folders.
    internal static string Find(string name, string[] first) => FindAll(name, first).FirstOrDefault() ?? "";

    // Every copy of a program, in the order Find looks.
    internal static IEnumerable<string> FindAll(string name, string[] first)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in first.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                     .Concat(Common()))
        {
            string f;
            try { f = Path.Combine(dir.Trim('"'), exe); }
            catch (ArgumentException) { continue; }   // a broken PATH entry
            if (File.Exists(f)) yield return f;
        }
    }

    private static IEnumerable<string> Common()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return [Path.Combine(local, "Microsoft", "WinGet", "Links"), Path.Combine(home, "scoop", "shims"), @"C:\ProgramData\chocolatey\bin",
                    @"C:\ffmpeg\bin", Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"), AppContext.BaseDirectory];
        }
        return ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"];
    }

    // The ffmpeg with SoX (Audio/Soxr.cs): Homebrew's ffmpeg-full (kept off the PATH, beside the plain ffmpeg
    // Mandarin uses), then the PATH, then where Homebrew (launchd's PATH is short) or winget put one. With none
    // that has SoX, the first found.
    private static string FindFfmpeg() =>
        Audio.Soxr.Pick(FindAll("ffmpeg", ["/opt/homebrew/opt/ffmpeg-full/bin", "/usr/local/opt/ffmpeg-full/bin"])) is { Length: > 0 } f ? f : "ffmpeg";
}

internal static class Log
{
    private static readonly object Gate = new();
    public static Action<string>? Sink { get; set; }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (Gate) Console.WriteLine(line);
        Sink?.Invoke(line);
    }
}
