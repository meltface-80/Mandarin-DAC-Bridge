// RendererFeed.cs — the renderer's audio path: the feed thread that opens each track's source, writes
// it to the sink, follows the DAC's clock, and goes on to the next track (gapless when it can). A part of
// Renderer (Renderer.cs), sharing its state and its lock.
namespace MandarinDacBridge;

internal sealed partial class Renderer
{
    // Everything stops and the sink drops what it holds; the DAC stays held.
    public async Task Halt()
    {
        Task done;
        lock (gate)
        {
            run++;
            cts?.Cancel();
            source?.Dispose();
            source = null;
            done = feedDone;
        }
        try { await done.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { /* it stops by itself */ }
        lock (gate)
        {
            sink.Flush();
            drained = true;
            ForgetMarks();
        }
    }

    private void StartLocked(Track track, double offset, bool paused)
    {
        int r = ++run;
        Status = "OK";
        Error = "";
        Transport = paused ? "PAUSED_PLAYBACK" : "TRANSITIONING";
        mark = new Mark(0, track, offset, track.Info);
        reached = null;
        if (!paused) sink.Pause(false);
        cts?.Cancel();   // the last Halt cancelled it already; this is for when its feed outlived Halt's wait
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        feeding = r;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feedDone = tcs.Task;
        var thread = new Thread(() =>
        {
            Native.Libc.FavourThisThread();   // real-time priority where allowed (Linux)
            try { Feed(r, track, offset, ct); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Fail(r, e); }
            finally
            {
                lock (gate) if (feeding == r) feeding = 0;
                tcs.TrySetResult();
            }
        }) { IsBackground = true, Name = "feed", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private bool Stale(int r) => r != Volatile.Read(ref run);

    private void Feed(int r, Track first, double offset, CancellationToken ct)
    {
        var t = first;
        double off = offset;
        while (t != null)
        {
            ct.ThrowIfCancellationRequested();
            if (t.Mime == "" && !Sources.IsLive(t.Uri)) t.Mime = ContentType(t.Uri, ct);
            IPcmSource src;
            try
            {
                src = Sources.Open(t.Uri, t.Mime, off, dac(), ffmpeg, log, ct);
            }
            catch (Exception e) when (t != first && !ct.IsCancellationRequested)
            {
                // The next track won't play: let this one finish, then stop.
                log("next track: " + e.Message);
                break;
            }
            lock (gate)
            {
                if (Stale(r)) { src.Dispose(); return; }
                source = src;
            }
            try
            {
                var info = src.Info;
                t.Info = info;
                if (t.Duration <= 0 && info.Duration > 0) t.Duration = info.Duration;
                if (fmt is not { } f || f.Rate != info.Rate || f.Channels != info.Channels)
                {
                    if (!drained) { sink.Drain(ct); AfterDrain(); }
                    var physical = sink.Configure(info.Rate, info.Channels);
                    lock (gate) { fmt = (info.Rate, info.Channels, physical); ForgetMarks(); }
                }
                Track? now = null;
                lock (gate)
                {
                    if (Stale(r)) return;
                    var m = new Mark(written, t, off, info);
                    marks.Add(m);
                    if (t == first) { Announce(m); now = NewlyReached(); }
                    if (Transport == "TRANSITIONING") Transport = "PLAYING";
                }
                Changed?.Invoke();
                if (now != null) Reached?.Invoke(now);
                Pump(src, info, ct);
            }
            finally
            {
                lock (gate) { if (source == src) source = null; }
                src.Dispose();
            }
            if (Stale(r)) return;
            Decoded?.Invoke(t);
            t = AwaitNext(r, ct);
            off = 0;
        }
        if (!drained) { sink.Drain(ct); AfterDrain(); }
        lock (gate)
        {
            if (Stale(r)) return;
            Transport = "STOPPED";
            mark = null;
        }
        arbiter.Touch();
        Changed?.Invoke();
        Finished?.Invoke(false);
    }

    // Called from a Decoded handler that has just asked for the next track (a
    // Squeezebox server sends it only then): it is waited for until the DAC
    // has almost nothing left, rather than half a second.
    public void ExpectNext() => Volatile.Write(ref expectNext, Volatile.Read(ref run));

    // The next track once this one is decoded: given already, or given while this one plays out.
    private Track? AwaitNext(int r, CancellationToken ct)
    {
        double least = Volatile.Read(ref expectNext) == r ? 0.05 : 0.5;
        for (;;)
        {
            if (Stale(r)) return null;
            lock (gate)
            {
                if (Next is { Taken: false } n) { n.Taken = true; return n; }
                double left = fmt is { } f ? (written - Heard()) / (double)f.Rate : 0;
                if (left < least) return null;
            }
            ct.WaitHandle.WaitOne(least < 0.5 ? 10 : 100);
            ct.ThrowIfCancellationRequested();
        }
    }

    private void Pump(IPcmSource src, TrackInfo info, CancellationToken ct)
    {
        int frameBytes = info.Channels * 4;
        var buf = new byte[1 << 16];
        int have = 0;
        for (;;)
        {
            int n = src.Read(buf.AsSpan(have), ct);
            ct.ThrowIfCancellationRequested();
            if (n <= 0) return;
            have += n;
            int usable = have - have % frameBytes;
            if (usable == 0) continue;
            var span = buf.AsSpan(0, usable);
            if (Muted)
            {
                if (info.Dop) Dsd.MuteDop(span);
                else span.Clear();
            }
            sink.Write(span, ct);
            lock (gate) { written += usable / frameBytes; drained = false; }
            buf.AsSpan(usable, have - usable).CopyTo(buf);
            have -= usable;
        }
    }

    private void AfterDrain()
    {
        lock (gate)
        {
            ForgetMarks();
            drained = true;
        }
    }

    // The sink's clock starts again at 0 (a configure, a flush, a drain): where each track began, and how
    // far the clock was seen, go with it. With gate held.
    private void ForgetMarks()
    {
        written = 0;
        marks.Clear();
        heard = 0;
    }

    // The sink's clock, as far as the marks go. A drained sink's clock is back at 0 a moment before its
    // marks are forgotten (AfterDrain), and a clock that can't be read (a DAC going) says 0: read as it is,
    // either took the first track's place for the one being heard. So 0 holds where the clock was last
    // seen; any other reading is taken as it is (one read high, ALSA's while it can't ask the DAC, is put
    // right by the next). With gate held.
    private long Heard()
    {
        long played = sink.Played;
        if (played == 0) return heard;
        return heard = played;
    }

    // Every 100 ms: the track the DAC has reached, and the owner kept current while it plays.
    private void OnClock()
    {
        bool changed = false;
        Track? now = null;
        lock (gate)
        {
            if (marks.Count == 0) return;
            long played = Heard();
            Mark? m = null;
            foreach (var x in marks) if (x.Frame <= played) m = x;
            if (m != null && m != mark) { changed = Announce(m); now = NewlyReached(); }
        }
        if (IsActive) arbiter.Touch();
        if (changed) Changed?.Invoke();
        if (now != null) Reached?.Invoke(now);
    }

    // The track being heard, the first time it is. With gate held.
    private Track? NewlyReached()
    {
        if (mark == null || mark.Track == reached) return null;
        reached = mark.Track;
        return reached;
    }

    // The DAC has reached this mark's track. With gate held.
    private bool Announce(Mark m)
    {
        mark = m;
        if (m.Track == Current) return false;
        Current = m.Track;
        if (Next == m.Track) Next = null;
        return true;
    }

    private void Fail(int r, Exception e)
    {
        lock (gate)
        {
            if (Stale(r)) return;
            log("play: " + e.Message);
            run++;
            source?.Dispose();
            source = null;
            sink.Flush();
            ForgetMarks();
            drained = true;
            Transport = "STOPPED";
            Status = "ERROR_OCCURRED";
            Error = e.Message;
            mark = null;
        }
        Changed?.Invoke();
        Finished?.Invoke(true);
    }
}
