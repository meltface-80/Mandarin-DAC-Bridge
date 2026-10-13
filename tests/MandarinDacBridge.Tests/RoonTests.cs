// RoonTests.cs — the extension's token goes only to the Core it was given at (RoonTokens.cs): anything on
// the network can answer discovery and claim the real Core's id. Roon now playing end to end, with a
// pretend Core speaking MOO: a part of EndToEndTests (dev/quality.md), using its helpers.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using MandarinDacBridge.Roon;
using Xunit;

namespace MandarinDacBridge.Tests;

public class RoonTokenTests
{
    private static Dictionary<string, string> Kept(string host) =>
        new(StringComparer.Ordinal) { ["core-1"] = "tok-1", ["core-1|host"] = host };

    [Fact]
    public void TheToken_GoesToTheCoreAtTheAddressItWasGivenAt()
    {
        Assert.Equal("tok-1", RoonTokens.For(Kept("192.168.1.20"), "core-1", "192.168.1.20", out var elsewhere));
        Assert.False(elsewhere);
    }

    [Fact]
    public void TheToken_IsntSent_ToACoreAnsweringFromAnotherAddress()
    {
        var all = Kept("192.168.1.20");
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.66", out var elsewhere));
        Assert.True(elsewhere);
        Assert.Equal("192.168.1.20", all["core-1|host"]);    // the impostor isn't remembered either
    }

    [Fact]
    public void NoToken_NothingIsSent_AndNothingIsBound()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.20", out var elsewhere));
        Assert.False(elsewhere);
        Assert.Empty(all);
    }

    [Fact]
    public void ATokenKeptBeforeAddressesWere_IsBoundToTheFirstCoreItGoesTo()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal) { ["core-1"] = "tok-1" };
        Assert.Equal("tok-1", RoonTokens.For(all, "core-1", "192.168.1.20", out _));
        Assert.Equal("192.168.1.20", all["core-1|host"]);
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.66", out var elsewhere));
        Assert.True(elsewhere);
    }

    [Fact]
    public void ATokenTheCoreGives_IsKeptWithItsAddress()
    {
        var all = Kept("192.168.1.20");
        RoonTokens.Keep(all, "core-1", "tok-2", "192.168.1.30");
        Assert.Equal("tok-2", RoonTokens.For(all, "core-1", "192.168.1.30", out _));
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.20", out _));
    }

    [Fact]
    public void OfTheCoresThatAnswer_TheOneATokenWasGivenAt_ComesFirst()
    {
        List<(string, int, string)> found = [("192.168.1.66", 9330, "first"), ("192.168.1.20", 9330, "kept"), ("192.168.1.70", 9330, "other")];
        Assert.Equal(["kept", "first", "other"], RoonTokens.Prefer(found, Kept("192.168.1.20")).Select(c => c.Name));
        Assert.Equal(["first", "kept", "other"], RoonTokens.Prefer(found, new(StringComparer.Ordinal)).Select(c => c.Name));
    }
}

public sealed partial class EndToEndTests
{
    // ------------------------------------------------------------ Roon now playing (a pretend Roon Core, speaking MOO)

    private sealed class FakeRoonCore : IDisposable
    {
        private readonly HttpListener http = new();
        public int Port { get; } = FreePort();
        public System.Collections.Concurrent.ConcurrentQueue<string> Got { get; } = new();
        public string Zone = "";

        public FakeRoonCore()
        {
            http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            http.Start();
            _ = Task.Run(async () =>
            {
                while (http.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await http.GetContextAsync(); } catch (Exception) { return; }
                    if (c.Request.Url!.AbsolutePath.StartsWith("/api/image/", StringComparison.Ordinal))
                    {
                        Got.Enqueue("image " + c.Request.Url.PathAndQuery);
                        c.Response.ContentType = "image/jpeg";
                        c.Response.OutputStream.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9 });
                        c.Response.Close();
                        continue;
                    }
                    if (!c.Request.IsWebSocketRequest || c.Request.Url.AbsolutePath != "/api") { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                    var ws = (await c.AcceptWebSocketAsync(null)).WebSocket;
                    _ = Serve(ws);
                }
            });
        }

        private static Task Send(WebSocket ws, string verb, string name, int id, string? body = null)
        {
            var head = $"MOO/1 {verb} {name}\nRequest-Id: {id}\n" + (body != null ? $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\nContent-Type: application/json\n" : "") + "\n";
            return ws.SendAsync(Encoding.UTF8.GetBytes(head + (body ?? "")), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private async Task Serve(WebSocket ws)
        {
            var buf = new byte[65536];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                var text = Encoding.UTF8.GetString(buf, 0, r.Count);
                Got.Enqueue(text);
                var first = text.Split('\n')[0];
                var id = int.Parse(Regex.Match(text, @"Request-Id: (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
                if (first.EndsWith("registry:1/info", StringComparison.Ordinal))
                    await Send(ws, "COMPLETE", "Success", id, """{"core_id":"core-1","display_name":"Test Core","display_version":"2.0"}""");
                else if (first.EndsWith("registry:1/register", StringComparison.Ordinal))
                {
                    // As if enabled in Roon's Settings → Extensions; then the Core pings.
                    await Send(ws, "CONTINUE", "Registered", id, """{"core_id":"core-1","token":"tok-1","display_name":"Test Core"}""");
                    await Send(ws, "REQUEST", "com.roonlabs.ping:1/ping", 900);
                }
                else if (first.EndsWith("transport:2/subscribe_zones", StringComparison.Ordinal))
                {
                    await Send(ws, "CONTINUE", "Subscribed", id, "{\"zones\":[" + Zone + "]}");
                    await Send(ws, "CONTINUE", "Changed", id, """{"zones_seek_changed":[{"zone_id":"z1","seek_position":100}]}""");
                }
            }
        }

        public void Dispose() { try { http.Stop(); } catch (Exception) { /* closing */ } }
    }

    [Fact]
    public async Task Roon_NowPlaying_ComesFromTheCore_ForTheDacsZone()
    {
        using var core = new FakeRoonCore
        {
            Zone = """
                {"zone_id":"z1","display_name":"Test DAC (Bridge)","state":"playing",
                 "outputs":[{"output_id":"o1","zone_id":"z1","display_name":"Test DAC (Bridge)"}],
                 "now_playing":{"seek_position":61,"length":562,"image_key":"img1",
                   "three_line":{"line1":"So What","line2":"Miles Davis","line3":"Kind of Blue"}}}
                """
        };
        var id = await Start(Dev("clock"), roonCore: $"127.0.0.1:{core.Port}");
        await Post("/api/services", "{\"roonApi\":true}");
        await Until(async () => (await Dacs()).RootElement.GetProperty("services").GetProperty("roonApiStatus").GetString() == "connected to Test Core");
        Assert.Equal(["Test DAC (Bridge)"], (await Dacs()).RootElement.GetProperty("services").GetProperty("roonZones").EnumerateArray().Select(z => z.GetString()!).ToArray());

        // The bridge isn't playing this DAC: what Roon plays to it is shown (Roon Bridge's case).
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("title").GetString() == "So What");
        var dac = await Dac();
        var p = dac.GetProperty("player");
        Assert.Equal(("PLAYING", "Miles Davis", "Kind of Blue", 562L), (p.GetProperty("transport").GetString(), p.GetProperty("artist").GetString(), p.GetProperty("album").GetString(), p.GetProperty("duration").GetInt64()));
        Assert.InRange(p.GetProperty("position").GetInt64(), 100, 102);    // the seek that came after
        Assert.Equal("Roon", dac.GetProperty("control").GetProperty("owner").GetString());
        Assert.Equal("Test DAC (Bridge)", dac.GetProperty("roonZone").GetString());
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9 }, await Http.GetByteArrayAsync($"http://127.0.0.1:{port}{p.GetProperty("art").GetString()}"));
        Assert.Contains(core.Got, g => g.StartsWith("image /api/image/img1?scale=fit", StringComparison.Ordinal));

        // Registered as an extension; the Core's ping answered; the token kept, its owner's only.
        Assert.Contains(core.Got, g => g.StartsWith("MOO/1 REQUEST com.roonlabs.registry:1/register", StringComparison.Ordinal) && g.Contains("\"extension_id\":\"com.musicd.mandarin-dac-bridge\""));
        await Until(() => Task.FromResult(core.Got.Any(g => g.StartsWith("MOO/1 COMPLETE Success", StringComparison.Ordinal) && g.Contains("Request-Id: 900"))));
        var tokens = Path.Combine(hosts[^1].Manager.Settings.Dir, "roon-api.json");
        Assert.Contains("\"core-1\":\"tok-1\"", File.ReadAllText(tokens));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, OperatingSystem.IsWindows() ? UnixFileMode.UserRead | UnixFileMode.UserWrite : File.GetUnixFileMode(tokens));

        // Roon through the Squeezebox player: the bridge plays, Roon's Squeezebox support says nothing of the
        // track, and the zone fills it in.
        if (HasFfmpeg())
        {
            var lms = new TcpListener(IPAddress.Loopback, 0);
            lms.Start();
            try
            {
                await Post("/api/services", $"{{\"squeezelite\":true,\"lmsServer\":\"127.0.0.1:{((IPEndPoint)lms.LocalEndpoint).Port}\"}}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var player = await lms.AcceptTcpClientAsync(cts.Token);
                var st = player.GetStream();
                var got = new System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)>();
                _ = ReadPlayer(st, got, cts.Token);
                await Until(() => Task.FromResult(got.Any(x => x.Op == "HELO")));
                await ToPlayer(st, "strm", Strm('s', "/a96.flac", new Uri(files.Base).Port));
                await Until(async () => (await Dac()) is var d && d.GetProperty("control").GetProperty("owner").GetString() == "Roon (Squeezebox)"
                                        && d.GetProperty("player").GetProperty("format").GetString() != "");
                var sq = (await Dac()).GetProperty("player");
                Assert.Equal(("So What", "Miles Davis", "96 kHz · 24-bit · FLAC"), (sq.GetProperty("title").GetString(), sq.GetProperty("artist").GetString(), sq.GetProperty("format").GetString()));
                await ToPlayer(st, "strm", Strm('q'));
                await Until(async () => await State(id) == "STOPPED");
            }
            finally { lms.Stop(); }
        }

        // Another zone chosen on the DAC's page: this one's now playing is no longer the DAC's.
        await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"roonZone\":\"Kitchen\"}"));
        await Until(async () => (await Dac()).GetProperty("roonZone").GetString() == "");
        Assert.NotEqual("So What", (await Dac()).GetProperty("player").GetProperty("title").GetString());
    }

    // Before 1.1.22 a Core claiming the id the token was given for got it, wherever it answered from.
    [Fact]
    public async Task Roon_TheToken_IsntSent_ToACoreAtAnotherAddress()
    {
        using var core = new FakeRoonCore();
        await Start(Dev("clock"), roonCore: $"127.0.0.1:{core.Port}");
        var tokens = Path.Combine(hosts[^1].Manager.Settings.Dir, "roon-api.json");
        OwnerOnlyFile.WriteStrings(tokens, new(StringComparer.Ordinal) { ["core-1"] = "tok-0", ["core-1|host"] = "192.168.1.20" });
        await Post("/api/services", "{\"roonApi\":true}");
        await Until(async () => (await Dacs()).RootElement.GetProperty("services").GetProperty("roonApiStatus").GetString() == "connected to Test Core");

        var register = Assert.Single(core.Got, g => g.StartsWith("MOO/1 REQUEST com.roonlabs.registry:1/register", StringComparison.Ordinal));
        Assert.DoesNotContain("tok-0", register, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\"", register, StringComparison.Ordinal);
        // Enabled again at this address: the token it gave is kept with it.
        var kept = OwnerOnlyFile.ReadStrings(tokens);
        Assert.Equal(("tok-1", "127.0.0.1"), (kept["core-1"], kept["core-1|host"]));
    }
}
