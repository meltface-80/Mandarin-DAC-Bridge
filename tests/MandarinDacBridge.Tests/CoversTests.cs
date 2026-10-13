// CoversTests.cs — a cover is fetched from a server the bridge doesn't control and passed on to anyone who
// asks (Covers.cs): only what that server itself sends, said to be a picture, of a picture's size.
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class CoversTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private readonly HttpListener server = new();
    private readonly HttpListener elsewhere = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> reachedElsewhere = new();
    private readonly int port = Free(), elsewherePort = Free();
    private int loops;

    public CoversTests()
    {
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        elsewhere.Prefixes.Add($"http://127.0.0.1:{elsewherePort}/");
        elsewhere.Start();
        Serve(elsewhere, c => { reachedElsewhere.Enqueue(c.Request.Url!.AbsolutePath); Picture(c, "image/png", Png); });
        Serve(server, c =>
        {
            switch (c.Request.Url!.AbsolutePath)
            {
                case "/cover": Picture(c, "image/png", Png); break;
                case "/moved": c.Response.StatusCode = 302; c.Response.RedirectLocation = $"http://127.0.0.1:{elsewherePort}/secret"; c.Response.Close(); break;
                case "/same-server": c.Response.StatusCode = 302; c.Response.RedirectLocation = "/cover"; c.Response.Close(); break;
                case "/loop": Interlocked.Increment(ref loops); c.Response.StatusCode = 302; c.Response.RedirectLocation = "/loop"; c.Response.Close(); break;
                case "/jpg": Picture(c, "image/jpg", Png); break;
                case "/slow":
                    c.Response.ContentType = "image/jpeg";
                    c.Response.ContentLength64 = 10;
                    try { c.Response.OutputStream.Write(new byte[1]); c.Response.OutputStream.Flush(); Thread.Sleep(3000); c.Response.Abort(); }
                    catch (Exception) { /* the bridge gave up: what this case is for */ }
                    break;
                case "/moved-picture": c.Response.StatusCode = 302; c.Response.RedirectLocation = $"http://127.0.0.1:{elsewherePort}/secret"; Picture(c, "image/png", Png); break;
                case "/untyped": Picture(c, null, Png); break;
                case "/page": Picture(c, "text/html", "<script>1</script>"u8.ToArray()); break;
                case "/declared-huge":
                    // Its headers sent (a byte of it written), then cut off: the size said is what is refused.
                    c.Response.ContentType = "image/jpeg";
                    c.Response.ContentLength64 = Covers.MaxBytes + 1L;
                    try { c.Response.OutputStream.Write(new byte[1]); c.Response.OutputStream.Flush(); c.Response.Abort(); }
                    catch (Exception) { /* the bridge stopped reading */ }
                    break;
                case "/endless":
                    c.Response.ContentType = "image/jpeg";
                    c.Response.SendChunked = true;
                    try { var b = new byte[65536]; for (long sent = 0; sent <= Covers.MaxBytes; sent += b.Length) c.Response.OutputStream.Write(b); c.Response.Close(); }
                    catch (Exception) { /* the bridge stopped reading: what this case is for */ }
                    break;
                case "/max": Picture(c, "image/jpeg", new byte[Covers.MaxBytes]); break;
                default: c.Response.StatusCode = 404; c.Response.Close(); break;
            }
        });
    }

    private static int Free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static void Serve(HttpListener l, Action<HttpListenerContext> handle) => _ = Task.Run(async () =>
    {
        while (l.IsListening)
        {
            HttpListenerContext c;
            try { c = await l.GetContextAsync(); } catch (Exception) { return; }   // stopped
            _ = Task.Run(() => handle(c));
        }
    });

    private static void Picture(HttpListenerContext c, string? type, byte[] bytes)
    {
        if (type != null) c.Response.ContentType = type;
        c.Response.ContentLength64 = bytes.Length;
        try { c.Response.OutputStream.Write(bytes); c.Response.Close(); }
        catch (Exception) { /* the bridge stopped reading */ }
    }

    private Task<(string Type, byte[] Bytes)?> Fetch(string path, TimeSpan? within = null) =>
        Covers.Fetch($"http://127.0.0.1:{port}{path}", CancellationToken.None, within);

    public void Dispose()
    {
        server.Close();
        elsewhere.Close();
    }

    [Fact]
    public async Task APicture_IsPassedOn_AsItCame()
    {
        var cover = Assert.NotNull(await Fetch("/cover"));
        Assert.Equal("image/png", cover.Type);
        Assert.Equal(Png, cover.Bytes);
        Assert.Equal(Covers.MaxBytes, Assert.NotNull(await Fetch("/max")).Bytes.Length);
        Assert.Equal("image/jpeg", Assert.NotNull(await Fetch("/jpg")).Type);    // a common misspelling of it
    }

    // Another path on the same server (Plex's thumbnails can move to its transcoder): followed, a few times.
    [Fact]
    public async Task ARedirectOnTheSameServer_IsFollowed_AFewTimes()
    {
        Assert.Equal(Png, Assert.NotNull(await Fetch("/same-server")).Bytes);
        Assert.Null(await Fetch("/loop"));
        Assert.Equal(4, loops);    // asked, and three redirects followed
    }

    [Fact]
    public async Task ACoverThatTrickles_IsGivenUp() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetch("/slow", TimeSpan.FromMilliseconds(300)));

    // Before 1.1.23 the bridge followed it to another server, and passed on what 127.0.0.1 or another
    // machine answered.
    [Theory]
    [InlineData("/moved")]
    [InlineData("/moved-picture")]    // even said to be a picture itself
    public async Task ARedirect_IsNoCover_AndIsntFollowed(string path)
    {
        Assert.Null(await Fetch(path));
        Assert.Empty(reachedElsewhere);    // a followed redirect would be done by the time Fetch returns
    }

    // Before 1.1.23 a reply with no type was passed on as a JPEG: whatever a server answered, with no type.
    [Theory]
    [InlineData("/untyped")]
    [InlineData("/page")]
    [InlineData("/nothing")]
    public async Task WhatIsntSaidToBeAPicture_IsNoCover(string path) => Assert.Null(await Fetch(path));

    [Theory]
    [InlineData("/declared-huge")]
    [InlineData("/endless")]
    public async Task MoreThanAPicture_IsNoCover(string path) => Assert.Null(await Fetch(path));
}
