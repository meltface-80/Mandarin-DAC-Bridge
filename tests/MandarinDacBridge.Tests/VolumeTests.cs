// VolumeTests.cs — the DAC's own volume, end to end: set by the apps (UPnP), the page and a
// Squeezebox server, or left fixed. Part of EndToEndTests (same bridge, same helpers) until those
// move to a shared fixture (dev/quality.md).
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests
{
    // ------------------------------------------------------------ the DAC's own volume

    private const string Rcs = "urn:schemas-upnp-org:service:RenderingControl:1";

    private async Task<string> Volume(string id) =>
        (await Soap(id, "RenderingControl", Rcs, "GetVolume", new() { ["InstanceID"] = "0", ["Channel"] = "Master" })).Out["CurrentVolume"];

    private Task<(int Status, string Text, Dictionary<string, string> Out)> SetVolume(string id, string v, string ua = Aud) =>
        Soap(id, "RenderingControl", Rcs, "SetVolume", new() { ["InstanceID"] = "0", ["Channel"] = "Master", ["DesiredVolume"] = v }, ua);

    // A pretend LMS's JSON-RPC: what the bridge sets its volume to ("mixer volume"), and whether it answers
    // at all (Roon's Squeezebox support has none).
    private sealed class FakeLmsJson : IDisposable
    {
        private readonly HttpListener http = new();
        public int Port { get; } = FreePort();
        public System.Collections.Concurrent.ConcurrentQueue<string> Volumes { get; } = new();
        public volatile bool Answers = true;

        public FakeLmsJson()
        {
            http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            http.Start();
            _ = Task.Run(async () =>
            {
                while (http.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await http.GetContextAsync(); } catch (Exception) { return; }    // stopped
                    if (!Answers) { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                    using var doc = JsonDocument.Parse(await new StreamReader(c.Request.InputStream).ReadToEndAsync());
                    var cmd = doc.RootElement.GetProperty("params")[1];
                    if (cmd[0].GetString() == "mixer" && cmd[1].GetString() == "volume") Volumes.Enqueue(cmd[2].GetString()!);
                    c.Response.OutputStream.Write("{\"id\":1,\"result\":{}}"u8);
                    c.Response.Close();
                }
            });
        }

        public void Dispose() { try { http.Stop(); } catch (Exception) { /* closing */ } }
    }

    // What LMS sends a squeezeplay player for a volume (audg's new gains, 16.16), worked out by hand with LMS's
    // own formulas (Slim::Player::SqueezePlay::getVolumeParameters, Squeezebox2::getVolume and dBToFixed).
    private static readonly Dictionary<int, uint> LmsGain = new() { [100] = 65536, [90] = 37120, [60] = 6656, [45] = 2816, [30] = 1230 };

    [Fact]
    public async Task Volume_IsTheDacsOwn_SetByTheAppsThePageAndTheServer_OrLeftFixed()
    {
        using var json = new FakeLmsJson();
        var id = await Start(Dev("clock", volume: 40), lmsJson: json.Port);
        var dac = await Dac();
        Assert.True(dac.GetProperty("volumeAvailable").GetBoolean());
        Assert.Equal("dac", dac.GetProperty("volumeMode").GetString());
        Assert.Equal(40, dac.GetProperty("level").GetInt32());
        Assert.Equal("40", await Volume(id));

        // An app's volume control (UPnP) sets it; the page and the app both see it.
        Assert.Equal(200, (await SetVolume(id, "25")).Status);
        Assert.Equal("25", await Volume(id));
        Assert.Equal(25, (await Dac()).GetProperty("level").GetInt32());
        var db = (await Soap(id, "RenderingControl", Rcs, "GetVolumeDB", new() { ["InstanceID"] = "0", ["Channel"] = "Master" })).Out["CurrentVolume"];
        Assert.True(int.Parse(db, CultureInfo.InvariantCulture) < 0);
        Assert.Contains("<errorCode>402</errorCode>", (await SetVolume(id, "loud")).Text);

        // The page's slider.
        await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"level\":60}"));
        Assert.Equal("60", await Volume(id));

        // Fixed: left where it is; the apps see 100 and can't move it.
        await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"volume\":\"fixed\"}"));
        dac = await Dac();
        Assert.Equal("fixed", dac.GetProperty("volumeMode").GetString());
        Assert.Equal(JsonValueKind.Null, dac.GetProperty("level").ValueKind);
        Assert.Equal("100", await Volume(id));
        Assert.Equal(200, (await SetVolume(id, "5")).Status);
        await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"volume\":\"dac\"}"));
        Assert.Equal("60", await Volume(id));

        // A Squeezebox server's volume (audg, "adjust" on): kept as one with the DAC's.
        var lms = new TcpListener(IPAddress.Loopback, 0);
        lms.Start();
        try
        {
            await Post("/api/services", $"{{\"squeezelite\":true,\"lmsServer\":\"127.0.0.1:{((IPEndPoint)lms.LocalEndpoint).Port}\"}}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var player = await lms.AcceptTcpClientAsync(cts.Token);
            var s = player.GetStream();
            var got = new System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)>();
            _ = ReadPlayer(s, got, cts.Token);
            await Until(() => Task.FromResult(got.Any(x => x.Op == "HELO")));
            static byte[] Audg(int volume, bool adjust = true)
            {
                var b = new byte[18];
                b[8] = (byte)(adjust ? 1 : 0);
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(10), LmsGain[volume]);
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(14), LmsGain[volume]);
                return b;
            }
            // The player handles the server's messages in order: once it has answered a later one (its
            // name), every earlier one has been dealt with. So "nothing changed" is checked without a wait.
            async Task Handled()
            {
                int n = got.Count(x => x.Op == "SETD");
                await ToPlayer(s, "setd", [0]);
                await Until(() => Task.FromResult(got.Count(x => x.Op == "SETD") > n));
            }
            async Task Reconnect()
            {
                player.Close();
                player = await lms.AcceptTcpClientAsync(cts.Token);
                s = player.GetStream();
                got = new();
                _ = ReadPlayer(s, got, cts.Token);
                await Until(() => Task.FromResult(got.Any(x => x.Op == "HELO")));
            }

            // How many times the bridge has said something (it says how it took the server, once per connection).
            int Said(string text) { lock (logs) return logs.Count(l => l.Contains(text, StringComparison.Ordinal)); }

            // Connected: LMS is told the DAC's level, so its slider shows the DAC's.
            await Until(() => Task.FromResult(json.Volumes.Contains("60") && Said("follows the DAC's (60%)") == 1));
            // What LMS had stored, sent as the player connects, and its echo of what it was told: the DAC
            // stays where it is, however loud LMS's was.
            await ToPlayer(s, "audg", Audg(100));
            await ToPlayer(s, "audg", Audg(60));
            await Handled();
            Assert.Equal("60", await Volume(id));
            // It sends its volume again with every stream it starts: the DAC's, so nothing moves.
            await ToPlayer(s, "strm", Strm('s', "/c44.flac", new Uri(files.Base).Port));
            await ToPlayer(s, "audg", Audg(60));
            await ToPlayer(s, "audg", Audg(100, adjust: false));     // the server's volume fixed at 100%: nothing changes
            await Handled();
            Assert.Equal("60", await Volume(id));
            // Its slider moved: the DAC goes to the number it shows.
            await ToPlayer(s, "audg", Audg(30));
            await Until(async () => await Volume(id) == "30");
            Assert.DoesNotContain("30", json.Volumes);                 // and isn't sent back
            // The DAC moved on the page: LMS's slider is moved to match.
            await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"level\":45}"));
            await Until(() => Task.FromResult(json.Volumes.Contains("45")));

            // A reconnect starts over: LMS is told again, and what it sends as the player comes back isn't applied.
            await Reconnect();
            await Until(() => Task.FromResult(Said("follows the DAC's (45%)") == 1));
            await ToPlayer(s, "audg", Audg(100));
            await Handled();
            Assert.Equal("45", await Volume(id));

            // A server without JSON-RPC (Roon's) can't be told: where its slider is as it connects is left alone,
            // and only its changes move the DAC, by as much as they move the slider (down 10 is down 10).
            json.Answers = false;
            await Reconnect();
            await Until(() => Task.FromResult(Said("can't be told the DAC's volume") == 1));
            await ToPlayer(s, "audg", Audg(100));
            await Handled();
            Assert.Equal("45", await Volume(id));
            await ToPlayer(s, "audg", Audg(90));
            await Until(async () => await Volume(id) == "35");
            player.Close();
        }
        finally { lms.Stop(); }
    }
}
