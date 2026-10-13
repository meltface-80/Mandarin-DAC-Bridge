// PlexLinkTests.cs — signing in to Plex (Caldera/PlexLink.cs) when plex.tv doesn't answer: each question is
// given up on, and the sign-in says so or asks again, instead of waiting for ever. A pretend plex.tv answers
// on a local port.
using System.Net;
using System.Net.Sockets;
using System.Text;
using MandarinDacBridge.Caldera;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class PlexLinkTests : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly List<TcpClient> held = [];
    private int asked;

    public PlexLinkTests()
    {
        listener.Start();
        PlexLink.PlexTv = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        PlexLink.Ask = TimeSpan.FromSeconds(1);
    }

    public void Dispose()
    {
        PlexLink.PlexTv = "https://plex.tv";
        PlexLink.Ask = TimeSpan.FromSeconds(15);
        stop.Cancel();
        listener.Stop();
        lock (held) foreach (var c in held) c.Dispose();
        stop.Dispose();
    }

    // Each request (the nth, counted from 1) is answered by answer: a JSON body, or null for none at all.
    private void Serve(Func<int, string?> answer) => _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; }
            lock (held) held.Add(c);
            var n = c.GetStream();
            _ = await n.ReadAsync(new byte[4096]);
            if (answer(Interlocked.Increment(ref asked)) is not { } json) continue;   // and then nothing
            var body = Encoding.UTF8.GetBytes(json);
            await n.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await n.WriteAsync(body);
            c.Dispose();
        }
    });

    private static async Task Within(Task t, int seconds) =>
        Assert.True(await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(seconds))) == t, "plex.tv was waited on for ever");

    // Whose sign-in it is: no answer, no name (before 1.1.17 the question waited for ever).
    [Fact]
    public async Task WhoseSignInItIs_IsGivenUpOn()
    {
        Serve(_ => null);
        var who = PlexLink.Account("a-token", "a-client");
        await Within(who, 5);
        Assert.Equal("", await who);
    }

    // Asking for a code: no answer, and the page is told so.
    [Fact]
    public async Task AskingForACode_IsGivenUpOn_AndSaysSo()
    {
        Serve(_ => null);
        var link = new PlexLink("a-client", _ => { });
        await Within(link.Start(), 5);
        Assert.Equal("", link.Code);
        Assert.Equal("plex.tv didn't answer: try again", link.Message);
        link.Cancel();
    }

    // Waiting for the code to be entered: one question unanswered, and the next is asked; the sign-in still
    // arrives.
    [Fact]
    public async Task WaitingForTheCode_AsksAgainAfterAnUnansweredQuestion()
    {
        Serve(i => i switch
        {
            1 => """{"id":7,"code":"ABCD"}""",
            2 => null,
            _ => """{"id":7,"code":"ABCD","authToken":"the-token"}"""
        });
        var got = new TaskCompletionSource<string>();
        var link = new PlexLink("a-client", t => got.TrySetResult(t));
        await link.Start();
        Assert.Equal("ABCD", link.Code);
        await Within(got.Task, 10);
        Assert.Equal("the-token", await got.Task);
        link.Cancel();
    }
}
