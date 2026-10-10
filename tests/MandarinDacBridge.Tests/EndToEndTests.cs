// The whole bridge, end to end, in-process: real ffmpeg, a little HTTP
// server standing in for Audirvana's or Mandarin's streams, SOAP from two
// controllers, and a DAC played in real time by the clock sink (or, on
// Linux, the real ALSA path on the "null" device).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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

    private async Task<string> Start(object dev, bool clock = true, bool busy = false, string soloist = "")
    {
        port = FreePort();
        var config = new Config
        {
            Port = port, DataDir = Directory.CreateTempSubdirectory("data-").FullName, BindIp = "127.0.0.1",
            LockGrace = TimeSpan.FromSeconds(2), Ffmpeg = "ffmpeg",
            TestDevices = JsonSerializer.Serialize(dev), TestSink = clock, TestSinkBusy = busy, Soloist = soloist
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
        Assert.Contains("<title>Now Playing</title>", await Http.GetStringAsync($"http://127.0.0.1:{port}/now"));
    }

    private async Task<JsonElement> Post(string path, string json)
    {
        using var res = await Http.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    // ------------------------------------------------------------ Squeezebox (a pretend Lyrion Music Server)

    // server → player: length (2) · opcode · payload
    private static async Task ToPlayer(NetworkStream s, string op, byte[] payload)
    {
        var b = new byte[2 + 4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)(4 + payload.Length));
        Encoding.ASCII.GetBytes(op, b.AsSpan(2));
        payload.CopyTo(b, 6);
        await s.WriteAsync(b);
    }

    private static byte[] Strm(char cmd, string path = "", int httpPort = 0, char format = 'f', char autostart = '1')
    {
        var head = Encoding.ASCII.GetBytes(path == "" ? "" : $"GET {path} HTTP/1.0\r\n\r\n");
        var b = new byte[24 + head.Length];
        b[0] = (byte)cmd; b[1] = (byte)autostart; b[2] = (byte)format;
        b[3] = b[4] = b[5] = (byte)'?'; b[6] = (byte)'1';
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), (ushort)httpPort);
        head.CopyTo(b, 24);
        return b;
    }

    // What the player sends: opcode · length (4) · payload, read in the background.
    private static async Task ReadPlayer(NetworkStream s, System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)> got, CancellationToken ct)
    {
        var head = new byte[8];
        try
        {
            for (;;)
            {
                await s.ReadExactlyAsync(head, ct);
                var data = new byte[BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4))];
                await s.ReadExactlyAsync(data, ct);
                got.Enqueue((Encoding.ASCII.GetString(head, 0, 4), data));
            }
        }
        catch (Exception) { /* closed */ }
    }

    private static List<string> Events(System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)> got) =>
        got.Where(x => x.Op == "STAT").Select(x => Encoding.ASCII.GetString(x.Data, 0, 4)).ToList();

    [Fact]
    public async Task Squeezebox_PlaysFromTheServer_Gapless_KeepsOthersOut()
    {
        if (!HasFfmpeg()) return;
        var id = await Start(Dev("clock"));
        var lms = new TcpListener(IPAddress.Loopback, 0);
        lms.Start();
        try
        {
            int lmsPort = ((IPEndPoint)lms.LocalEndpoint).Port;
            var sv = await Post("/api/services", $"{{\"squeezelite\":true,\"lmsServer\":\"127.0.0.1:{lmsPort}\"}}");
            Assert.True(sv.GetProperty("squeezelite").GetBoolean());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var player = await lms.AcceptTcpClientAsync(cts.Token);
            var s = player.GetStream();
            var got = new System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)>();
            _ = ReadPlayer(s, got, cts.Token);

            // HELO: a squeezelite-like player that takes FLAC, at the DAC's highest rate.
            await Until(() => Task.FromResult(got.Any(x => x.Op == "HELO")));
            var helo = got.First(x => x.Op == "HELO").Data;
            var caps = Encoding.ASCII.GetString(helo, 36, helo.Length - 36);
            Assert.Contains("Model=squeezelite", caps);
            Assert.Contains("MaxSampleRate=192000", caps);
            Assert.Contains(",flc", caps);
            Assert.Equal(0x02, helo[2] & 0x03);    // a locally administered MAC

            // The server asks its name.
            await ToPlayer(s, "setd", [0]);
            await Until(() => Task.FromResult(got.Any(x => x.Op == "SETD")));
            var setd = got.First(x => x.Op == "SETD").Data;
            Assert.Equal("Test DAC (Bridge)", Encoding.UTF8.GetString(setd, 1, setd.Length - 2));

            // Play a 2 s track; when it has been read, the next (same rate: gapless).
            var httpPort = new Uri(files.Base).Port;
            await ToPlayer(s, "strm", Strm('s', "/c44.flac", httpPort));
            await Until(() => Task.FromResult(Events(got).Contains("STMs")));
            Assert.Equal("STMc", Events(got)[0]);
            var dac = await Dac();
            Assert.Equal("PLAYING", dac.GetProperty("player").GetProperty("transport").GetString());
            Assert.Equal("Squeezebox server", dac.GetProperty("control").GetProperty("owner").GetString());
            Assert.Equal("44.1 kHz · 16-bit · FLAC", dac.GetProperty("player").GetProperty("format").GetString());
            Assert.StartsWith("connected to", dac.GetProperty("squeezebox").GetString());

            // Audirvana is kept out while the server plays.
            var r = await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("a96.flac"), ["CurrentURIMetaData"] = "" }, Aud);
            Assert.Contains("<errorCode>705</errorCode>", r.Text);

            await Until(() => Task.FromResult(Events(got).Contains("STMd")), 6000);
            await ToPlayer(s, "strm", Strm('s', "/c44.flac", httpPort));
            await Until(() => Task.FromResult(Events(got).Count(e => e == "STMs") == 2), 6000);
            Assert.Equal("PLAYING", await State(id));
            await Until(() => Task.FromResult(Events(got).Contains("STMu")), 8000);
            var ev = Events(got).Where(e => e != "STMt").ToList();
            Assert.Equal(["STMc", "STMs", "STMd", "STMc", "STMs", "STMd", "STMu"], ev);
            Assert.Equal("STOPPED", await State(id));

            // Status requests are answered with the server's timestamp; stop is answered with flushed.
            var t = Strm('t');
            BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(14), 0xC0FFEE);
            await ToPlayer(s, "strm", t);
            await Until(() => Task.FromResult(got.Any(x => x.Op == "STAT" && BinaryPrimitives.ReadUInt32BigEndian(x.Data.AsSpan(47)) == 0xC0FFEE)));
            await ToPlayer(s, "strm", Strm('s', "/a96.flac", httpPort));
            await Until(async () => await State(id) == "PLAYING");
            await ToPlayer(s, "strm", Strm('q'));
            await Until(() => Task.FromResult(Events(got).Contains("STMf")));
            await Until(async () => await State(id) == "STOPPED");
        }
        finally { lms.Stop(); }
    }

    // ------------------------------------------------------------ Spotify Connect (a pretend Soloist)

    // Soloist's WebSocket, played by the test: what the bridge sends comes into Commands.
    private sealed class FakeSoloistSocket : IDisposable
    {
        private readonly HttpListener listener = new();
        private WebSocket? socket;
        public int Port { get; }
        public System.Collections.Concurrent.ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeSoloistSocket()
        {
            Port = FreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                Connected.TrySetResult();
                var buf = new byte[8192];
                try
                {
                    for (;;)
                    {
                        var r = await socket.ReceiveAsync(buf, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        Commands.Enqueue(Encoding.UTF8.GetString(buf, 0, r.Count));
                    }
                }
                catch (Exception) { /* closed */ }
            });
        }

        public Task Send(string json) => socket!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        public void Dispose() { try { listener.Stop(); } catch (Exception) { /* closing */ } }
    }

    private static string Item(string name) => """
        {"uri":"spotify:track:x","entity_type":"track","decorations":{"identity":{"name":"NAME"},
         "visual_identity":{"cover":[{"url":"https://i.example/s.jpg","size":"small"},{"url":"https://i.example/l.jpg","size":"large"}]},
         "parent":{"entity":{"uri":"spotify:album:y","entity_type":"album","decorations":{"identity":{"name":"Kind of Blue"}}}},
         "creators":[{"uri":"spotify:artist:z","entity_type":"artist","decorations":{"identity":{"name":"Miles Davis"}}},
                     {"uri":"spotify:artist:w","entity_type":"artist","decorations":{"identity":{"name":"John Coltrane"}}}],
         "playback":{"duration_ms":562000}}}
        """.Replace("NAME", name);

    private static bool Has(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, program)));

    [Fact]
    public async Task Spotify_ThroughSoloist_PlaysItsSound_ShowsWhatsPlaying_KeepsOthersOut()
    {
        if (!HasFfmpeg() || !OperatingSystem.IsLinux() || !Has("pulseaudio") || !Has("pacat")) return;
        using var fake = new FakeSoloistSocket();
        // A pretend soloist: notes how it was started, says where its WebSocket is, and plays a tone
        // through the PulseAudio it was pointed at (the bridge's private one).
        var tone = Path.Combine(tmp, "tone.raw");
        var make = Process.Start(new ProcessStartInfo("ffmpeg", $"-loglevel error -y -f lavfi -i sine=f=440:r=44100:d=30 -ac 2 -f s16le {tone}"))!;
        make.WaitForExit();
        var script = Path.Combine(tmp, "soloist");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            data=""; prev=""
            for a in "$@"; do [ "$prev" = "--data-dir" ] && data="$a"; prev="$a"; done
            printf '%s\n' "$@" > "$data/args"
            echo "$XDG_RUNTIME_DIR|$PULSE_SERVER" > "$data/env"
            echo 127.0.0.1 > "$data/ws.addr"
            echo {{fake.Port}} > "$data/ws.port"
            pacat --playback --format=s16le --rate=44100 --channels=2 --raw {{tone}}
            exec sleep 60

            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var id = await Start(Dev("clock"), soloist: script);

        // Switched on, with a key: the key is kept, and never sent back.
        using (var res = await Http.PostAsync($"http://127.0.0.1:{port}/api/services",
                   new StringContent("{\"spotify\":true,\"soloistKey\":\"test-key-123\"}", Encoding.UTF8, "application/json")))
        {
            var text = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("test-key-123", text);
            Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("soloistKey").GetBoolean());
        }
        Assert.DoesNotContain("test-key-123", await Http.GetStringAsync($"http://127.0.0.1:{port}/api/dacs"));
        await fake.Connected.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var data = Path.Combine(hosts[^1].Manager.Settings.Dir, "soloist", id, "data");
        var args = File.ReadAllLines(Path.Combine(data, "args"));
        Assert.Equal("test-key-123", args[Array.IndexOf(args, "--api-key") + 1]);
        Assert.Equal("Test DAC (Bridge)", args[Array.IndexOf(args, "--device-name") + 1]);
        Assert.Equal("127.0.0.1:0", args[Array.IndexOf(args, "--ws") + 1]);
        Assert.Equal("100", args[Array.IndexOf(args, "--initial-volume") + 1]);
        Assert.Matches(@"^/.*mdb-\d+-dac-\w+\|unix:/.*/pulse/native$", File.ReadAllText(Path.Combine(data, "env")).Trim());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(hosts[^1].Manager.Settings.Dir, "settings.json")));

        // Spotify plays: the DAC plays its sound, with what Spotify says is playing.
        await fake.Send("{\"type\":\"auth_state\",\"logged_in\":true,\"is_active\":true,\"device_name\":\"Test DAC (Bridge)\"}");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await fake.Send($$"""{"type":"playback_state","status":"playing","item":{{Item("So What")}},"position":{"position_ms":61000,"timestamp_ms":{{now}},"speed":1.0},"volume":100,"is_active":true}""");
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("transport").GetString() == "PLAYING");
        var dac = await Dac();
        var p = dac.GetProperty("player");
        Assert.Equal("Spotify", dac.GetProperty("control").GetProperty("owner").GetString());
        Assert.Equal("44.1 kHz · SPOTIFY", p.GetProperty("format").GetString());
        Assert.Equal("So What", p.GetProperty("title").GetString());
        Assert.Equal("Miles Davis, John Coltrane", p.GetProperty("artist").GetString());
        Assert.Equal("Kind of Blue", p.GetProperty("album").GetString());
        Assert.Equal("https://i.example/l.jpg", p.GetProperty("art").GetString());
        Assert.Equal(562, p.GetProperty("duration").GetInt64());
        Assert.InRange(p.GetProperty("position").GetInt64(), 61, 63);
        Assert.Equal("playing", dac.GetProperty("spotify").GetString());
        // Soloist's sound (the pretend one's tone, through the private PulseAudio) reaches the DAC: its clock runs.
        var renderer = hosts[^1].Manager.Get(id)!.Renderer;
        await Until(() => Task.FromResult(renderer.Position() > 1.0), 6000);

        // The volume stays at 100% (bit-perfect).
        await fake.Send("{\"type\":\"volume_changed\",\"volume\":40}");
        await Until(() => Task.FromResult(fake.Commands.Any(c => c.Contains("\"set_volume\"") && c.Contains("100"))));

        // Audirvana is kept out while Spotify plays; paused and stopped in Spotify, paused and stopped here.
        Assert.Contains("<errorCode>705</errorCode>", (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("c44.flac"), ["CurrentURIMetaData"] = "" }, Aud)).Text);
        await fake.Send("{\"type\":\"playback_changed\",\"status\":\"paused\"}");
        await Until(async () => await State(id) == "PAUSED_PLAYBACK");
        await fake.Send("{\"type\":\"playback_changed\",\"status\":\"playing\"}");
        await Until(async () => await State(id) == "PLAYING");
        await fake.Send("{\"type\":\"track_changed\",\"item\":" + Item("Freddie Freeloader") + "}");
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("title").GetString() == "Freddie Freeloader");
        await fake.Send("{\"type\":\"playback_changed\",\"status\":\"idle\"}");
        await Until(async () => await State(id) == "STOPPED");

        // Audirvana plays (after Spotify's grace); Spotify starting then is kept out, and told to pause.
        await Task.Delay(2200);
        Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("a96.flac"), ["CurrentURIMetaData"] = "" }, Aud)).Status);
        Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Status);
        await Until(async () => await State(id) == "PLAYING");
        await fake.Send("{\"type\":\"playback_changed\",\"status\":\"playing\"}");
        await Until(() => Task.FromResult(fake.Commands.Any(c => c.Contains("\"pause\""))));
        Assert.Equal("Audirvana", (await Dac()).GetProperty("control").GetProperty("owner").GetString());
        Assert.StartsWith("kept out", (await Dac()).GetProperty("spotify").GetString());
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
