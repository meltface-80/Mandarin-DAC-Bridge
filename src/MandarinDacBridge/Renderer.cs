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
//
// UPnP is one way in; the Squeezebox player (Slim/) and Spotify Connect
// (Spotify/) drive the same transport, and follow it through Reached,
// Decoded and Finished. What they know about a track that the URL doesn't
// say (the title from Lyrion, Spotify's position) goes in Track.Live.
using System.Text.RegularExpressions;
using MandarinDacBridge.Audio;

namespace MandarinDacBridge;

internal sealed partial class Renderer : IDisposable
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
    private long heard;                  // where the sink's clock was last seen since then (Heard)
    private bool drained = true;
    private readonly List<Mark> marks = [];
    private Mark? mark;                  // the one being heard
    private double pendingOffset;        // a seek while stopped
    private Track? reached;              // the last track Reached was raised for
    private int expectNext;              // the run whose next track is on its way (asked for on Decoded)

    public string Transport { get; private set; } = "NO_MEDIA_PRESENT";
    public string Status { get; private set; } = "OK";
    public string Error { get; private set; } = "";
    public Track? Current { get; private set; }
    public Track? Next { get; private set; }
    public bool Muted { get; private set; }
    public event Action? Changed;
    // The DAC has begun to play this track.
    public event Action<Track>? Reached;
    // This track has been read to its end (the next can be sent now, for gapless; see ExpectNext).
    public event Action<Track>? Decoded;
    // Everything has played out (false), or playing failed (true).
    public event Action<bool>? Finished;

    public Renderer(ISink sink, string ffmpeg, Func<DacTraits> dac, Arbiter arbiter, Action<string> log)
    {
        this.sink = sink;
        this.ffmpeg = ffmpeg;
        this.dac = dac;
        this.arbiter = arbiter;
        this.log = log;
        sink.StatusChanged += () =>
        {
            // A DAC let go of (shared, or taken by another program) has to be set up again.
            if (!sink.Exclusive) lock (gate) fmt = null;
            Changed?.Invoke();
        };
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

    public async Task<Track> SetUri(string uri, string meta)
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
        lock (gate) return Current!;
    }

    public Task<Track?> SetNext(string uri, string meta)
    {
        Track? t;
        lock (gate) t = Next = uri == "" ? null : MakeTrack(uri, meta);
        Changed?.Invoke();
        return Task.FromResult(t);
    }

    public Task Play()
    {
        if (!sink.Exclusive)
        {
            // A shared DAC (let go while idle) is taken back now, if nothing else has it.
            if (!sink.Reclaim()) throw new UpnpException(701, "The DAC is held by another program: " + (sink.Message == "" ? "waiting for it" : sink.Message));
            lock (gate) fmt = null;
        }
        lock (gate)
        {
            if (Transport == "PAUSED_PLAYBACK" && feeding == run && feeding != 0)
            {
                if (Current?.Live is { Position: not null } l) Current.Live = l with { At = Arbiter.Now };
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
            // A position given by the server stops where it is.
            if (Current?.Live is { Position: { } lp } l) Current.Live = l with { Position = lp + Math.Max(0, Arbiter.Now - l.At) / 1000.0, At = Arbiter.Now };
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
            // Started only when no feed is running: another seek may have started one while this one waited
            // for Halt, and two feeds would write to one sink (and its audio would stay queued ahead of this
            // one's). That one is halted too, and the sink emptied, before this one starts.
            for (int tries = 0; ; tries++)
            {
                await Halt();
                if (paused) sink.Pause(true);
                lock (gate)
                {
                    if (feeding != 0 && tries < 3) continue;
                    StartLocked(Current ?? cur, s, paused);
                    break;
                }
            }
        }
        else lock (gate) pendingOffset = s;
        Changed?.Invoke();
    }

    public void SetMute(bool on) { Muted = on; Changed?.Invoke(); }

    // ------------------------------------------------------------ the audio path

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
            var live = t?.Live;
            double duration = live?.Duration > 0 ? live.Duration : t?.Duration ?? 0;
            double position = PositionUnlocked();
            if (live?.Position is { } lp)
            {
                position = lp + (Transport == "PLAYING" ? Math.Max(0, Arbiter.Now - live.At) / 1000.0 : 0);
                if (duration > 0 && position > duration) position = duration;
            }
            return new NowPlaying(Transport, Status, Error, Pick(live?.Title, t?.Didl.Title), Pick(live?.Artist, t?.Didl.Artist), Pick(live?.Album, t?.Didl.Album),
                (long)Math.Round(position), (long)Math.Round(duration), i != null ? Describe(i) : "", fmt?.Physical ?? "", Muted, Pick(live?.Art, t?.Didl.Art));
        }
    }

    private static string Pick(string? a, string? b) => !string.IsNullOrEmpty(a) ? a : b ?? "";

    // Live details for the track playing now (Squeezebox server, Spotify).
    public void SetLive(Track t, LiveMeta meta)
    {
        t.Live = meta;
        Changed?.Invoke();
    }

    private double PositionUnlocked()
    {
        var m = mark;
        if (m == null) return pendingOffset;
        double t = m.Offset;
        if (fmt is { } f && marks.Contains(m)) t += Math.Max(0, Heard() - m.Frame) / (double)f.Rate;
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
