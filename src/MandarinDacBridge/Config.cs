// Config.cs — every setting, from the environment, with a default that needs
// no changing.
//
//   PORT                 the page and the UPnP devices (55500)
//   DATA_DIR             settings.json lives here (data/ beside the program)
//   FFMPEG               the decoder (ffmpeg on the PATH, or Homebrew's)
//   BRIDGE_IP            the address to advertise, when the machine has several
//   LOCK_GRACE_S         how long a controller keeps a DAC after it stops (10)
//   MANDARIN_PORT        Mandarin's own port, to recognise its streams (3500)
//   BRIDGE_ALL_OUTPUTS   1 = offer every output, not only USB DACs
//   BRIDGE_NAME_SUFFIX   added to each DAC's name on the network (" (Bridge)")
//   SQUEEZELITE          1 = each DAC is a Squeezebox player too (also a switch on the page)
//   LMS_SERVER           the Squeezebox server (Lyrion, Roon) to use, instead of looking for one
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
    public string Ffmpeg { get; init; } = "ffmpeg";
    public string BindIp { get; init; } = "";
    public TimeSpan LockGrace { get; init; } = TimeSpan.FromSeconds(10);
    public int MandarinPort { get; init; } = 3500;
    public bool AllOutputs { get; init; }
    public string NameSuffix { get; init; } = " (Bridge)";
    // Defaults for the page's switches (the page's choice, once made, is kept in settings.json).
    public bool Squeezelite { get; init; }
    public string LmsServer { get; init; } = "";
    public bool Spotify { get; init; }
    public string Soloist { get; init; } = "";
    public string SoloistKey { get; init; } = "";
    public bool Caldera { get; init; }
    public bool Qobuz { get; init; }
    public bool MacApps { get; init; }
    public bool Roon { get; init; }
    public TimeSpan ScanEvery { get; init; } = TimeSpan.FromSeconds(3);
    public string Hostname { get; init; } = System.Net.Dns.GetHostName().Replace(".local", "");
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
            Ffmpeg = Env("FFMPEG") ?? FindFfmpeg(),
            BindIp = Env("BRIDGE_IP") ?? "",
            LockGrace = TimeSpan.FromSeconds(double.TryParse(Env("LOCK_GRACE_S"), System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 10),
            MandarinPort = int.TryParse(Env("MANDARIN_PORT"), out var mp) ? mp : 3500,
            AllOutputs = Env("BRIDGE_ALL_OUTPUTS") == "1",
            NameSuffix = Environment.GetEnvironmentVariable("BRIDGE_NAME_SUFFIX") ?? " (Bridge)",
            Squeezelite = Env("SQUEEZELITE") == "1",
            LmsServer = Env("LMS_SERVER") ?? "",
            Spotify = Env("SPOTIFY") == "1",
            Soloist = Env("SOLOIST") ?? "",
            SoloistKey = Env("SOLOIST_API_KEY") ?? "",
            Caldera = Env("CALDERA") == "1",
            Qobuz = Env("QOBUZ") == "1",
            MacApps = Env("MAC_APPS") == "1",
            Roon = Env("ROON_BRIDGE") == "1",
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
    internal static string Find(string name, string[] first)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in first.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                     .Concat(Common()))
        {
            try
            {
                var f = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(f)) return f;
            }
            catch (ArgumentException) { /* a broken PATH entry */ }
        }
        return "";
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

    // ffmpeg on the PATH, else where Homebrew (launchd's PATH is short) or winget put it.
    private static string FindFfmpeg() => Find("ffmpeg", []) is { Length: > 0 } f ? f : "ffmpeg";
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
