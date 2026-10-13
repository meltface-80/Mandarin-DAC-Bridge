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
                if (c.Request.Url!.AbsolutePath == "/api/v2/resources")
                {
                    // plex.tv's list of the account's servers: this one is the owner's.
                    c.Response.ContentType = "application/json";
                    body = Encoding.UTF8.GetBytes($$$"""
                        [{"name":"Home","provides":"server","owned":true,"connections":[{"protocol":"http","address":"127.0.0.1","port":{{{plexPort}}},"uri":"http://127.0.0.1:{{{plexPort}}}","local":true}]},
                         {"name":"A friend's","provides":"server","owned":false,"connections":[{"protocol":"http","address":"10.9.9.9","port":32400,"uri":"http://10.9.9.9:32400"}]}]
                        """);
                }
                else if (c.Request.Url!.AbsolutePath == "/api/v2/user")
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

        // A device on the network tells Caldera to play from a server of its own, with no token: that
        // server is never sent the owner's sign-in (1.1.9 sent it). With a token of its own, it gets that.
        var strangerPort = FreePort();
        using var stranger = new HttpListener();
        stranger.Prefixes.Add($"http://127.0.0.1:{strangerPort}/");
        stranger.Start();
        var strangerSaw = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (stranger.IsListening)
            {
                HttpListenerContext c;
                try { c = await stranger.GetContextAsync(); } catch (Exception) { return; }   // stopped
                strangerSaw.Enqueue(c.Request.Headers["X-Plex-Token"] ?? "");
                c.Response.StatusCode = 404;
                c.Response.Close();
            }
        });
        File.WriteAllText(Path.Combine(cfg, "timeline.xml"),
            $"<MediaContainer commandID=\"2\"><Timeline type=\"music\" state=\"playing\" time=\"1000\" duration=\"100000\" key=\"/library/metadata/43\" ratingKey=\"43\" address=\"127.0.0.1\" port=\"{strangerPort}\" protocol=\"http\"/></MediaContainer>");
        await Until(() => { lock (logs) return Task.FromResult(logs.Any(l => l.Contains($"127.0.0.1:{strangerPort} isn't one of your servers"))); });
        Assert.Empty(strangerSaw);
        File.WriteAllText(Path.Combine(cfg, "timeline.xml"),
            $"<MediaContainer commandID=\"3\"><Timeline type=\"music\" state=\"playing\" time=\"1000\" duration=\"100000\" key=\"/library/metadata/44\" ratingKey=\"44\" address=\"127.0.0.1\" port=\"{strangerPort}\" protocol=\"http\" token=\"its-own-token\"/></MediaContainer>");
        await Until(() => Task.FromResult(!strangerSaw.IsEmpty));
        Assert.All(strangerSaw, t => Assert.Equal("its-own-token", t));
        // The owner's server named, with a track "key" that would move the request to the stranger: refused.
        int strangerBefore = strangerSaw.Count;
        File.WriteAllText(Path.Combine(cfg, "timeline.xml"),
            $"<MediaContainer commandID=\"4\"><Timeline type=\"music\" state=\"playing\" time=\"1000\" duration=\"100000\" key=\"@127.0.0.1:{strangerPort}/x\" ratingKey=\"45\" address=\"127.0.0.1\" port=\"{plexPort}\" protocol=\"http\"/></MediaContainer>");
        await Until(() => { lock (logs) return Task.FromResult(logs.Any(l => l.Contains($"isn't a path on http://127.0.0.1:{plexPort}"))); });
        Assert.Equal(strangerBefore, strangerSaw.Count);
        stranger.Stop();

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

// Which Plex servers may be sent the owner's sign-in, and how they are asked (Caldera/PlexServers.cs);
// what may be passed on as a cover (Web.cs).
public sealed class PlexServersTests : IDisposable
{
    private readonly HttpListener plexTv = new();
    private readonly int plexTvPort = Free();
    private volatile bool plexTvUp = true;
    private int asked;

    public PlexServersTests()
    {
        plexTv.Prefixes.Add($"http://127.0.0.1:{plexTvPort}/");
        plexTv.Start();
        _ = Task.Run(async () =>
        {
            while (plexTv.IsListening)
            {
                HttpListenerContext c;
                try { c = await plexTv.GetContextAsync(); } catch (Exception) { return; }   // stopped
                Interlocked.Increment(ref asked);
                c.Response.StatusCode = plexTvUp ? 200 : 503;
                c.Response.ContentType = "application/json";
                c.Response.OutputStream.Write(Encoding.UTF8.GetBytes("""
                    [{"name":"Home","provides":"server","owned":true,"connections":[{"protocol":"http","address":"192.168.1.10","port":32400,"uri":"http://192.168.1.10:32400"}]}]
                    """));
                c.Response.Close();
            }
        });
        Caldera.PlexLink.PlexTv = $"http://127.0.0.1:{plexTvPort}";
    }

    public void Dispose()
    {
        Caldera.PlexLink.PlexTv = "https://plex.tv";
        plexTv.Stop();
    }

    private static int Free()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Theory]
    [InlineData("http://192.168.1.10:32400", "/library/metadata/42", "http://192.168.1.10:32400/library/metadata/42")]
    [InlineData("http://192.168.1.10:32400", "/library/metadata/42/thumb/1?t=9", "http://192.168.1.10:32400/library/metadata/42/thumb/1?t=9")]
    [InlineData("http://192.168.1.10:32400", "/a@b", "http://192.168.1.10:32400/a@b")]
    [InlineData("http://192.168.1.10:32400", "@evil.example:8080/x", null)]      // would move the host
    [InlineData("http://192.168.1.10:32400", "//evil.example/x", null)]
    [InlineData("http://192.168.1.10:32400", "http://evil.example/x", null)]
    [InlineData("http://192.168.1.10:32400", "\\evil.example\\x", null)]
    [InlineData("http://192.168.1.10:32400", "", null)]
    [InlineData("ftp://192.168.1.10:32400", "/x", null)]
    public void WhatIsAskedOfAServer_IsAPathOnThatVeryServer(string server, string path, string? url) =>
        Assert.Equal(url, PlexServersOn(server, path));

    private static string? PlexServersOn(string server, string path) => Caldera.PlexServers.On(server, path)?.ToString();

    [Fact]
    public void TheOwnersServers_AreTheOnesPlexTvListsAsOwned_AtEveryAddress_WithItsScheme()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            [{"name":"Home","provides":"server","owned":true,"connections":[
               {"protocol":"http","address":"192.168.1.10","port":32400,"uri":"http://192.168.1.10:32400"},
               {"protocol":"https","address":"203.0.113.5","port":32400,"uri":"https://203-0-113-5.abc.plex.direct:32400"},
               {"protocol":"http","address":"fe80::1","port":32400},
               {"protocol":"http","address":"192.168.1.12","port":32400.5}]},
             {"name":"A friend's","provides":"server","owned":false,"connections":[{"address":"10.9.9.9","port":32400,"uri":"http://10.9.9.9:32400"}]},
             {"name":"A phone","provides":"client,player","owned":true,"connections":[{"address":"192.168.1.20","port":32500}]}]
            """);
        var owned = Caldera.PlexServers.Owned(doc.RootElement);
        Assert.Equal(["http://192.168.1.10:32400", "http://[fe80::1]:32400", "https://203-0-113-5.abc.plex.direct:32400", "https://203.0.113.5:32400"],
            owned.Order(StringComparer.Ordinal));
        // Listed for https only: the same address over http is not the owner's (the token would go in the clear).
        Assert.DoesNotContain(Caldera.PlexServers.Key("http://203.0.113.5:32400"), owned);
    }

    [Fact]
    public async Task OwnershipComesFromPlexTv_AndAnUnknownAnswerIsNotANo()
    {
        var ct = CancellationToken.None;
        Assert.True(await Caldera.PlexServers.IsOwned("http://192.168.1.10:32400", "tok-a", "client", ct));
        Assert.False(await Caldera.PlexServers.IsOwned("http://192.168.1.11:32400", "tok-a", "client", ct));
        Assert.False(await Caldera.PlexServers.IsOwned("https://192.168.1.10:32400", "tok-a", "client", ct));
        Assert.False(await Caldera.PlexServers.IsOwned("http://192.168.1.10:32400", "", "client", ct));
        // plex.tv down, for a new account: it can't say, which is not "not yours"; and it isn't asked again at once.
        plexTvUp = false;
        Assert.Null(await Caldera.PlexServers.IsOwned("http://192.168.1.10:32400", "tok-b", "client", ct));
        int before = asked;
        Assert.Null(await Caldera.PlexServers.IsOwned("http://192.168.1.10:32400", "tok-b", "client", ct));
        Assert.Equal(before, asked);
    }

    [Fact]
    public async Task APlexRequest_DoesNotFollowARedirect_WithTheToken()
    {
        var elsewhere = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int otherPort = Free(), redirectPort = Free();
        using var other = new HttpListener();
        other.Prefixes.Add($"http://127.0.0.1:{otherPort}/");
        other.Start();
        _ = Task.Run(async () =>
        {
            try { var c = await other.GetContextAsync(); elsewhere.Enqueue(c.Request.Headers["X-Plex-Token"] ?? ""); c.Response.Close(); }
            catch (Exception) { }   // stopped
        });
        using var redirect = new HttpListener();
        redirect.Prefixes.Add($"http://127.0.0.1:{redirectPort}/");
        redirect.Start();
        _ = Task.Run(async () =>
        {
            try { var c = await redirect.GetContextAsync(); c.Response.StatusCode = 302; c.Response.RedirectLocation = $"http://127.0.0.1:{otherPort}/x"; c.Response.Close(); }
            catch (Exception) { }   // stopped
        });
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{redirectPort}/library/metadata/1");
        req.Headers.TryAddWithoutValidation("X-Plex-Token", "secret");
        using var res = await Caldera.PlexServers.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        await Task.Delay(300);   // time for a followed redirect to have arrived, were it followed
        Assert.Empty(elsewhere);
    }

    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData("image/webp", "image/webp")]
    [InlineData(null, "image/jpeg")]
    [InlineData("image/svg+xml", null)]
    [InlineData("text/html", null)]
    public void ACoverIsPassedOnOnlyAsAPicture(string? from, string? passed) => Assert.Equal(passed, Web.CoverType(from));
}
