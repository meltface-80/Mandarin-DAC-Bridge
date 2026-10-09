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
    public TimeSpan ScanEvery { get; init; } = TimeSpan.FromSeconds(3);
    public string Hostname { get; init; } = System.Net.Dns.GetHostName().Replace(".local", "");
    public string Platform { get; init; } = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : "other";
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
            TestDevices = Env("BRIDGE_TEST_DEVICES"),
            TestSink = Env("BRIDGE_TEST_SINK") is "fake" or "busy",
            TestSinkBusy = Env("BRIDGE_TEST_SINK") == "busy"
        };
        try { Directory.CreateDirectory(c.DataDir); } catch (Exception) { /* reported when settings are saved */ }
        return c;
    }

    // ffmpeg on the PATH, else where Homebrew puts it (launchd's PATH is short).
    private static string FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                     .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]))
        {
            var f = Path.Combine(dir, "ffmpeg");
            if (File.Exists(f)) return f;
        }
        return "ffmpeg";
    }
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
