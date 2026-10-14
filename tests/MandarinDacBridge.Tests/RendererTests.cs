// RendererTests.cs — the renderer's own account of which track the DAC is playing (Renderer.cs,
// RendererFeed.cs), driven directly, with a DAC that plays at the speed of the clock (ClockSink).
using System.Diagnostics;
using MandarinDacBridge.Audio;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class RendererTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mdb-renderer-").FullName;
    private readonly FileServer files;

    public RendererTests() => files = new FileServer(dir);

    public void Dispose()
    {
        files.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { /* left for the system's temp cleaning */ }
    }

    private static bool HasFfmpeg()
    {
        try { using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true }); p!.WaitForExit(); return p.ExitCode == 0; }
        catch (Exception) { return false; }   // not installed: the tests that need it return early
    }

    private string Tone(string name, double seconds)
    {
        var f = Path.Combine(dir, name);
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"sine=frequency=1000:sample_rate=44100:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-ac", "2", f }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return $"{files.Base}/{name}";
    }

    // A DAC whose drain is slow to return once it has played everything (a busy machine, a DAC that
    // answers late): its clock is already back at 0.
    private sealed class SlowToReturn(ClockSink dac, int late = 500) : ISink
    {
        public Action? ClockReset;    // called while it is slow to return
        public volatile bool Blank;   // its clock can't be read: it says 0
        public bool Exclusive => dac.Exclusive;
        public string Message => dac.Message;
        public int HolderPid => dac.HolderPid;
        public event Action? StatusChanged { add => dac.StatusChanged += value; remove => dac.StatusChanged -= value; }
        public event Action? Gone { add { } remove { } }
        public void Start() => dac.Start();
        public string Configure(int rate, int channels) => dac.Configure(rate, channels);
        public void Write(ReadOnlySpan<byte> pcm, CancellationToken ct) => dac.Write(pcm, ct);
        public void Drain(CancellationToken ct)
        {
            dac.Drain(ct);
            ClockReset?.Invoke();
            Thread.Sleep(late);
        }
        public void Flush() => dac.Flush();
        public void Pause(bool on) => dac.Pause(on);
        public long Played => Blank ? 0 : dac.Played;
        public void Share(bool on) => dac.Share(on);
        public bool Reclaim() => dac.Reclaim();
        public void Dispose() => dac.Dispose();
    }

    // Plays these tracks gapless to a DAC that returns from its drain this late: (the tracks reached, in
    // order; the position read once the DAC's clock was back at 0; the last track's length).
    private async Task<(string[] Reached, double PositionAtReset, double Last)> PlayThrough(int late, params double[] seconds)
    {
        var uris = seconds.Select((d, i) => Tone($"t{i}.flac", d)).ToArray();
        var reached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var sink = new SlowToReturn(new ClockSink(), late);
        using var renderer = new Renderer(sink, "ffmpeg", () => new DacTraits([44100], 2, "pcm"), new Arbiter(TimeSpan.FromSeconds(2), 3500), _ => { });
        double atReset = -1;
        sink.ClockReset = () => atReset = renderer.Position();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.Reached += t => reached.Enqueue(t.Uri);
        renderer.Finished += _ => finished.TrySetResult();
        int next = 1;
        renderer.Decoded += _ => { if (next < uris.Length) renderer.SetNext(uris[next++], "").GetAwaiter().GetResult(); };
        await renderer.SetUri(uris[0], "");
        await renderer.Play();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        return (reached.Select(u => Path.GetFileName(new Uri(u).AbsolutePath)).ToArray(), atReset, seconds[^1]);
    }

    // Before 1.1.28 the DAC's clock, back at 0 once it had played everything, was read before the renderer
    // forgot where each track began: the first track's place looked like the one being heard. A Squeezebox
    // server was told the first track had started (STMs) just before the end (STMu); the page and UPnP showed
    // it, and the last track's position read 0:00. Now it holds where the clock was last seen.
    [Fact]
    public async Task AfterTheLastTrack_AnEarlierOneIsntReachedAgain_NorThePositionBackAtTheStart()
    {
        if (!HasFfmpeg()) return;
        var (reached, position, last) = await PlayThrough(500, 0.6, 0.9);    // lengths apart: the end of the right one
        Assert.Equal(["t0.flac", "t1.flac"], reached);
        Assert.InRange(position, last / 2, last);    // where the clock was last seen, near the end: not back at 0
    }

    // A clock that can't be read says 0 (a DAC going, WASAPI's position failing). That isn't the DAC going
    // back, nor on: the track playing stays the one heard, and its position holds.
    [Fact]
    public async Task AClockThatCantBeRead_HoldsWhereItWas()
    {
        if (!HasFfmpeg()) return;
        string first = Tone("first.flac", 3), second = Tone("second.flac", 1.5);
        var sink = new SlowToReturn(new ClockSink(), 0);
        using var renderer = new Renderer(sink, "ffmpeg", () => new DacTraits([44100], 2, "pcm"), new Arbiter(TimeSpan.FromSeconds(2), 3500), _ => { });
        var reached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        renderer.Reached += t => reached.Enqueue(t.Uri);
        renderer.Decoded += t => { if (t.Uri == first) renderer.SetNext(second, "").GetAwaiter().GetResult(); };
        await renderer.SetUri(first, "");
        await renderer.Play();
        var clock = Stopwatch.StartNew();
        while ((renderer.Next == null || renderer.Position() < 0.5) && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.NotNull(renderer.Next);    // the second is written behind the first: a jump would reach it
        Assert.True(renderer.Position() >= 0.5, $"at {renderer.Position()}");
        double before = renderer.Position();
        sink.Blank = true;
        await Task.Delay(300);            // three of the renderer's 100 ms looks at the clock
        double during = renderer.Position();
        string[] seen = [.. reached];
        sink.Blank = false;
        Assert.Equal([first], seen);
        Assert.InRange(during, before, 3 - 0.1);
    }

    // Where the clock was last seen goes with the marks: a play that follows a longer one, on a DAC that
    // hasn't yet said it has played anything (its clock still at 0), starts at 0, not where the last ended.
    [Fact]
    public async Task APlayAfterALongerOne_StartsAgainFromTheBeginning()
    {
        if (!HasFfmpeg()) return;
        string first = Tone("long.flac", 2), next = Tone("short.flac", 1);
        var sink = new SlowToReturn(new ClockSink(), 0);
        using var renderer = new Renderer(sink, "ffmpeg", () => new DacTraits([44100], 2, "pcm"), new Arbiter(TimeSpan.FromSeconds(2), 3500), _ => { });
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.Finished += _ => finished.TrySetResult();
        await renderer.SetUri(first, "");
        await renderer.Play();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        sink.Blank = true;    // the DAC hasn't said yet that it has played anything
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.Reached += t => { if (t.Uri == next) started.TrySetResult(); };
        await renderer.SetUri(next, "");
        await renderer.Play();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));    // its first samples are with the DAC
        double at = renderer.Position();
        sink.Blank = false;
        Assert.True(at < 0.1, $"at {at}");
    }
}
