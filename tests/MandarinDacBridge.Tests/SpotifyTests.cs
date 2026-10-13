// SpotifyTests.cs — Spotify Connect through Spotify Soloist, end to end: the bridge's own wiring, and a
// pretend Soloist (a script that plays a tone through the bridge's private PulseAudio, and a WebSocket
// here). A part of EndToEndTests (dev/quality.md: an area moves out as a part of the same class until
// the shared fixture exists), using its helpers.
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests
{
    // Spotify's sound reaches the renderer through a name ("live:spotify/<dac>") its player registers.
    // A new key, a Soloist download or an expired build restarts the player: the new one must still be
    // reachable by that name. (1.1.9: the old player, closed after the new one had registered, took the
    // name away, and every play then failed until Spotify was switched off and on.)
    [Fact]
    public async Task Spotify_RestartedByANewKey_StillReachesItsSound()
    {
        var id = await Start(Dev("clock"), soloist: Path.Combine(tmp, "no-soloist-here"));
        await PostServices("{\"spotify\":true,\"soloistKey\":\"key-one\"}");
        Assert.Equal("Spotify isn't playing", OpenSpotifySound(id));

        await PostServices("{\"soloistKey\":\"key-two\"}");   // restarts the player
        Assert.Equal("Spotify isn't playing", OpenSpotifySound(id));

        await PostServices("{\"spotify\":false}");
        Assert.Equal($"live:spotify/{id} isn't playing", OpenSpotifySound(id));
    }

    private async Task PostServices(string json)
    {
        using var res = await Http.PostAsync($"http://127.0.0.1:{port}/api/services", new StringContent(json, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
    }

    // What opening Spotify's sound says while nothing plays: its player's own answer, or that no player
    // has the name at all.
    private static string OpenSpotifySound(string id) =>
        Assert.Throws<InvalidOperationException>(() =>
            Sources.Open($"live:spotify/{id}", "", 0, new DacTraits([44100], 2, "pcm"), "ffmpeg", _ => { }, CancellationToken.None)).Message;

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
        // What Soloist says, kept for finding out why a connection fails (its owner's only); and the sign-in, in the log.
        var soloistLog = Path.Combine(hosts[^1].Manager.Settings.Dir, "soloist", id, "soloist.log");
        Assert.Contains("[bridge] {\"type\":\"auth_state\",\"logged_in\":true", File.ReadAllText(soloistLog));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(soloistLog));
        lock (logs) Assert.Contains(logs, l => l.Contains("Soloist is signed in, and the Spotify app has chosen it"));
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
}
