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
    private const string Real = "192.168.1.20:9330";

    private static Dictionary<string, string> Kept(string address) =>
        new(StringComparer.Ordinal) { ["core-1"] = "tok-1", ["core-1|host"] = address };

    [Fact]
    public void TheToken_GoesToTheCoreAtTheAddressItWasGivenAt()
    {
        Assert.Equal(new RoonTokens.Grant("tok-1", false, false), RoonTokens.For(Kept(Real), "core-1", Real));
        Assert.Equal(Real, RoonTokens.Address("192.168.1.20", 9330));
    }

    [Theory]
    [InlineData("192.168.1.66:9330")]    // another machine
    [InlineData("192.168.1.20:9999")]    // another program on the Core's machine
    public void TheToken_IsntSent_ToACoreAnsweringFromAnotherAddress(string address)
    {
        var all = Kept(Real);
        Assert.Equal(new RoonTokens.Grant("", true, false), RoonTokens.For(all, "core-1", address));
        Assert.Equal(Real, all["core-1|host"]);    // nor is that address remembered
    }

    [Fact]
    public void NoToken_NothingIsSent_AndNothingIsBound()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Equal(new RoonTokens.Grant("", false, false), RoonTokens.For(all, "core-1", Real));
        Assert.Empty(all);
    }

    // Enabled in one Core, the bridge leaves any other, whatever id it gives.
    [Theory]
    [InlineData("core-2")]
    [InlineData("")]
    [InlineData("core-1|host")]
    public void ACoreTheBridgeIsntEnabledIn_IsLeft_WhenItIsEnabledInAnother(string coreId)
    {
        Assert.Equal(new RoonTokens.Grant("", true, false), RoonTokens.For(Kept(Real), coreId, "192.168.1.66:9330"));
        // Not when its token has no address yet, nor when the bridge isn't enabled anywhere.
        Assert.Equal(new RoonTokens.Grant("", false, false), RoonTokens.For(new(StringComparer.Ordinal) { ["core-1"] = "tok-1" }, coreId, "192.168.1.66:9330"));
        Assert.Equal(new RoonTokens.Grant("", false, false), RoonTokens.For(new(StringComparer.Ordinal) { ["core-1|host"] = Real }, coreId, "192.168.1.66:9330"));
    }

    [Fact]
    public void Forgetting_KeepsTheTokens_AndDropsTheirAddresses()
    {
        var all = Kept(Real);
        all["core-2"] = "tok-2";
        Assert.True(RoonTokens.Forget(all));
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["core-1"] = "tok-1", ["core-2"] = "tok-2" }, all);
        Assert.False(RoonTokens.Forget(all));
        Assert.Equal(new RoonTokens.Grant("tok-1", false, true), RoonTokens.For(all, "core-1", "192.168.1.30:9330"));
    }

    [Fact]
    public void ATokenKeptBeforeAddressesWere_IsBoundToTheFirstCoreItGoesTo()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal) { ["core-1"] = "tok-1" };
        Assert.Equal(new RoonTokens.Grant("tok-1", false, true), RoonTokens.For(all, "core-1", Real));
        Assert.Equal(Real, all["core-1|host"]);
        Assert.Equal(new RoonTokens.Grant("", true, false), RoonTokens.For(all, "core-1", "192.168.1.66:9330"));
    }

    [Fact]
    public void ATokenTheCoreGives_IsKeptWithItsAddress()
    {
        var all = Kept(Real);
        Assert.True(RoonTokens.Keep(all, "core-1", "tok-2", "192.168.1.30:9330"));
        Assert.Equal("tok-2", RoonTokens.For(all, "core-1", "192.168.1.30:9330").Token);
        Assert.Equal("", RoonTokens.For(all, "core-1", Real).Token);
    }

    // A Core that names itself "core-1|host" would otherwise write core-1's address with its "token".
    [Fact]
    public void AnIdThatWouldNameAnotherIdsAddress_IsNoCore()
    {
        var all = Kept(Real);
        Assert.False(RoonTokens.Keep(all, "core-1|host", "192.168.1.66:9330", "192.168.1.66:9330"));
        Assert.False(RoonTokens.Keep(all, "", "tok-2", "192.168.1.66:9330"));
        Assert.Equal(Kept(Real), all);
        Assert.Equal(new RoonTokens.Grant("", true, false), RoonTokens.For(all, "core-1|host", Real));
    }

    [Fact]
    public void OfTheCoresThatAnswer_TheOneATokenWasGivenAt_ComesFirst()
    {
        List<(string, int, string)> found = [("192.168.1.66", 9330, "first"), ("192.168.1.20", 9999, "same machine"), ("192.168.1.20", 9330, "kept")];
        Assert.Equal(["kept", "first", "same machine"], RoonTokens.Prefer(found, Kept(Real)).Select(c => c.Name));
        Assert.Equal(["first", "same machine", "kept"], RoonTokens.Prefer(found, new(StringComparer.Ordinal)).Select(c => c.Name));
        // Only an id's own address counts, not a value under a key that names none.
        var planted = new Dictionary<string, string>(StringComparer.Ordinal) { ["x|host|host"] = "192.168.1.66:9330" };
        Assert.Equal(["first", "same machine", "kept"], RoonTokens.Prefer(found, planted).Select(c => c.Name));
        List<(string, int, string)> later = [("192.168.1.20", 9330, "kept"), ("192.168.1.66", 9330, "planted")];
        Assert.Equal(["kept", "planted"], RoonTokens.Prefer(later, planted).Select(c => c.Name));
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
        public string RegistersAs = "core-1";       // the core_id it gives in Registered

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
                    await Send(ws, "CONTINUE", "Registered", id, $$"""{"core_id":"{{RegistersAs}}","token":"tok-1","display_name":"Test Core"}""");
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

    // Before 1.1.22 a Core claiming the id the token was given for got it, wherever it answered from. Now a
    // Core other than the one the bridge was enabled in isn't even registered with: it could say the bridge
    // was enabled and have its "token" kept, at its address.
    [Theory]
    [InlineData("core-1")]
    [InlineData("core-2")]
    public async Task Roon_ACoreOtherThanTheOneEnabled_IsLeft_AndGetsNoToken(string enabledIn)
    {
        using var core = new FakeRoonCore();
        var before = new Dictionary<string, string>(StringComparer.Ordinal) { [enabledIn] = "tok-0", [enabledIn + "|host"] = "192.168.1.20:9330" };
        var tokens = await RoonWithTokens(core, before);
        await Until(async () => await RoonStatus() == $"Test Core at 127.0.0.1:{core.Port} isn't the Roon Core the bridge was enabled in: looking for that one (if it has moved, switch Roon now playing off and on)");

        Assert.DoesNotContain(core.Got, g => g.Contains("registry:1/register", StringComparison.Ordinal));
        Assert.DoesNotContain(core.Got, g => g.Contains("tok-0", StringComparison.Ordinal));
        Assert.Equal(before, OwnerOnlyFile.ReadStrings(tokens));
    }

    // The owner's way to take up a Core that has moved: Roon now playing switched off and on. The token is
    // kept, so Roon needn't be asked again.
    [Fact]
    public async Task Roon_SwitchedOffAndOn_TheCoreThatMoved_IsTakenUp()
    {
        using var core = new FakeRoonCore();
        var tokens = await RoonWithTokens(core, new(StringComparer.Ordinal) { ["core-1"] = "tok-0", ["core-1|host"] = "192.168.1.20:9330" });
        await Until(async () => (await RoonStatus())!.Contains("isn't the Roon Core the bridge was enabled in", StringComparison.Ordinal));
        await Post("/api/services", "{\"roonApi\":false}");
        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal) { ["core-1"] = "tok-0" }, OwnerOnlyFile.ReadStrings(tokens));

        await Post("/api/services", "{\"roonApi\":true}");
        await Until(async () => await RoonStatus() == "connected to Test Core");
        Assert.Contains(core.Got, g => g.StartsWith("MOO/1 REQUEST com.roonlabs.registry:1/register", StringComparison.Ordinal) && g.Contains("\"token\":\"tok-0\"", StringComparison.Ordinal));
        var kept = OwnerOnlyFile.ReadStrings(tokens);
        Assert.Equal(("tok-1", $"127.0.0.1:{core.Port}"), (kept["core-1"], kept["core-1|host"]));
    }

    // Registered under another id than the Core gave as its own, a token would bind that id here: "core-1|host"
    // would even rewrite core-1's address with the "token".
    [Theory]
    [InlineData("core-1|host")]
    [InlineData("core-2")]
    public async Task Roon_ACoreRegisteringAsAnotherCore_KeepsNothing(string registersAs)
    {
        using var core = new FakeRoonCore { RegistersAs = registersAs };
        var before = new Dictionary<string, string>(StringComparer.Ordinal) { ["core-1"] = "tok-0", ["core-1|host"] = $"127.0.0.1:{core.Port}" };
        var tokens = await RoonWithTokens(core, before);
        await Until(async () => await RoonStatus() == "connected to Test Core");
        Assert.Equal(before, OwnerOnlyFile.ReadStrings(tokens));
    }

    private async Task<string> RoonWithTokens(FakeRoonCore core, Dictionary<string, string> kept)
    {
        await Start(Dev("clock"), roonCore: $"127.0.0.1:{core.Port}");
        var tokens = Path.Combine(hosts[^1].Manager.Settings.Dir, "roon-api.json");
        OwnerOnlyFile.WriteStrings(tokens, new(kept, StringComparer.Ordinal));
        await Post("/api/services", "{\"roonApi\":true}");
        return tokens;
    }

    private async Task<string?> RoonStatus() => (await Dacs()).RootElement.GetProperty("services").GetProperty("roonApiStatus").GetString();
}
