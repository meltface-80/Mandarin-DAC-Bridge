// The whole bridge, end to end, in-process: real ffmpeg, a little HTTP
// server standing in for Audirvana's or Mandarin's streams, SOAP from two
// controllers, and a DAC played in real time by the clock sink (or, on
// Linux, the real ALSA path on the "null" device).
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Avt = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string Aud = "Audirvana Studio/2.0 UPnP/1.0";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string tmp = Directory.CreateTempSubdirectory("dacbridge-").FullName;
    private readonly List<BridgeHost> hosts = [];
    private readonly List<string> logs = [];
    private FileServer files = null!;
    private int port;

    public async Task InitializeAsync()
    {
        Log.Sink = l => { lock (logs) logs.Add(l); };
        Directory.CreateDirectory(Path.Combine(tmp, "media"));
        if (HasFfmpeg())
        {
            Make("a96.flac", 96000, 3, "s32");
            Make("b96.flac", 96000, 2, "s32");
            Make("c44.flac", 44100, 2, "s16");
            Make("hi.flac", 352800, 1, "s32");
        }
        files = new FileServer(Path.Combine(tmp, "media"));
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var h in hosts) await h.StopAsync();
        files.Dispose();
        Log.Sink = null;
        lock (logs) foreach (var l in logs) output.WriteLine(l);
    }

    private static bool HasFfmpeg()
    {
        try { using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true }); p!.WaitForExit(); return p.ExitCode == 0; }
        catch (Exception) { return false; }
    }

    private void Make(string name, int rate, int secs, string fmt)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in new[] { "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=f=440:r={rate}:d={secs}", "-ac", "2", "-sample_fmt", fmt, "-c:a", "flac", Path.Combine(tmp, "media", name) })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task<string> Start(object dev, bool clock = true, bool busy = false, string soloist = "", string roonCore = "", int lmsJson = 9000)
    {
        port = FreePort();
        var config = new Config
        {
            Port = port, DataDir = Directory.CreateTempSubdirectory("data-").FullName, BindIp = "127.0.0.1",
            LockGrace = TimeSpan.FromSeconds(2), Ffmpeg = "ffmpeg",
            TestDevices = JsonSerializer.Serialize(dev), TestSink = clock, TestSinkBusy = busy, Soloist = soloist, RoonCore = roonCore, LmsJsonPort = lmsJson
        };
        var host = new BridgeHost(config);
        hosts.Add(host);
        await host.StartAsync();
        var v = await Until(async () => (await Dacs()).RootElement.GetProperty("dacs") is { } d && d.GetArrayLength() > 0 ? d[0] : (JsonElement?)null);
        return v.GetProperty("id").GetString()!;
    }

    private static object Dev(string spec, int[]? dsdNative = null, int[]? rates = null, int? volume = null)
    {
        var d = new Dictionary<string, object>
        {
            ["key"] = "test:" + spec, ["name"] = "Test DAC", ["manufacturer"] = "ALSA", ["model"] = "null", ["transport"] = "USB",
            ["usb"] = "0000:0000", ["spec"] = spec, ["rates"] = rates ?? new[] { 44100, 48000, 88200, 96000, 176400, 192000 },
            ["bits"] = new[] { 16, 24, 32 }, ["channels"] = 2, ["formats"] = new[] { "S32_LE · 2 ch" }, ["dsdNative"] = dsdNative ?? Array.Empty<int>()
        };
        if (volume is { } v) d["testVolume"] = v;
        return new[] { d };
    }

    private async Task<JsonDocument> Dacs() => JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/api/dacs"));

    private async Task<JsonElement> Dac() => (await Dacs()).RootElement.GetProperty("dacs")[0];

    private async Task<(int Status, string Text, Dictionary<string, string> Out)> Soap(string id, string service, string type, string action,
        Dictionary<string, string>? args = null, string ua = "")
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action} xmlns:u=\"{type}\">");
        foreach (var (k, v) in args ?? new()) sb.Append(CultureInfo.InvariantCulture, $"<{k}>{Xml.Esc(v)}</{k}>");
        sb.Append(CultureInfo.InvariantCulture, $"</u:{action}></s:Body></s:Envelope>");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/upnp/{id}/{service}/control") { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{type}#{action}\"");
        if (ua != "") req.Headers.TryAddWithoutValidation("User-Agent", ua);
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var o = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(text, @"<(\w+)>([^<]*)</\1>")) o[m.Groups[1].Value] = Xml.Unesc(m.Groups[2].Value);
        return ((int)res.StatusCode, text, o);
    }

    private Task<(int Status, string Text, Dictionary<string, string> Out)> Av(string id, string action, Dictionary<string, string>? args = null, string ua = "")
    {
        var a = new Dictionary<string, string> { ["InstanceID"] = "0" };
        foreach (var (k, v) in args ?? new()) a[k] = v;
        return Soap(id, "AVTransport", Avt, action, a, ua);
    }

    private async Task<string> State(string id) => (await Av(id, "GetTransportInfo")).Out["CurrentTransportState"];

    private static async Task<T> Until<T>(Func<Task<T?>> f, int ms = 8000) where T : struct
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        for (;;)
        {
            try { if (await f() is { } v) return v; } catch (Exception) { /* not yet */ }
            if (DateTime.UtcNow > end) throw new TimeoutException("timed out waiting");
            await Task.Delay(100);
        }
    }

    private static Task Until(Func<Task<bool>> f, int ms = 8000) => Until<bool>(async () => await f() ? true : null, ms);

    private string Url(string name) => $"{files.Base}/{name}";

    [Fact]
    public async Task ThePageIsInsideTheProgram()
    {
        var id = await Start(Dev("clock"));
        var html = await Http.GetStringAsync($"http://127.0.0.1:{port}/");
        Assert.Contains("<title>DAC Bridge</title>", html);
        var font = await Http.GetByteArrayAsync($"http://127.0.0.1:{port}/fonts/manrope.woff2");
        Assert.True(font.Length > 10000);
        // A DAC with no volume of its own: 100 to the apps, nothing to set on the page.
        Assert.False((await Dac()).GetProperty("volumeAvailable").GetBoolean());
        Assert.Equal("100", (await Soap(id, "RenderingControl", Rcs, "GetVolume", new() { ["InstanceID"] = "0", ["Channel"] = "Master" })).Out["CurrentVolume"]);
        using var post = await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"dsd\":\"dop\"}"));
        Assert.Equal("dop", (await Dac()).GetProperty("dsd").GetString());
        Assert.Equal(404, (int)(await Http.GetAsync($"http://127.0.0.1:{port}/../etc/passwd")).StatusCode);
        Assert.Contains("<title>Now Playing</title>", await Http.GetStringAsync($"http://127.0.0.1:{port}/now"));
    }

    private async Task<JsonElement> Post(string path, string json)
    {
        using var res = await Http.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    // ------------------------------------------------------------ Roon Bridge (a pretend one, as Roon's package lays it out)

    [Fact]
    public async Task RoonBridge_RunsBesideTheBridge_SharesTheDacs_AndStopsWithAllItStarted()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Start(Dev("clock"));
        var data = hosts[^1].Manager.Settings.Dir;
        var install = Path.Combine(data, "roon", "RoonBridge");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "VERSION"), "207101683\n2.71 (build 1683) production\nproduction\n");
        // Like Roon's start.sh: starts the real program and waits; a SIGTERM ends the script only.
        var start = Path.Combine(install, "start.sh");
        File.WriteAllText(start, """
            #!/bin/sh
            echo "$ROON_DATAROOT|$ROON_ID_DIR" > "$ROON_DATAROOT/env"
            sleep 300 &
            echo $! > "$ROON_DATAROOT/child"
            wait

            """);
        File.SetUnixFileMode(start, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await Post("/api/services", "{\"roon\":true}");
        await Until(async () => (await Dacs()).RootElement.GetProperty("services").GetProperty("roonStatus").GetString()!.StartsWith("running · 2.71 (build 1683)", StringComparison.Ordinal));
        var root = Path.Combine(data, "roon", "data");
        await Until(() => Task.FromResult(File.Exists(Path.Combine(root, "child"))));
        Assert.Equal($"{root}|{root}", File.ReadAllText(Path.Combine(root, "env")).Trim());
        int child = int.Parse(File.ReadAllText(Path.Combine(root, "child")).Trim(), CultureInfo.InvariantCulture);
        Assert.True(Native.Libc.Alive(child));
        // Shared while nothing plays through the bridge, so Roon can open the DAC.
        await Until(async () => !(await Dac()).GetProperty("exclusive").GetBoolean());

        // Switched off: the script and what it started stop; the DAC is held again.
        await Post("/api/services", "{\"roon\":false}");
        await Until(() => Task.FromResult(!Native.Libc.Alive(child)), 10000);
        await Until(async () => (await Dac()).GetProperty("exclusive").GetBoolean());
    }

    private static bool Has(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, program)));

    // ------------------------------------------------------------ Qobuz Connect through QobuzProxy (a pretend one)

    [Fact]
    public async Task Qobuz_GetsASpeakerPerDac_AimedAtTheBridge_AndIsNamedQobuz()
    {
        if (OperatingSystem.IsWindows() || !Has("python3") || !HasFfmpeg()) return;
        var id = await Start(Dev("clock"));
        var data = hosts[^1].Manager.Settings.Dir;
        // An "installed" QobuzProxy whose python notes how it was started and answers /api/status.
        // (A plain TCPServer: http.server.HTTPServer looks its own address up in DNS first, which can stall on macOS.)
        var venvBin = Path.Combine(data, "qobuz", "venv", "bin");
        Directory.CreateDirectory(venvBin);
        File.WriteAllText(Path.Combine(data, "qobuz", "installed-" + Qobuz.QobuzConnect.Version), "test");
        var python = Path.Combine(venvBin, "python");
        File.WriteAllText(python, """
            #!/bin/sh
            printf '%s\n' "$@" > "$QOBUZPROXY_DATA_DIR/args"
            exec python3 -c '
            import http.server, json, socketserver
            class H(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    body = json.dumps({"auth": {"authenticated": True, "email": "me@example.com"}, "speakers": []}).encode()
                    self.send_response(200); self.send_header("Content-Type", "application/json"); self.end_headers(); self.wfile.write(body)
                def log_message(self, *a): pass
            socketserver.TCPServer.allow_reuse_address = True
            socketserver.TCPServer(("127.0.0.1", 8689), H).serve_forever()
            '

            """);
        File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var sv = await Post("/api/services", "{\"qobuz\":true}");
        Assert.True(sv.GetProperty("qobuz").GetBoolean());
        Assert.True(sv.GetProperty("qobuzInstalled").GetBoolean());
        // QobuzProxy can take a while to start on a slow CI runner; a timeout shows the status it got to.
        var status = "";
        try { await Until(async () => (status = (await Dacs()).RootElement.GetProperty("services").GetProperty("qobuzStatus").GetString()!) == "ready · choose it in Qobuz · me@example.com", 20000); }
        catch (TimeoutException)
        {
            string direct;
            try { direct = await Http.GetStringAsync("http://127.0.0.1:8689/api/status"); } catch (Exception e) { direct = e.GetType().Name + ": " + e.Message; }
            Assert.Fail($"status \"{status}\"; the pretend QobuzProxy {(File.Exists(Path.Combine(data, "qobuz", "args")) ? "started" : "never started")}; its page says: {direct}");
        }

        var args = File.ReadAllLines(Path.Combine(data, "qobuz", "args"));
        Assert.Equal(["-m", "qobuz_proxy", "--config", Path.Combine(data, "qobuz", "config.yaml")], args);
        var yaml = File.ReadAllText(Path.Combine(data, "qobuz", "config.yaml"));
        Assert.Contains("name: \"Test DAC (Bridge)\"", yaml);
        Assert.Contains($"dlna_description_url: \"http://127.0.0.1:{port}/upnp/{id}/description.xml\"", yaml);
        Assert.Contains("dlna_fixed_volume: true", yaml);
        Assert.Contains("proxy_port: 7120", yaml);

        // A stream from a QobuzProxy speaker's proxy port is Qobuz's: it owns the DAC, by name.
        Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = "http://127.0.0.1:7120/track/1.flac", ["CurrentURIMetaData"] = "" },
            "Python/3.12 aiohttp/3.9.5")).Status);
        Assert.Equal("Qobuz", (await Dac()).GetProperty("control").GetProperty("owner").GetString());

        await Post("/api/services", "{\"qobuz\":false}");
        Assert.Equal("", (await Dacs()).RootElement.GetProperty("services").GetProperty("qobuzStatus").GetString());
    }

    // ------------------------------------------------------------ share when idle (Roon Bridge beside the bridge)

    [Fact]
    public async Task SharedDac_IsLetGoWhileIdle_AndTakenBackToPlay()
    {
        if (!HasFfmpeg()) return;
        var id = await Start(Dev("clock"));
        Assert.True((await Dac()).GetProperty("exclusive").GetBoolean());
        await Post($"/api/dacs/{id}/settings", "{\"share\":true}");
        await Until(async () => !(await Dac()).GetProperty("exclusive").GetBoolean());
        Assert.Equal(Audio.Sharing.Idle, (await Dac()).GetProperty("waiting").GetString());

        Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("c44.flac"), ["CurrentURIMetaData"] = "" }, Aud)).Status);
        Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Status);
        await Until(async () => await State(id) == "PLAYING");
        Assert.True((await Dac()).GetProperty("exclusive").GetBoolean());
        await Until(async () => await State(id) == "STOPPED", 6000);
        // Let go again once the grace (2 s here) is over.
        await Until(async () => !(await Dac()).GetProperty("exclusive").GetBoolean(), 6000);

        await Post($"/api/dacs/{id}/settings", "{\"share\":false}");
        await Until(async () => (await Dac()).GetProperty("exclusive").GetBoolean());

        // Held again: the DAC is set up afresh, even for a track at the rate it had before.
        await Task.Delay(2200);
        Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Status);
        await Until(async () => await State(id) == "PLAYING");
        await Until(async () => await State(id) == "STOPPED", 6000);
        Assert.Equal("OK", (await Av(id, "GetTransportInfo")).Out["CurrentTransportStatus"]);
    }
}

// Serves the test media, with Range support, and remembers the ranges asked for.
internal sealed class FileServer : IDisposable
{
    private readonly HttpListener listener = new();
    public string Base { get; }
    public List<string> Ranges { get; } = [];

    public FileServer(string dir)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Base = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Base + "/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch (Exception) { return; }
                _ = Task.Run(() => Serve(c, dir));
            }
        });
    }

    private void Serve(HttpListenerContext c, string dir)
    {
        try
        {
            var f = Path.Combine(dir, Path.GetFileName(c.Request.Url!.AbsolutePath));
            if (!File.Exists(f)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
            var buf = File.ReadAllBytes(f);
            c.Response.ContentType = f.EndsWith(".dsf", StringComparison.Ordinal) ? "audio/dsf" : "audio/flac";
            var range = c.Request.Headers["Range"];
            var m = Regex.Match(range ?? "", @"bytes=(\d+)-(\d*)");
            if (m.Success)
            {
                lock (Ranges) Ranges.Add(range!);
                long a = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = m.Groups[2].Value != "" ? Math.Min(long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), buf.Length - 1) : buf.Length - 1;
                c.Response.StatusCode = 206;
                c.Response.Headers["Content-Range"] = $"bytes {a}-{b}/{buf.Length}";
                c.Response.ContentLength64 = b - a + 1;
                c.Response.OutputStream.Write(buf, (int)a, (int)(b - a + 1));
            }
            else
            {
                c.Response.ContentLength64 = buf.Length;
                c.Response.OutputStream.Write(buf);
            }
            c.Response.Close();
        }
        catch (Exception) { try { c.Response.Abort(); } catch (Exception) { /* gone */ } }
    }

    public void Dispose() { try { listener.Stop(); listener.Close(); } catch (Exception) { /* closed */ } }
}
