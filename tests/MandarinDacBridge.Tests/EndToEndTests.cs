// The whole bridge, end to end, in-process: real ffmpeg, a little HTTP
// server standing in for Audirvana's or Mandarin's streams, SOAP from two
// controllers, and a DAC played in real time by the clock sink (or, on
// Linux, the real ALSA path on the "null" device).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace MandarinDacBridge.Tests;

public sealed class EndToEndTests(ITestOutputHelper output) : IAsyncLifetime
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

    private async Task<string> Start(object dev, bool clock = true, bool busy = false)
    {
        port = FreePort();
        var config = new Config
        {
            Port = port, DataDir = Directory.CreateTempSubdirectory("data-").FullName, BindIp = "127.0.0.1",
            LockGrace = TimeSpan.FromSeconds(2), Ffmpeg = "ffmpeg",
            TestDevices = JsonSerializer.Serialize(dev), TestSink = clock, TestSinkBusy = busy
        };
        var host = new BridgeHost(config);
        hosts.Add(host);
        await host.StartAsync();
        var v = await Until(async () => (await Dacs()).RootElement.GetProperty("dacs") is { } d && d.GetArrayLength() > 0 ? d[0] : (JsonElement?)null);
        return v.GetProperty("id").GetString()!;
    }

    private static object Dev(string spec, int[]? dsdNative = null, int[]? rates = null) => new[]
    {
        new Dictionary<string, object>
        {
            ["key"] = "test:" + spec, ["name"] = "Test DAC", ["manufacturer"] = "ALSA", ["model"] = "null", ["transport"] = "USB",
            ["usb"] = "0000:0000", ["spec"] = spec, ["rates"] = rates ?? new[] { 44100, 48000, 88200, 96000, 176400, 192000 },
            ["bits"] = new[] { 16, 24, 32 }, ["channels"] = 2, ["formats"] = new[] { "S32_LE · 2 ch" }, ["dsdNative"] = dsdNative ?? Array.Empty<int>()
        }
    };

    private async Task<JsonDocument> Dacs() => JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/api/dacs"));

    private async Task<JsonElement> Dac() => (await Dacs()).RootElement.GetProperty("dacs")[0];

    private async Task<(int Status, string Text, Dictionary<string, string> Out)> Soap(string id, string service, string type, string action,
        Dictionary<string, string>? args = null, string ua = "")
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action} xmlns:u=\"{type}\">");
        foreach (var (k, v) in args ?? new()) sb.Append($"<{k}>{Xml.Esc(v)}</{k}>");
        sb.Append($"</u:{action}></s:Body></s:Envelope>");
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
    public async Task PlaysInRealTime_KeepsOthersOut_Gapless_Seeks_Pauses_ChangesRate()
    {
        if (!HasFfmpeg()) return;
        var id = await Start(Dev("clock"));

        // Audirvana loads and plays a 96 kHz track with a 96 kHz one next (gapless).
        Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("a96.flac"), ["CurrentURIMetaData"] = "" }, Aud)).Status);
        Assert.Equal(200, (await Av(id, "SetNextAVTransportURI", new() { ["NextURI"] = Url("b96.flac"), ["NextURIMetaData"] = "" }, Aud)).Status);
        Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Status);
        await Until(async () => await State(id) == "PLAYING");

        // Mandarin (no User-Agent) tries to take over while it plays: kept out, with 705.
        var r = await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("c44.flac"), ["CurrentURIMetaData"] = "" });
        Assert.Equal(500, r.Status);
        Assert.Contains("<errorCode>705</errorCode>", r.Text);
        Assert.Contains("<errorCode>705</errorCode>", (await Av(id, "Stop")).Text);
        var v1 = await Dac();
        Assert.Equal("Audirvana", v1.GetProperty("control").GetProperty("owner").GetString());
        Assert.Equal("96 kHz · 24-bit · FLAC", v1.GetProperty("player").GetProperty("format").GetString());
        Assert.NotEqual(JsonValueKind.Null, v1.GetProperty("control").GetProperty("blocked").ValueKind);

        // The position follows the DAC's clock.
        await Task.Delay(1200);
        var pos = (await Av(id, "GetPositionInfo")).Out;
        Assert.Equal(Url("a96.flac"), pos["TrackURI"]);
        Assert.Matches(@"^0:00:0[12]$", pos["RelTime"]);
        Assert.Equal("0:00:03", pos["TrackDuration"]);

        // The next track takes over when the DAC gets to it; NextURI empties.
        await Until(async () => (await Av(id, "GetPositionInfo")).Out["TrackURI"] == Url("b96.flac"), 6000);
        var mi = (await Av(id, "GetMediaInfo")).Out;
        Assert.Equal(Url("b96.flac"), mi["CurrentURI"]);
        Assert.Equal("", mi.GetValueOrDefault("NextURI", ""));
        Assert.Equal("PLAYING", await State(id));

        // Seek, pause (the clock stops), resume.
        Assert.Equal(200, (await Av(id, "Seek", new() { ["Unit"] = "REL_TIME", ["Target"] = "0:00:01" }, Aud)).Status);
        await Av(id, "Pause", null, Aud);
        Assert.Equal("PAUSED_PLAYBACK", await State(id));
        await Task.Delay(300);
        var p1 = (await Av(id, "GetPositionInfo")).Out["RelTime"];
        await Task.Delay(700);
        Assert.Equal(p1, (await Av(id, "GetPositionInfo")).Out["RelTime"]);
        Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Status);
        await Until(async () => await State(id) == "STOPPED", 6000);

        // After the grace, Mandarin may play: 44.1 kHz, so the DAC changes rate.
        await Task.Delay(2200);
        Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("c44.flac"), ["CurrentURIMetaData"] = "" })).Status);
        Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" })).Status);
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("format").GetString() == "44.1 kHz · 16-bit · FLAC");
        Assert.Equal("a controller at 127.0.0.1", (await Dac()).GetProperty("control").GetProperty("owner").GetString());

        // A different-rate next track: the first plays out, the DAC switches, the second plays.
        Assert.Equal(200, (await Av(id, "SetNextAVTransportURI", new() { ["NextURI"] = Url("b96.flac"), ["NextURIMetaData"] = "" })).Status);
        await Until(async () =>
        {
            var p = (await Dac()).GetProperty("player");
            return p.GetProperty("format").GetString()!.StartsWith("96 kHz") && p.GetProperty("transport").GetString() == "PLAYING";
        }, 6000);
        Assert.Equal(200, (await Av(id, "Stop")).Status);
        Assert.Equal("STOPPED", await State(id));

        // The UPnP face: description and protocol info.
        var desc = await Http.GetStringAsync($"http://127.0.0.1:{port}/upnp/{id}/description.xml");
        Assert.Contains("MediaRenderer:1", desc);
        Assert.Contains("Test DAC (Bridge)", desc);
        var pi = await Soap(id, "ConnectionManager", "urn:schemas-upnp-org:service:ConnectionManager:1", "GetProtocolInfo");
        Assert.Contains("audio/flac", pi.Out["Sink"]);
        Assert.Contains("audio/L24;rate=96000", pi.Out["Sink"]);
    }

    [Fact]
    public async Task ADacAnotherProgramHolds_PlayIsRefused_AndThePageSaysSo()
    {
        port = FreePort();
        var config = new Config
        {
            Port = port, DataDir = Directory.CreateTempSubdirectory("data-").FullName, BindIp = "127.0.0.1",
            TestDevices = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["key"] = "busy", ["name"] = "Busy DAC", ["spec"] = "busy", ["rates"] = new[] { 44100 }, ["holderPid"] = 4242, ["holderName"] = "Audirvana" } }),
            TestSink = true, TestSinkBusy = true
        };
        var host = new BridgeHost(config);
        hosts.Add(host);
        await host.StartAsync();
        var v = await Until(async () => (await Dacs()).RootElement.GetProperty("dacs") is { } d && d.GetArrayLength() > 0 ? d[0] : (JsonElement?)null);
        Assert.False(v.GetProperty("exclusive").GetBoolean());
        Assert.Equal("Audirvana", v.GetProperty("holder").GetString());
        var id = v.GetProperty("id").GetString()!;
        await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = "http://127.0.0.1:9/x.flac", ["CurrentURIMetaData"] = "" }, Aud);
        Assert.Contains("<errorCode>701</errorCode>", (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Text);
    }

    [Fact]
    public async Task TheRealLinuxAlsaPath_HoldsTheDeviceAndPlaysThroughIt()
    {
        if (!OperatingSystem.IsLinux() || !HasFfmpeg() || !File.Exists("/usr/share/alsa/alsa.conf")) return;
        // ALSA's "null" device takes audio as fast as it is given: this checks the path, not the timing.
        var id = await Start(Dev("null"), clock: false);
        foreach (var f in new[] { "a96.flac", "c44.flac" })
        {
            Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url(f), ["CurrentURIMetaData"] = "" }, Aud)).Status);
            Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Status);
            await Until(async () => await State(id) == "STOPPED");
            Assert.Equal("OK", (await Av(id, "GetTransportInfo")).Out["CurrentTransportStatus"]);
        }
        Assert.Contains("S32_LE", (await Dac()).GetProperty("player").GetProperty("physical").GetString());
        lock (logs) Assert.Contains(logs, l => l.Contains("exclusive: the DAC is the bridge's"));
    }

    [Fact]
    public async Task DsdGoesAsDop_AnUnsupportedRateIsResampled_AndSubscribersHearAboutIt()
    {
        if (!HasFfmpeg()) return;
        // A DSD64 DSF of 0.5 s (silence pattern).
        File.WriteAllBytes(Path.Combine(tmp, "media", "song.dsf"), DsdTests.Dsf(bytesPerChannel: 2822400 / 8 / 2, fill: (_, _) => 0x69, pad: 0x69));
        var id = await Start(Dev("clock", dsdNative: [176400]));

        // Somewhere for events to arrive.
        var notes = new List<string>();
        using var cb = new HttpListener();
        int cbPort = FreePort();
        cb.Prefixes.Add($"http://127.0.0.1:{cbPort}/");
        cb.Start();
        _ = Task.Run(async () =>
        {
            while (cb.IsListening)
            {
                try
                {
                    var c = await cb.GetContextAsync();
                    using var sr = new StreamReader(c.Request.InputStream);
                    var body = await sr.ReadToEndAsync();
                    lock (notes) notes.Add(body);
                    c.Response.Close();
                }
                catch (Exception) { return; }
            }
        });

        using var sub = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), $"http://127.0.0.1:{port}/upnp/{id}/AVTransport/event");
        sub.Headers.TryAddWithoutValidation("CALLBACK", $"<http://127.0.0.1:{cbPort}/ev>");
        sub.Headers.TryAddWithoutValidation("NT", "upnp:event");
        sub.Headers.TryAddWithoutValidation("TIMEOUT", "Second-300");
        using var subRes = await Http.SendAsync(sub);
        Assert.Equal(200, (int)subRes.StatusCode);
        Assert.StartsWith("uuid:", subRes.Headers.GetValues("SID").First());
        bool Heard(string s) { lock (notes) return notes.Any(n => n.Contains(s)); }
        await Until(() => Task.FromResult(Heard("TransportState val=&quot;NO_MEDIA_PRESENT&quot;")));

        Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("song.dsf"), ["CurrentURIMetaData"] = "" }, Aud)).Status);
        await Av(id, "Play", new() { ["Speed"] = "1" }, Aud);
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("format").GetString() == "DSD64 · DoP at 176.4 kHz");
        Assert.Contains("bytes=0-65535", files.Ranges);
        Assert.Contains("bytes=92-", files.Ranges);
        await Until(() => Task.FromResult(Heard("TransportState val=&quot;PLAYING&quot;")));
        await Until(async () => await State(id) == "STOPPED", 5000);
        await Until(() => Task.FromResult(Heard("TransportState val=&quot;STOPPED&quot;")));

        // 352.8 kHz to a DAC that stops at 192 kHz: down to 176.4, in the same family.
        await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("hi.flac"), ["CurrentURIMetaData"] = "" }, Aud);
        await Av(id, "Play", new() { ["Speed"] = "1" }, Aud);
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("format").GetString()!.StartsWith("352.8 kHz → 176.4 kHz"));
        await Av(id, "Stop", null, Aud);
        cb.Stop();
    }

    [Fact]
    public async Task ThePageIsInsideTheProgram()
    {
        var id = await Start(Dev("clock"));
        var html = await Http.GetStringAsync($"http://127.0.0.1:{port}/");
        Assert.Contains("<title>DAC Bridge</title>", html);
        var font = await Http.GetByteArrayAsync($"http://127.0.0.1:{port}/fonts/manrope.woff2");
        Assert.True(font.Length > 10000);
        using var post = await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"dsd\":\"dop\"}"));
        Assert.Equal("dop", (await Dac()).GetProperty("dsd").GetString());
        Assert.Equal(404, (int)(await Http.GetAsync($"http://127.0.0.1:{port}/../etc/passwd")).StatusCode);
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
            c.Response.ContentType = f.EndsWith(".dsf") ? "audio/dsf" : "audio/flac";
            var range = c.Request.Headers["Range"];
            var m = Regex.Match(range ?? "", @"bytes=(\d+)-(\d*)");
            if (m.Success)
            {
                lock (Ranges) Ranges.Add(range!);
                long a = long.Parse(m.Groups[1].Value), b = m.Groups[2].Value != "" ? Math.Min(long.Parse(m.Groups[2].Value), buf.Length - 1) : buf.Length - 1;
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
