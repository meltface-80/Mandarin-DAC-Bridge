// PlaybackTests.cs — playing through the bridge, end to end: in real time on the clock sink (gapless,
// seeks, pauses, a change of rate, others kept out), a DAC another program holds, ALSA's real path on Linux,
// and DSD as DoP. A part of EndToEndTests (dev/quality.md: an area moves out as a part of the same class
// until the shared fixture exists), using its helpers.
using System.Net;
using System.Text.Json;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests
{
    [Fact]
    public async Task PlaysInRealTime_KeepsOthersOut_Gapless_Seeks_Pauses_ChangesRate()
    {
        if (!HasFfmpeg()) return;
        Make("d96.flac", 96000, 6, "s32");   // room for a slow runner's controller calls
        var id = await Start(Dev("clock"));

        // Audirvana loads and plays a 96 kHz track (six seconds) with a 96 kHz one next (gapless).
        Assert.Equal(200, (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("d96.flac"), ["CurrentURIMetaData"] = "" }, Aud)).Status);
        Assert.Equal(200, (await Av(id, "SetNextAVTransportURI", new() { ["NextURI"] = Url("b96.flac"), ["NextURIMetaData"] = "" }, Aud)).Status);
        Assert.Equal(200, (await Av(id, "Play", new() { ["Speed"] = "1" }, Aud)).Status);
        await Until(async () => await State(id) == "PLAYING");
        var playing = System.Diagnostics.Stopwatch.StartNew();

        // Mandarin (no User-Agent) tries to take over while it plays: kept out, with 705.
        var r = await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("c44.flac"), ["CurrentURIMetaData"] = "" });
        Assert.Equal(500, r.Status);
        Assert.Contains("<errorCode>705</errorCode>", r.Text);
        Assert.Contains("<errorCode>705</errorCode>", (await Av(id, "Stop")).Text);
        var v1 = await Dac();
        Assert.Equal("Audirvana", v1.GetProperty("control").GetProperty("owner").GetString());
        Assert.Equal("96 kHz · 24-bit · FLAC", v1.GetProperty("player").GetProperty("format").GetString());
        Assert.NotEqual(JsonValueKind.Null, v1.GetProperty("control").GetProperty("blocked").ValueKind);

        // The position follows the DAC's clock: about the time since it started playing, however long the
        // calls above took (a slow Windows runner took over three seconds, past a shorter first track's end).
        // Two seconds in at least, so that a clock that doesn't run can't pass.
        await Task.Delay(2200);
        var asked = playing.Elapsed.TotalSeconds;
        var pos = (await Av(id, "GetPositionInfo")).Out;
        var answered = playing.Elapsed.TotalSeconds;
        Assert.Equal(Url("d96.flac"), pos["TrackURI"]);
        var rel = TimeSpan.Parse(pos["RelTime"], System.Globalization.CultureInfo.InvariantCulture).TotalSeconds;
        Assert.InRange(rel, Math.Floor(asked) - 1, answered + 1);
        Assert.Equal("0:00:06", pos["TrackDuration"]);

        // The next track takes over when the DAC gets to it; NextURI empties.
        await Until(async () => (await Av(id, "GetPositionInfo")).Out["TrackURI"] == Url("b96.flac"), 10000);
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
            return p.GetProperty("format").GetString()!.StartsWith("96 kHz", StringComparison.Ordinal) && p.GetProperty("transport").GetString() == "PLAYING";
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
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("format").GetString()!.StartsWith("352.8 kHz → 176.4 kHz", StringComparison.Ordinal));
        await Av(id, "Stop", null, Aud);
        cb.Stop();
    }
}
