// RoonBridgeHost.cs — Roon Bridge, fetched from Roon Labs on request and run
// beside the bridge (off unless switched on). Roon Bridge can't be shipped with
// the bridge; like RoPieee, the bridge downloads Roon's own build the first time
// it's wanted. Roon's playback (RAAT) is its own, so Roon Bridge plays to the
// DACs itself: while it's on, every DAC is shared when idle (the bridge lets go
// whenever nothing plays through it, and takes the DAC back when an app plays).
//
//   Linux    RoonBridge_linux<arch>.tar.bz2, unpacked into DATA_DIR/roon/RoonBridge,
//            checked with Roon's check.sh, and run as Roon's installer runs it
//            (start.sh, with ROON_DATAROOT and ROON_ID_DIR in DATA_DIR/roon/data),
//            but by the bridge: no root, no systemd. Roon Bridge updates itself there.
//   macOS    RoonBridge.dmg: the app goes into ~/Applications; the bridge opens it.
//   Windows  RoonBridgeInstaller64.exe: Roon's installer is opened, to click through.
//
// A Roon Bridge installed separately (tools/linux/roon-bridge.sh, or by hand) is
// used as it is: the bridge doesn't start a second one.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MandarinDacBridge.Roon;

internal static partial class RoonDownload
{
    public const string Base = "https://download.roonlabs.net/builds";

    // Roon's package for this machine, or null (none is made for it).
    public static string? Package =>
        OperatingSystem.IsLinux() ? RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "RoonBridge_linuxx64.tar.bz2",
            Architecture.Arm64 => "RoonBridge_linuxarmv8.tar.bz2",
            Architecture.Arm => "RoonBridge_linuxarmv7hf.tar.bz2",
            _ => null
        }
        : OperatingSystem.IsMacOS() ? "RoonBridge.dmg"
        : OperatingSystem.IsWindows() ? "RoonBridgeInstaller64.exe"
        : null;

    public static string Root(string dataDir) => Path.Combine(dataDir, "roon");
    public static string InstallDir(string dataDir) => Path.Combine(Root(dataDir), "RoonBridge");
    public static string DataRoot(string dataDir) => Path.Combine(Root(dataDir), "data");
    public static string Start(string dataDir) => Path.Combine(InstallDir(dataDir), "start.sh");
    public static string MacApp => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "RoonBridge.app");

    // What's installed: "2.71 (build 1683)" on Linux, "installed" for the Mac app, else "".
    public static string Version(string dataDir)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return Directory.Exists(MacApp) ? "installed" : "";
            if (!File.Exists(Start(dataDir))) return "";
            var lines = File.ReadAllLines(Path.Combine(InstallDir(dataDir), "VERSION"));
            return lines.Length > 1 ? lines[1].Replace(" production", "").Trim() : "installed";
        }
        catch (Exception) { return ""; }
    }

    public static async Task<string> Fetch(string dataDir, Action<string> progress, CancellationToken ct)
    {
        var pkg = Package ?? throw new PlatformNotSupportedException($"Roon makes no Roon Bridge for {RuntimeInformation.OSDescription} on {RuntimeInformation.ProcessArchitecture}");
        var root = Root(dataDir);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux() && Tar() == null)
            throw new InvalidOperationException("unpacking Roon Bridge needs bzip2: sudo apt install bzip2 (it's in the Docker image)");
        var file = Path.Combine(root, pkg);
        progress("downloading from Roon Labs…");
        using (var res = await Sources.Http.GetAsync($"{Base}/{pkg}", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            await using var f = File.Create(file);
            await body.CopyToAsync(f, ct);
        }
        try
        {
            if (OperatingSystem.IsLinux()) return await UnpackLinux(dataDir, file, progress, ct);
            if (OperatingSystem.IsMacOS()) return await InstallMac(root, file, progress, ct);
            // Windows: Roon's own installer, to click through on this machine's screen.
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
            file = "";
            return "installer opened";
        }
        finally { if (file != "") try { File.Delete(file); } catch (Exception) { /* left behind */ } }
    }

    // tar that can read .bz2 (it runs bzip2 or lbzip2), or null.
    private static string? Tar() =>
        Config.Find("tar", []) is { Length: > 0 } t && (Config.Find("bzip2", []) != "" || Config.Find("lbzip2", []) != "") ? t : null;

    private static async Task<string> UnpackLinux(string dataDir, string file, Action<string> progress, CancellationToken ct)
    {
        progress("unpacking…");
        var root = Root(dataDir);
        var tmp = Path.Combine(root, "unpack.tmp");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var (code, output) = await Run(Tar()!, ["-xjf", file, "-C", tmp], ct);
        if (code != 0) throw new InvalidOperationException("couldn't unpack Roon Bridge: " + Last(output));
        var unpacked = Path.Combine(tmp, "RoonBridge");
        if (!File.Exists(Path.Combine(unpacked, "start.sh"))) throw new InvalidDataException("Roon's package has no start.sh in it");
        // Roon's own check: glibc, libstdc++, libicu, ALSA.
        progress("checking that Roon Bridge can run here…");
        var bash = Config.Find("bash", []);
        if (bash != "" && File.Exists(Path.Combine(unpacked, "check.sh")))
        {
            var (ok, report) = await Run(bash, [Path.Combine(unpacked, "check.sh")], ct);
            if (ok != 0)
            {
                var failed = report.Split('\n').Where(l => l.Contains("FAILED")).Select(l => l.Replace("[ FAILED ]", "").Trim()).Where(l => l != "" && !l.StartsWith("STATUS")).ToList();
                throw new InvalidOperationException("Roon Bridge can't run on this machine: " + (failed.Count > 0 ? string.Join("; ", failed) : Last(report)) + " (see kb.roonlabs.com/LinuxInstall)");
            }
        }
        var target = InstallDir(dataDir);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(unpacked, target);
        Directory.Delete(tmp, true);
        Directory.CreateDirectory(DataRoot(dataDir));
        return Version(dataDir);
    }

    private static async Task<string> InstallMac(string root, string dmg, Action<string> progress, CancellationToken ct)
    {
        progress("installing into Applications…");
        var mount = Path.Combine(root, "mnt");
        Directory.CreateDirectory(mount);
        var (code, output) = await Run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmg], ct);
        if (code != 0) throw new InvalidOperationException("couldn't open Roon's disk image: " + Last(output));
        try
        {
            var app = Directory.GetDirectories(mount, "*.app").FirstOrDefault() ?? throw new InvalidDataException("Roon's disk image has no app in it");
            Directory.CreateDirectory(Path.GetDirectoryName(MacApp)!);
            if (Directory.Exists(MacApp)) Directory.Delete(MacApp, true);
            (code, output) = await Run("/usr/bin/ditto", [app, MacApp], ct);
            if (code != 0) throw new InvalidOperationException("couldn't copy Roon Bridge: " + Last(output));
        }
        finally { await Run("/usr/bin/hdiutil", ["detach", mount, "-quiet"], CancellationToken.None); }
        return "installed";
    }

    internal static async Task<(int Code, string Output)> Run(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} didn't start");
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, Strip(await o + await e));
    }

    // Colours out (Roon's scripts use them).
    private static string Strip(string s) => AnsiCodes().Replace(s, "");

    private static string Last(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial System.Text.RegularExpressions.Regex AnsiCodes();
}

// Roon Bridge kept running while switched on (Linux: run here; macOS: the app opened).
internal sealed class RoonRunner : IDisposable
{
    private readonly string dataDir;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;
    private readonly object gate = new();
    private Process? proc;
    private string lastError = "";
    private bool opened;     // the Mac app was opened by the bridge (and is quit when switched off)

    public string Status { get; private set; } = "starting";

    public RoonRunner(string dataDir)
    {
        this.dataDir = dataDir;
        thread = new Thread(Run) { IsBackground = true, Name = "roon-bridge" };
        thread.Start();
    }

    private static void Log(string m) => MandarinDacBridge.Log.Write("Roon Bridge: " + m);

    private void Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (RoonDownload.Version(dataDir) == "" && !OperatingSystem.IsWindows())
                {
                    Status = "not downloaded yet: Download Roon Bridge fetches it from Roon Labs";
                    Wait(3000);
                    continue;
                }
                // One installed separately is used as it is.
                if (proc == null && !opened && RoonBridge.Running(fresh: true) != "")
                {
                    Status = "running (installed separately) · the DACs are shared with it";
                    Wait(5000);
                    continue;
                }
                if (OperatingSystem.IsLinux()) RunLinux();
                else if (OperatingSystem.IsMacOS()) { OpenMac(); Wait(10_000); }
                else { Status = RoonBridge.Running() != "" ? "running · the DACs are shared with it" : "not running: install it with Download Roon Bridge"; Wait(5000); }
            }
            catch (Exception e)
            {
                if (stop.IsCancellationRequested) return;
                if (e.Message != lastError) Log(e.Message);
                lastError = e.Message;
                Status = e.Message;
                Wait(15_000);
            }
        }
    }

    private void Wait(int ms) => stop.Token.WaitHandle.WaitOne(ms);

    private void RunLinux()
    {
        var root = RoonDownload.DataRoot(dataDir);
        Directory.CreateDirectory(root);
        var psi = new ProcessStartInfo(RoonDownload.Start(dataDir)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["ROON_DATAROOT"] = root;
        psi.Environment["ROON_ID_DIR"] = root;
        var lines = new Queue<string>();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Roon Bridge didn't start");
        void Line(string? l)
        {
            if (l == null) return;
            lock (lines) { lines.Enqueue(l); while (lines.Count > 20) lines.Dequeue(); }
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (gate) proc = p;
        var started = Environment.TickCount64;
        Log($"started ({RoonDownload.Version(dataDir)})");
        Status = $"running · {RoonDownload.Version(dataDir)} · the DACs are shared with it";
        lastError = "";
        try
        {
            while (!p.HasExited && !stop.IsCancellationRequested) Wait(1000);
            if (stop.IsCancellationRequested) return;
            string last;
            lock (lines) last = lines.LastOrDefault(l => l.Trim() != "") ?? "";
            // A quick exit is a fault (waited on before the next try); a long run that ends is just started again.
            if (Environment.TickCount64 - started < 30_000)
                throw new InvalidOperationException($"Roon Bridge stopped ({p.ExitCode}){(last != "" ? ": " + last[..Math.Min(200, last.Length)] : "")}");
            Log($"stopped ({p.ExitCode}); starting it again");
        }
        finally
        {
            lock (gate) proc = null;
            Stop(p);
        }
    }

    // start.sh doesn't pass a SIGTERM on (its clean-up runs only when it ends by itself), so everything it
    // started is asked to stop too: Roon Bridge, its helper, its audio server. Whatever is left after 5 s is stopped.
    private static void Stop(Process p)
    {
        try
        {
            var all = Native.Libc.Descendants(p.Id);
            all.Insert(0, p.Id);
            foreach (var pid in all) Native.Libc.Terminate(pid);
            for (int i = 0; i < 50 && all.Any(Native.Libc.Alive); i++) Thread.Sleep(100);
            foreach (var pid in all.Where(Native.Libc.Alive)) Native.Libc.ForceStop(pid);
            p.WaitForExit(1000);
        }
        catch (Exception) { /* gone */ }
    }

    private void OpenMac()
    {
        if (RoonBridge.Running(fresh: true) != "") { Status = "running · the DACs are shared with it"; return; }
        var (code, output) = RoonDownload.Run("/usr/bin/open", ["-g", "-a", RoonDownload.MacApp], stop.Token).GetAwaiter().GetResult();
        if (code != 0) throw new InvalidOperationException("couldn't open Roon Bridge: " + output.Trim());
        Log("opened");
        opened = true;
        Status = "running · the DACs are shared with it";
    }

    public void Dispose()
    {
        stop.Cancel();
        Process? p;
        lock (gate) p = proc;
        if (p != null) Stop(p);
        thread.Join(TimeSpan.FromSeconds(8));
        if (OperatingSystem.IsMacOS() && opened)
            try { RoonDownload.Run("/usr/bin/osascript", ["-e", "tell application \"RoonBridge\" to quit"], CancellationToken.None).Wait(5000); }
            catch (Exception) { /* left running */ }
    }
}
