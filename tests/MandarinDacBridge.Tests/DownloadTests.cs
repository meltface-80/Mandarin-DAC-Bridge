// DownloadTests.cs — a program fetched from its publisher (Download.cs) gives up on a publisher that has
// stopped sending, and not on one that is only slow; what is run to set it up is stopped when given up on.
// A pretend publisher answers on a local port.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class DownloadTests : IDisposable
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly List<TcpClient> held = [];

    public DownloadTests() => listener.Start();

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        lock (held) foreach (var c in held) c.Dispose();
        stop.Dispose();
    }

    private string Url => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/soloist.tar.gz";

    // Each request is answered by serve, on a connection that is then left open.
    private void Serve(Func<NetworkStream, Task> serve) => _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; }
            lock (held) held.Add(c);
            var n = c.GetStream();
            _ = await n.ReadAsync(new byte[4096]);
            try { await serve(n); } catch (Exception) { /* the test has finished with it */ }
        }
    });

    private static byte[] Head(int length) => Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\n\r\n");

    // A publisher that answers, sends a little and then nothing: the download gives up, and says why.
    // (Before 1.1.17 it waited for ever, and the page's Download did nothing until the bridge restarted.)
    [Fact]
    public async Task APublisherThatStopsSending_IsGivenUpOn()
    {
        Serve(async n => { await n.WriteAsync(Head(100_000)); await n.WriteAsync(new byte[100]); });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var read = Task.Run(async () =>
        {
            await using var body = await Download.Open(Url, CancellationToken.None, Idle);
            await body.CopyToAsync(Stream.Null);
        });
        Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))) == read, "the download never gave up");
        var e = await Assert.ThrowsAsync<TimeoutException>(() => read);
        Assert.Contains("127.0.0.1 sent nothing for 1 seconds", e.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
    }

    // One that takes the request and never answers it.
    [Fact]
    public async Task APublisherThatNeverAnswers_IsGivenUpOn()
    {
        Serve(_ => Task.CompletedTask);
        var open = Download.Open(Url, CancellationToken.None, Idle);
        Assert.True(await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(5))) == open, "the download never gave up");
        var e = await Assert.ThrowsAsync<TimeoutException>(() => open);
        Assert.Contains("127.0.0.1 didn't answer", e.Message);
    }

    // One that is slow but keeps sending: each piece comes well within the limit, so the whole of it, which
    // takes over the limit, arrives.
    [Fact]
    public async Task APublisherThatIsSlowButSending_IsWaitedFor()
    {
        Serve(async n =>
        {
            await n.WriteAsync(Head(8 * 1000));
            for (int i = 0; i < 8; i++) { await Task.Delay(250); await n.WriteAsync(new byte[1000]); }
        });
        await using var body = await Download.Open(Url, CancellationToken.None, Idle);
        var got = new MemoryStream();
        await body.CopyToAsync(got);
        Assert.Equal(8000, got.Length);
    }

    // Stopped on purpose (the bridge closing) is a cancellation, not a stall.
    [Fact]
    public async Task ADownloadStoppedOnPurpose_IsCancelled_NotTimedOut()
    {
        Serve(async n => { await n.WriteAsync(Head(100_000)); await n.WriteAsync(new byte[100]); });
        using var cancel = new CancellationTokenSource();
        await using var body = await Download.Open(Url, cancel.Token, TimeSpan.FromSeconds(30));
        cancel.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => body.CopyToAsync(Stream.Null, cancel.Token));
    }

    // A setup step (Roon's check, Apple's tools) that hangs: given up on, it is stopped, not left running.
    [Fact]
    public async Task ASetupStepGivenUpOn_IsStopped()
    {
        if (OperatingSystem.IsWindows()) return;   // a shell script stands in for the step
        var dir = Directory.CreateTempSubdirectory("mdb-download-").FullName;
        try
        {
            var pidFile = Path.Combine(dir, "pid");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Roon.RoonDownload.Run("/bin/sh", ["-c", $"echo $$ > {pidFile}; exec sleep 60"], cancel.Token));
            var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Running(pid) && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            Assert.False(Running(pid), "the step was left running");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static bool Running(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }   // no such process
    }

    // The class of error: a request through the streaming client (Sources.Http waits for ever, as a track
    // plays for as long as it plays) with nothing to end it. Each file that uses it is here with what bounds
    // it; a new one has to say.
    [Fact]
    public void EveryUseOfTheStreamingClient_IsBounded()
    {
        var bounded = new Dictionary<string, string>
        {
            ["Sources.cs"] = "a track, for as long as it plays: stopped by the renderer's token",
            ["Renderer.cs"] = "the track's first answer: a timeout token",
            ["Web.cs"] = "a cover for the page: the browser's request, which it ends",
            ["Download.cs"] = "a program: each read within a minute",
            ["PlexLink.cs"] = "plex.tv: each question within PlexLink.Ask",
        };
        var src = Path.Combine(ComposeTests.RepoRoot(), "src", "MandarinDacBridge");
        var users = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("Sources.Http"))
            .Select(Path.GetFileName).ToList();
        Assert.Contains("Download.cs", users);
        foreach (var f in users) Assert.True(bounded.ContainsKey(f!), $"{f} uses Sources.Http: what ends its requests? (add it here, with the answer)");
    }
}
