// QobuzConnect.cs — the DACs as Qobuz Connect speakers, through QobuzProxy
// (github.com/leolobato/qobuz-proxy, MIT): a Qobuz Connect device that plays
// to a UPnP renderer. Each DAC's renderer is one: the bridge runs one
// qobuz-proxy with a speaker per DAC, "<DAC> (Bridge)", aimed at the DAC's
// own UPnP description. Qobuz's FLAC (up to 24-bit/192 kHz, or the DAC's
// best) comes through qobuz-proxy's pass-through proxy untouched, into the
// same exclusive, rate-switched path as every UPnP app, kept apart from the
// others by the arbiter like any of them (it is named "Qobuz" by the
// audio-proxy port each speaker is given, as Mandarin is by its own).
//
// qobuz-proxy is Python. The bridge installs it on request, pinned to a
// release, into a private Python environment in DATA_DIR/qobuz (needs
// Python 3.10 or later), writes its config, starts it, and restarts it when
// the DACs change. Signing in to Qobuz happens in qobuz-proxy's own web page
// (port 8689), linked from the bridge's.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MandarinDacBridge.Qobuz;

internal sealed record QobuzSpeaker(string Id, string Key, string Name, string DescriptionUrl, string Ip, int Port);

internal sealed class QobuzConnect : IDisposable
{
    public const string Version = "1.7.7";
    public const int WebPort = 8689;
    public const int FirstProxyPort = 7120;
    // QOBUZPROXY_SOURCE: another place to install it from (a local copy, a fork).
    private static string Source => Environment.GetEnvironmentVariable("QOBUZPROXY_SOURCE") is { Length: > 0 } s ? s
        : $"https://github.com/leolobato/qobuz-proxy/archive/refs/tags/v{Version}.tar.gz";

    private readonly string dir;
    private readonly Func<List<QobuzSpeaker>> speakers;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread runner;
    private readonly object gate = new();
    private readonly AutoResetEvent changed = new(false);
    private Process? proc;
    private string written = "";
    private string lastError = "";
    private int installing;

    public string Status { get; private set; } = "starting";
    public string Install { get; private set; } = "";
    public bool SignedIn { get; private set; }
    public string Account { get; private set; } = "";

    public QobuzConnect(string dataDir, Func<List<QobuzSpeaker>> speakers)
    {
        dir = Path.Combine(dataDir, "qobuz");
        this.speakers = speakers;
        runner = new Thread(Run) { IsBackground = true, Name = "qobuz-proxy" };
        runner.Start();
    }

    private static void Log(string m) => MandarinDacBridge.Log.Write("Qobuz Connect: " + m);

    private void SetStatus(string s) => Status = s;

    // ------------------------------------------------------------ Python, and qobuz-proxy in it

    private string Venv => Path.Combine(dir, "venv");
    private string VenvPython => OperatingSystem.IsWindows() ? Path.Combine(Venv, "Scripts", "python.exe") : Path.Combine(Venv, "bin", "python");
    public bool Installed => File.Exists(VenvPython) && File.Exists(Path.Combine(dir, "installed-" + Version));

    // A Python 3.10 or later: Homebrew's before the Mac's own (3.9), the py launcher on Windows.
    public static (string Exe, string[] Args, string Version)? FindPython()
    {
        var tries = new List<(string, string[])>();
        if (OperatingSystem.IsWindows()) { tries.Add(("py", ["-3"])); tries.Add(("python", [])); tries.Add(("python3", [])); }
        else
        {
            foreach (var d in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" })
                foreach (var v in new[] { "python3.13", "python3.12", "python3.11", "python3.10", "python3" })
                    if (File.Exists(Path.Combine(d, v))) tries.Add((Path.Combine(d, v), []));
            tries.Add(("python3", []));
        }
        foreach (var (exe, pre) in tries)
        {
            var (code, output) = Run(exe, [.. pre, "-c", "import sys; print('%d.%d' % sys.version_info[:2])"], 10_000);
            if (code != 0) continue;
            var v = output.Trim();
            var parts = v.Split('.');
            if (parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && (major > 3 || minor >= 10))
                return (exe, pre, v);
        }
        return null;
    }

    private static (int Code, string Output) Run(string exe, string[] args, int ms, Dictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(ms)) { try { p.Kill(true); } catch (Exception) { /* gone */ } return (-1, "timed out"); }
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (Exception x) { return (-1, x.Message); }
    }

    // Makes the private Python environment and installs qobuz-proxy in it (a minute or two).
    public async Task InstallProxy()
    {
        if (Interlocked.Exchange(ref installing, 1) == 1) return;
        try
        {
            await Task.Run(() =>
            {
                var py = FindPython() ?? throw new InvalidOperationException(
                    OperatingSystem.IsMacOS() ? "needs Python 3.10 or later: brew install python"
                    : OperatingSystem.IsWindows() ? "needs Python 3.10 or later, from python.org"
                    : "needs Python 3.10 or later: sudo apt install python3 python3-venv");
                Directory.CreateDirectory(dir);
                Install = $"making a Python {py.Version} environment…";
                if (!File.Exists(VenvPython))
                {
                    var (code, output) = Run(py.Exe, [.. py.Args, "-m", "venv", Venv], 120_000);
                    if (code != 0)
                        throw new InvalidOperationException(output.Contains("ensurepip") || output.Contains("venv")
                            ? "needs Python's venv: sudo apt install python3-venv" : "python -m venv: " + Last(output));
                }
                Install = $"installing QobuzProxy {Version} from GitHub…";
                var (c2, o2) = Run(VenvPython, ["-m", "pip", "install", "--disable-pip-version-check", "--quiet", Source], 600_000);
                if (c2 != 0) throw new InvalidOperationException("pip: " + Last(o2));
                foreach (var old in Directory.GetFiles(dir, "installed-*")) File.Delete(old);
                File.WriteAllText(Path.Combine(dir, "installed-" + Version), DateTime.UtcNow.ToString("O"));
            });
            Install = "";
            Log($"QobuzProxy {Version} installed");
            changed.Set();
        }
        catch (Exception e)
        {
            Install = "install failed: " + e.Message;
            Log(Install);
        }
        finally { Volatile.Write(ref installing, 0); }
    }

    private static string Last(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l != "") is { } l ? l[..Math.Min(240, l.Length)] : "";

    // ------------------------------------------------------------ its config, and running it

    // A speaker per DAC, aimed at its UPnP description; the same UUID for a DAC each time.
    public static string Config(List<QobuzSpeaker> list)
    {
        // A YAML double-quoted string: backslash and quote escaped, no control characters.
        static string Q(string s) => "\"" + new string(s.Where(c => !char.IsControl(c)).ToArray()).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("# Written by Mandarin DAC Bridge: its DACs as Qobuz Connect speakers.");
        sb.AppendLine("# The bridge writes this again when its DACs change; edits to the speakers here don't last.");
        sb.AppendLine("qobuz:");
        sb.AppendLine("  max_quality: auto");
        sb.AppendLine("server:");
        sb.AppendLine($"  http_port: {WebPort}");
        sb.AppendLine("  bind_address: \"0.0.0.0\"");
        sb.AppendLine("logging:");
        sb.AppendLine("  level: info");
        sb.AppendLine("speakers:");
        foreach (var s in list)
        {
            sb.AppendLine($"  - name: {Q(s.Name)}");
            sb.AppendLine($"    uuid: {Q(Uuid(s.Key))}");
            sb.AppendLine("    backend: dlna");
            sb.AppendLine("    device_type: streamer");
            sb.AppendLine("    max_quality: auto");
            sb.AppendLine($"    dlna_ip: {Q(s.Ip)}");
            sb.AppendLine($"    dlna_port: {s.Port}");
            sb.AppendLine($"    dlna_description_url: {Q(s.DescriptionUrl)}");
            sb.AppendLine("    dlna_fixed_volume: true");
            sb.AppendLine($"    http_port: {WebPort + 1 + list.IndexOf(s)}");
            sb.AppendLine($"    proxy_port: {FirstProxyPort + list.IndexOf(s)}");
        }
        return sb.ToString();
    }

    public static string Uuid(string key)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("qobuz|" + key));
        h[6] = (byte)((h[6] & 0x0F) | 0x50);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h[..16], bigEndian: true).ToString();
    }

    // The DACs changed (added, gone, renamed): qobuz-proxy starts again with them.
    public void Changed() => changed.Set();

    private void Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!Installed)
                {
                    SetStatus(Install != "" ? Install : "needs QobuzProxy: install it on this page");
                    WaitChange(3000);
                    continue;
                }
                var list = speakers();
                if (list.Count == 0) { SetStatus("no DACs to offer"); WaitChange(3000); continue; }
                Directory.CreateDirectory(dir);
                written = Config(list);
                File.WriteAllText(Path.Combine(dir, "config.yaml"), written);
                RunProxy(list.Count);
            }
            catch (Exception e)
            {
                if (stop.IsCancellationRequested) return;
                if (e.Message != lastError) Log(e.Message);
                lastError = e.Message;
                SetStatus(e.Message);
                WaitChange(10_000);
            }
        }
    }

    private void WaitChange(int ms) => WaitHandle.WaitAny([changed, stop.Token.WaitHandle], ms);

    private void RunProxy(int count)
    {
        var psi = new ProcessStartInfo(VenvPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-m", "qobuz_proxy", "--config", Path.Combine(dir, "config.yaml") }) psi.ArgumentList.Add(a);
        psi.Environment["QOBUZPROXY_DATA_DIR"] = dir;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        var lines = new Queue<string>();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("qobuz-proxy didn't start");
        void Line(string? l)
        {
            if (l == null) return;
            lock (lines) { lines.Enqueue(l); while (lines.Count > 30) lines.Dequeue(); }
            if (l.Contains("ERROR") || l.Contains("Traceback") || l.Contains("Exception")) Log(l.Length > 200 ? l[..200] : l);
        }
        string Last()
        {
            string last;
            lock (lines) last = lines.LastOrDefault(l => l.Trim() != "") ?? "";
            return last != "" ? ": " + last[..Math.Min(200, last.Length)] : "";
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (gate) proc = p;
        Log($"offering {count} DAC{(count == 1 ? "" : "s")} to Qobuz (web page on port {WebPort})");
        SetStatus("starting…");
        lastError = "";
        // Until its page first answers; a slow machine's first start (Python loading) takes a while.
        var deadline = DateTime.UtcNow + StartupLimit;
        bool answered = false;
        try
        {
            while (!p.HasExited && !stop.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny([changed, stop.Token.WaitHandle], answered ? 3000 : 1000) == 0) { Log("the DACs changed: starting again"); return; }
                answered |= PollStatus();
                if (!answered && DateTime.UtcNow > deadline)
                    throw new InvalidOperationException($"qobuz-proxy didn't answer within {StartupLimit.TotalSeconds:0} s{Last()}");
            }
            if (stop.IsCancellationRequested) return;
            throw new InvalidOperationException($"qobuz-proxy stopped ({p.ExitCode}){Last()}");
        }
        finally
        {
            lock (gate) proc = null;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch (Exception) { /* gone */ }
            SignedIn = false;
        }
    }

    private static readonly HttpClient Local = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(60);

    // Signed in or not, from qobuz-proxy's own status (false while its page doesn't answer yet).
    private bool PollStatus()
    {
        try
        {
            using var doc = JsonDocument.Parse(Local.GetStringAsync($"http://127.0.0.1:{WebPort}/api/status").GetAwaiter().GetResult());
            var auth = doc.RootElement.TryGetProperty("auth", out var a) ? a : default;
            SignedIn = auth.ValueKind == JsonValueKind.Object && auth.TryGetProperty("authenticated", out var ok) && ok.ValueKind == JsonValueKind.True;
            Account = SignedIn && auth.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() ?? "" : "";
            SetStatus(SignedIn ? $"ready · choose it in Qobuz{(Account != "" ? " · " + Account : "")}" : "sign in to Qobuz on QobuzProxy's page");
            return true;
        }
        catch (Exception) { return false; /* still starting */ }
    }

    public void Dispose()
    {
        stop.Cancel();
        Process? p;
        lock (gate) p = proc;
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch (Exception) { /* gone */ }
        runner.Join(TimeSpan.FromSeconds(5));
    }
}
