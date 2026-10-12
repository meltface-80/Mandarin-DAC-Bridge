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

    [Fact]
    public async Task Volume_IsTheDacsOwn_SetByTheAppsThePageAndTheServer_OrLeftFixed()
    {
        var id = await Start(Dev("clock", volume: 40));
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

        // A Squeezebox server's volume (audg, "adjust" on): -30 dB, as a level on a 60 dB curve.
        var lms = new TcpListener(IPAddress.Loopback, 0);
        lms.Start();
        try
        {
            await Post("/api/services", $"{{\"squeezelite\":true,\"lmsServer\":\"127.0.0.1:{((IPEndPoint)lms.LocalEndpoint).Port}\"}}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var player = await lms.AcceptTcpClientAsync(cts.Token);
            var s = player.GetStream();
            var got = new System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)>();
            _ = ReadPlayer(s, got, cts.Token);
            await Until(() => Task.FromResult(got.Any(x => x.Op == "HELO")));
            static byte[] Audg(bool adjust, double db)
            {
                var b = new byte[18];
                b[8] = (byte)(adjust ? 1 : 0);
                uint g = (uint)Math.Round(65536 * Math.Pow(10, db / 20));
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(10), g);
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(14), g);
                return b;
            }
            await ToPlayer(s, "audg", Audg(false, -30));     // the server's volume fixed at 100%: nothing changes
            await Task.Delay(300);
            Assert.Equal("60", await Volume(id));
            await ToPlayer(s, "audg", Audg(true, -30));
            await Until(async () => await Volume(id) == "24");
        }
        finally { lms.Stop(); }

        // Fixed: left where it is; the apps see 100 and can't move it.
        await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"volume\":\"fixed\"}"));
        dac = await Dac();
        Assert.Equal("fixed", dac.GetProperty("volumeMode").GetString());
        Assert.Equal(JsonValueKind.Null, dac.GetProperty("level").ValueKind);
        Assert.Equal("100", await Volume(id));
        Assert.Equal(200, (await SetVolume(id, "5")).Status);
        await Http.PostAsync($"http://127.0.0.1:{port}/api/dacs/{id}/settings", new StringContent("{\"volume\":\"dac\"}"));
        Assert.Equal("24", await Volume(id));
    }
}
