// Renderer.cs — one DAC as a UPnP media renderer: the transport (a URI, the
// next URI, play, pause, stop, seek), the position, and the audio path from
// the controller's stream to the DAC.
//
//   controller's URL ─▶ source (ffmpeg / DoP) ─▶ 32-bit PCM ─▶ sink ─▶ DAC
//
// Gapless: when a track has been decoded and the next one has the same rate
// and channels, it goes into the same open stream with nothing between. When
// it differs, the first is let play out, the DAC is switched, and the next
// one starts.
//
// The position is the DAC's own clock: the sink counts the frames it has
// played; "marks" say at which frame each track began, so the track shown
// changes when the DAC reaches it, not when it was decoded.
using System.Text.RegularExpressions;
using MandarinDacBridge.Audio;

namespace MandarinDacBridge;

internal sealed class UpnpException(int code, string description) : Exception(description)
{
    public int Code { get; } = code;
}

internal sealed class Track(string uri, string meta)
{
    public string Uri { get; } = uri;
    public string Meta { get; } = meta;
    public Didl Didl { get; } = Xml.ParseDidl(meta, uri);
    public string Mime { get; set; } = "";
    public double Duration { get; set; }
    public TrackInfo? Info { get; set; }
    public bool Taken { get; set; }
}

internal sealed record Mark(long Frame, Track Track, double Offset, TrackInfo? Info);

internal sealed record NowPlaying(string Transport, string Status, string Error, string Title, string Artist, string Album,
    long Position, long Duration, string Format, string Physical, bool Muted);

internal sealed class Renderer : IDisposable
{
    private readonly ISink sink;
    private readonly string ffmpeg;
    private readonly Func<DacTraits> dac;
    private readonly Arbiter arbiter;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly Timer clock;

    private CancellationTokenSource? cts;
    private Task feedDone = Task.CompletedTask;
    private int run;                     // the play under way; an older one stops by itself
    private int feeding;                 // the run whose feed is still going
    private IPcmSource? source;
    private (int Rate, int Channels, string Physical)? fmt;
    private long written;                // frames sent since the sink's clock last started at 0
    private bool drained = true;
    private readonly List<Mark> marks = [];
    private Mark? mark;                  // the one being heard
    private double pendingOffset;        // a seek while stopped

    public string Transport { get; private set; } = "NO_MEDIA_PRESENT";
    public string Status { get; private set; } = "OK";
    public string Error { get; private set; } = "";
    public Track? Current { get; private set; }
    public Track? Next { get; private set; }
    public bool Muted { get; private set; }
    public event Action? Changed;

    public Renderer(ISink sink, string ffmpeg, Func<DacTraits> dac, Arbiter arbiter, Action<string> log)
    {
        this.sink = sink;
        this.ffmpeg = ffmpeg;
        this.dac = dac;
        this.arbiter = arbiter;
        this.log = log;
        sink.StatusChanged += () => Changed?.Invoke();
        clock = new Timer(_ => OnClock(), null, 100, 100);
    }

    public bool IsActive => Transport is "PLAYING" or "TRANSITIONING";

    private static string GuessMime(string uri)
    {
        var m = Regex.Match(uri.Split('?')[0], @"\.([a-z0-9]{2,5})$", RegexOptions.IgnoreCase);
        return !m.Success ? "" : m.Groups[1].Value.ToLowerInvariant() switch
        {
            "flac" => "audio/flac", "wav" => "audio/wav", "aif" or "aiff" => "audio/aiff", "dsf" => "audio/dsf", "dff" => "audio/dff",
            "m4a" or "mp4" => "audio/mp4", "mp3" => "audio/mpeg", "ogg" => "audio/ogg", "opus" => "audio/opus", "aac" => "audio/aac", _ => ""
        };
    }

    private static Track MakeTrack(string uri, string meta)
    {
        var t = new Track(uri, meta);
        t.Mime = (t.Didl.Mime != "" ? t.Didl.Mime : GuessMime(uri)).ToLowerInvariant();
        t.Duration = t.Didl.Duration;
        return t;
    }

    // ------------------------------------------------------------ the transport

    public async Task SetUri(string uri, string meta)
    {
        if (uri == "") throw new UpnpException(714, "Illegal MIME-type");
        bool was = IsActive;
        await Halt();
        lock (gate)
        {
            Current = MakeTrack(uri, meta);
            Next = null;
            pendingOffset = 0;
            mark = null;
            Status = "OK";
            Error = "";
            Transport = "STOPPED";
        }
        Changed?.Invoke();
        if (was) await Play();
    }

    public Task SetNext(string uri, string meta)
    {
        lock (gate) Next = uri == "" ? null : MakeTrack(uri, meta);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task Play()
    {
        if (!sink.Exclusive) throw new UpnpException(701, "The DAC is held by another program: " + (sink.Message == "" ? "waiting for it" : sink.Message));
        lock (gate)
        {
            if (Transport == "PAUSED_PLAYBACK" && feeding == run && feeding != 0)
            {
                sink.Pause(false);
                Transport = fmt != null && marks.Count > 0 ? "PLAYING" : "TRANSITIONING";
            }
            else if (IsActive) return Task.CompletedTask;
            else if (Current == null) throw new UpnpException(701, "Nothing to play");
            else StartLocked(Current, pendingOffset, paused: false);
        }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task Pause()
    {
        lock (gate)
        {
            if (!IsActive) return Task.CompletedTask;
            sink.Pause(true);
            Transport = "PAUSED_PLAYBACK";
        }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task Stop()
    {
        await Halt();
        lock (gate)
        {
            pendingOffset = 0;
            mark = null;
            Transport = Current != null ? "STOPPED" : "NO_MEDIA_PRESENT";
        }
        Changed?.Invoke();
    }

    public async Task Seek(double seconds)
    {
        var cur = Current ?? throw new UpnpException(701, "Nothing to seek in");
        var s = Math.Max(0, seconds);
        if (IsActive || Transport == "PAUSED_PLAYBACK")
        {
            bool paused = Transport == "PAUSED_PLAYBACK";
            await Halt();
            if (paused) sink.Pause(true);
            lock (gate) StartLocked(Current ?? cur, s, paused);
        }
        else lock (gate) pendingOffset = s;
        Changed?.Invoke();
    }

    public void SetMute(bool on) { Muted = on; Changed?.Invoke(); }

    // ------------------------------------------------------------ the audio path

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
            written = 0;
            drained = true;
            marks.Clear();
        }
    }

    private void StartLocked(Track track, double offset, bool paused)
    {
        int r = ++run;
        Status = "OK";
        Error = "";
        Transport = paused ? "PAUSED_PLAYBACK" : "TRANSITIONING";
        mark = new Mark(0, track, offset, track.Info);
        if (!paused) sink.Pause(false);
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
            if (t.Mime == "") t.Mime = ContentType(t.Uri, ct);
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
                    lock (gate) { fmt = (info.Rate, info.Channels, physical); written = 0; marks.Clear(); }
                }
                lock (gate)
                {
                    if (Stale(r)) return;
                    var m = new Mark(written, t, off, info);
                    marks.Add(m);
                    if (t == first) Announce(m);
                    if (Transport == "TRANSITIONING") Transport = "PLAYING";
                }
                Changed?.Invoke();
                Pump(src, info, ct);
            }
            finally
            {
                lock (gate) { if (source == src) source = null; }
                src.Dispose();
            }
            if (Stale(r)) return;
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
    }

    // The next track once this one is decoded: given already, or given while this one plays out.
    private Track? AwaitNext(int r, CancellationToken ct)
    {
        for (;;)
        {
            if (Stale(r)) return null;
            lock (gate)
            {
                if (Next is { Taken: false } n) { n.Taken = true; return n; }
                double left = fmt is { } f ? (written - sink.Played) / (double)f.Rate : 0;
                if (left < 0.5) return null;
            }
            ct.WaitHandle.WaitOne(100);
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
            written = 0;
            marks.Clear();
            drained = true;
        }
    }

    // Every 100 ms: the track the DAC has reached, and the owner kept current while it plays.
    private void OnClock()
    {
        bool changed = false;
        lock (gate)
        {
            if (marks.Count == 0) return;
            long played = sink.Played;
            Mark? m = null;
            foreach (var x in marks) if (x.Frame <= played) m = x;
            if (m != null && m != mark) changed = Announce(m);
        }
        if (IsActive) arbiter.Touch();
        if (changed) Changed?.Invoke();
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
            written = 0;
            marks.Clear();
            drained = true;
            Transport = "STOPPED";
            Status = "ERROR_OCCURRED";
            Error = e.Message;
            mark = null;
        }
        Changed?.Invoke();
    }

    // The Content-Type a URL answers with, when nothing else says (raw L16/L24 needs it).
    private static string ContentType(string uri, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(3000);
            using var res = Sources.Http.Send(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var ctype = res.Content.Headers.ContentType;
            if (ctype?.MediaType == null) return "";
            var mime = ctype.MediaType.ToLowerInvariant();
            if (mime.StartsWith("audio/l")) foreach (var p in ctype.Parameters) mime += $";{p.Name}={p.Value}";
            return mime;
        }
        catch (Exception) { return ""; }
    }

    // ------------------------------------------------------------ reads

    public double Position() { lock (gate) return PositionUnlocked(); }

    public NowPlaying Now()
    {
        lock (gate)
        {
            var t = Current;
            var i = mark?.Info ?? t?.Info;
            return new NowPlaying(Transport, Status, Error, t?.Didl.Title ?? "", t?.Didl.Artist ?? "", t?.Didl.Album ?? "",
                (long)Math.Round(PositionUnlocked()), (long)Math.Round(t?.Duration ?? 0), i != null ? Describe(i) : "", fmt?.Physical ?? "", Muted);
        }
    }

    private double PositionUnlocked()
    {
        var m = mark;
        if (m == null) return pendingOffset;
        double t = m.Offset;
        if (fmt is { } f && marks.Contains(m)) t += Math.Max(0, sink.Played - m.Frame) / (double)f.Rate;
        var d = Current?.Duration ?? 0;
        return d > 0 && t > d ? d : t;
    }

    // "96 kHz · 24-bit · FLAC"
    public static string Describe(TrackInfo i)
    {
        if (i.Dop) return $"{i.Codec} · DoP at {Devices.KHz(i.Rate)}";
        var parts = new List<string> { i.Resampled ? $"{Devices.KHz(i.SrcRate)} → {Devices.KHz(i.Rate)}" : Devices.KHz(i.Rate) };
        if (i.Bits > 0 && !i.Float) parts.Add($"{i.Bits}-bit");
        var codec = i.Codec.StartsWith("pcm_") ? "PCM" : i.Codec.StartsWith("dsd_") ? "DSD → PCM" : i.Codec;
        if (codec != "") parts.Add(codec.ToUpperInvariant());
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        clock.Dispose();
        lock (gate) { run++; cts?.Cancel(); source?.Dispose(); source = null; }
    }
}
