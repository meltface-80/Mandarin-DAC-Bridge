// CalderaTests.cs — Plex through Caldera Headless, end to end, with a pretend caldera-music. Part of
// EndToEndTests (same bridge, same helpers) until those move to a shared fixture (dev/quality.md).
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests
{
    // ------------------------------------------------------------ Plex through Caldera Headless (a pretend caldera-music)

    [Fact]
    public async Task Caldera_IsSetUpBitPerfect_SharesTheDac_ShowsWhatPlexPlays()
    {
        if (!OperatingSystem.IsLinux() || !Has("python3")) return;
        var id = await Start(Dev("clock"));
        var data = hosts[^1].Manager.Settings.Dir;

        // A pretend Plex server: a track's metadata, and its cover (only with the token).
        var plexPort = FreePort();
        using var plex = new HttpListener();
        plex.Prefixes.Add($"http://127.0.0.1:{plexPort}/");
        plex.Start();
        var tokensSeen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (plex.IsListening)
            {
                HttpListenerContext c;
                try { c = await plex.GetContextAsync(); } catch (Exception) { return; }
                var token = c.Request.Headers["X-Plex-Token"] ?? c.Request.QueryString["X-Plex-Token"] ?? "";
                tokensSeen.Enqueue(token);
                byte[] body;
                if (c.Request.Url!.AbsolutePath == "/api/v2/user")
                {
                    c.Response.ContentType = "application/json";
                    body = Encoding.UTF8.GetBytes("""{"id":1,"username":"miles","email":"miles@example.com"}""");
                }
                else if (c.Request.Url!.AbsolutePath == "/library/metadata/42")
                {
                    c.Response.ContentType = "application/json";
                    body = Encoding.UTF8.GetBytes("""
                        {"MediaContainer":{"Metadata":[{"title":"So What","grandparentTitle":"Miles Davis","parentTitle":"Kind of Blue",
                         "duration":562000,"thumb":"/library/metadata/42/thumb/1",
                         "Media":[{"audioCodec":"flac","bitDepth":24,"Part":[{"Stream":[{"streamType":2,"samplingRate":96000,"bitDepth":24,"codec":"flac"}]}]}]}]}}
                        """);
                }
                else { c.Response.ContentType = "image/jpeg"; body = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]; }
                c.Response.OutputStream.Write(body);
                c.Response.Close();
            }
        });

        // A pretend caldera-music: notes its settings, then answers Plex Companion's timeline from a file.
        var install = Path.Combine(data, "caldera", "caldera-music");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        Directory.CreateDirectory(Path.Combine(install, "lib"));
        File.WriteAllText(Path.Combine(install, "VERSION"), "1.1.0\n");
        var program = Path.Combine(install, "bin", "caldera-music");
        File.WriteAllText(program, """
            #!/bin/sh
            cfg=""; prev=""; set_mode=0
            for a in "$@"; do [ "$prev" = "--config" ] && cfg="$a"; [ "$a" = "--set" ] && set_mode=1; prev="$a"; done
            if [ $set_mode = 1 ]; then printf '%s\n' "$@" > "$cfg/set-args"; echo "$HOME|$LD_LIBRARY_PATH" > "$cfg/env"; exit 0; fi
            port=$(grep -o 'companion.port=[0-9]*' "$cfg/set-args" | cut -d= -f2)
            exec python3 -c '
            import sys, http.server, socketserver
            port, path = int(sys.argv[1]), sys.argv[2]
            class H(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    try: body = open(path, "rb").read()
                    except OSError: body = b"<MediaContainer><Timeline type=\"music\" state=\"stopped\"/></MediaContainer>"
                    self.send_response(200); self.send_header("Content-Type", "text/xml"); self.end_headers(); self.wfile.write(body)
                def log_message(self, *a): pass
            socketserver.TCPServer.allow_reuse_address = True
            socketserver.TCPServer(("127.0.0.1", port), H).serve_forever()
            ' "$port" "$cfg/timeline.xml"

            """);
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Caldera.PlexLink.PlexTv = $"http://127.0.0.1:{plexPort}";      // plex.tv, played by the pretend server
        using (var res = await Http.PostAsync($"http://127.0.0.1:{port}/api/services",
                   new StringContent("{\"caldera\":true,\"plexToken\":\"plex-token-1\"}", Encoding.UTF8, "application/json")))
        {
            var text = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("plex-token-1", text);
            var sv = JsonDocument.Parse(text).RootElement;
            Assert.True(sv.GetProperty("plexSignedIn").GetBoolean());
            Assert.Equal("1.1.0", sv.GetProperty("calderaVersion").GetString());
        }

        // Set up bit-perfect, on this DAC, under the bridge's name; its own HOME and libraries.
        var cfg = Path.Combine(data, "caldera", "players", id);
        await Until(() => Task.FromResult(File.Exists(Path.Combine(cfg, "set-args"))));
        var set = File.ReadAllLines(Path.Combine(cfg, "set-args"));
        foreach (var want in new[] { "plex.token=plex-token-1", "companion.clientName=Test DAC (Bridge)", "audio.outputDeviceUid=clock",
                     "audio.sampleRate=0", "audio.channels=0", "audio.masterVolume=100", "player.loudnessLeveling=no", "player.sweetFades=no" })
            Assert.Contains(want, set);
        Assert.Equal($"{Path.Combine(data, "caldera")}|{Path.Combine(install, "lib")}", File.ReadAllText(Path.Combine(cfg, "env")).Trim());
        // Caldera keeps its own copy of the token under its HOME: that and its folder are its owner's only.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(cfg));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(data, "caldera")));

        // The DAC is shared while Caldera is on: let go while the bridge isn't playing.
        await Until(async () => !(await Dac()).GetProperty("exclusive").GetBoolean());
        await Until(async () => (await Dac()).GetProperty("caldera").GetString() == "ready · choose it in Plexamp");

        // Plexamp plays to it: the page shows what, from the Plex server, and who has the DAC.
        File.WriteAllText(Path.Combine(cfg, "timeline.xml"),
            $"<MediaContainer commandID=\"1\"><Timeline type=\"music\" state=\"playing\" time=\"61000\" duration=\"562000\" key=\"/library/metadata/42\" ratingKey=\"42\" address=\"127.0.0.1\" port=\"{plexPort}\" protocol=\"http\"/></MediaContainer>");
        await Until(async () => (await Dac()).GetProperty("player").GetProperty("title").GetString() == "So What");
        var dac = await Dac();
        var p = dac.GetProperty("player");
        Assert.Equal("PLAYING", p.GetProperty("transport").GetString());
        Assert.Equal("Miles Davis", p.GetProperty("artist").GetString());
        Assert.Equal("Kind of Blue", p.GetProperty("album").GetString());
        Assert.Equal("96 kHz · 24-bit · FLAC", p.GetProperty("format").GetString());
        Assert.Equal(61, p.GetProperty("position").GetInt64());
        Assert.Equal("Plex (Caldera)", dac.GetProperty("control").GetProperty("owner").GetString());
        var art = p.GetProperty("art").GetString()!;
        Assert.StartsWith($"/api/dacs/{id}/art", art);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }, await Http.GetByteArrayAsync($"http://127.0.0.1:{port}{art}"));
        Assert.All(tokensSeen, t => Assert.Equal("plex-token-1", t));
        Assert.DoesNotContain("plex-token-1", await Http.GetStringAsync($"http://127.0.0.1:{port}/api/dacs"));
        // Whose sign-in it is, on the page.
        await Until(async () => (await Dacs()).RootElement.GetProperty("services").GetProperty("plexAccount").GetString() == "miles");

        // Signed out: the token is forgotten, here and in Caldera's settings; the player waits for a new sign-in.
        var so = JsonDocument.Parse(await (await Http.PostAsync($"http://127.0.0.1:{port}/api/services/plex/signout", null)).Content.ReadAsStringAsync()).RootElement;
        Assert.False(so.GetProperty("plexSignedIn").GetBoolean());
        Assert.Equal("", so.GetProperty("plexAccount").GetString());
        Assert.DoesNotContain("plex-token-1", File.ReadAllText(Path.Combine(data, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(data, "caldera", "players")) && Directory.EnumerateFiles(Path.Combine(data, "caldera", "players"), "set-args", SearchOption.AllDirectories).Any());
        await Until(async () => (await Dac()).GetProperty("caldera").GetString()!.Contains("needs a Plex sign-in"));

        // Switched off: Caldera stops, the DAC is held again.
        await Post("/api/services", "{\"caldera\":false}");
        await Until(async () => (await Dac()).GetProperty("exclusive").GetBoolean());
        plex.Stop();
        Caldera.PlexLink.PlexTv = "https://plex.tv";
    }
}
