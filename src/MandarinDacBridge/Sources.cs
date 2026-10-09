// Sources.cs — a track's URL → 32-bit PCM at the track's own rate.
//
// Most tracks go through ffmpeg: whatever the controller sends (FLAC, WAV,
// AIFF, ALAC, raw L16/L24, MP3, AAC…) comes out as signed 32-bit samples,
// which hold 16- and 24-bit audio exactly. Nothing is resampled while the DAC
// takes the track's rate; when it doesn't, the nearest rate it does take in
// the same family (44.1 or 48 kHz) is made with SoX's resampler.
//
// DSD files (DSF, DFF) go to the DAC as DoP when that's on for it (Dsd.cs);
// otherwise ffmpeg turns them into PCM.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MandarinDacBridge;

internal sealed record TrackInfo(int Rate, int Channels, int Bits, string Codec, int SrcRate, bool Float, double Duration, bool Dop, bool Resampled = false);

internal sealed record DacTraits(int[] Rates, int Channels, string Dsd);

internal sealed record RawPcm(string Format, int Rate, int Channels, int Bits);

internal interface IPcmSource : IDisposable
{
    TrackInfo Info { get; }
    // Up to buffer.Length bytes of PCM; 0 at the end.
    int Read(Span<byte> buffer, CancellationToken ct);
}

internal static partial class Sources
{
    public const string UserAgent = "MandarinDacBridge/1.0";

    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    static Sources() => Http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

    private static bool Family441(int r) => r % 11025 == 0;

    // The rate to send a track at: its own when the DAC takes it (or its rates
    // aren't known), else the DAC's highest in the same family that isn't
    // above it, else that family's lowest, else the DAC's highest.
    public static int PickRate(int src, int[] rates)
    {
        if (rates.Length == 0 || rates.Contains(src)) return src;
        var fam = rates.Where(r => Family441(r) == Family441(src)).OrderDescending().ToArray();
        var below = fam.FirstOrDefault(r => r <= src);
        if (below > 0) return below;
        if (fam.Length > 0) return fam[^1];
        return rates.Max();
    }

    // "audio/L24;rate=96000;channels=2" → ffmpeg's raw input.
    public static RawPcm? RawPcmOf(string? mime)
    {
        var m = RawMime().Match((mime ?? "").Trim());
        if (!m.Success) return null;
        var rate = Regex.Match(m.Groups[2].Value, @"rate=(\d+)", RegexOptions.IgnoreCase);
        var ch = Regex.Match(m.Groups[2].Value, @"channels=(\d+)", RegexOptions.IgnoreCase);
        return new RawPcm($"s{m.Groups[1].Value}be", rate.Success ? int.Parse(rate.Groups[1].Value) : 44100,
            ch.Success ? int.Parse(ch.Groups[1].Value) : 2, int.Parse(m.Groups[1].Value));
    }

    public static bool IsDsd(string? mime, string uri) =>
        DsdMime().IsMatch(mime ?? "") || DsdExt().IsMatch(uri);

    // What ffmpeg says about its input: codec, rate, depth, duration.
    public static (string Codec, int SrcRate, int Bits, bool Float, double Duration, int Channels) ParseProbe(string text)
    {
        var input = Regex.Split(text, "^Output #0|^Stream mapping", RegexOptions.Multiline)[0];
        double duration = 0;
        var d = Regex.Match(input, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (d.Success) duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
        var s = ProbeStream().Match(input);
        if (!s.Success) return ("", 0, 0, false, duration, 0);
        var layout = s.Groups[3].Value.Trim();
        int channels = layout == "mono" ? 1 : layout == "stereo" ? 2 : (Regex.Match(layout, @"(\d+) channels") is { Success: true } c ? int.Parse(c.Groups[1].Value) : 0);
        var sf = s.Groups[4].Value;
        bool isFloat = sf.StartsWith("flt") || sf.StartsWith("dbl");
        int bits = s.Groups[5].Success ? int.Parse(s.Groups[5].Value)
            : sf.StartsWith("s16") ? 16 : sf.StartsWith("s32") ? 32 : sf.StartsWith("u8") ? 8 : sf.StartsWith("flt") ? 32 : sf.StartsWith("dbl") ? 64 : 0;
        return (s.Groups[1].Value, int.Parse(s.Groups[2].Value), bits, isFloat, duration, channels);
    }

    // A source for a track, at a position.
    public static IPcmSource Open(string uri, string mime, double offset, DacTraits dac, string ffmpeg, Action<string> log, CancellationToken ct)
    {
        if (IsDsd(mime, uri) && dac.Dsd == "dop")
        {
            try
            {
                var s = DsdSource.Open(uri, offset, dac.Rates, log, ct);
                if (s != null) return s;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { log("DSD header: " + e.Message + "; trying ffmpeg"); }
        }
        var raw = RawPcmOf(mime);
        var first = FfmpegSource.Open(ffmpeg, uri, offset, raw, [], log, ct);
        var info = first.Info;
        var filters = new List<string>();
        int want = PickRate(info.Rate, dac.Rates);
        if (info.Channels == 1) filters.Add("pan=stereo|c0=c0|c1=c0");
        else if (info.Channels > Math.Max(2, dac.Channels)) filters.Add("pan=stereo|c0=FL|c1=FR");
        if (want != info.Rate) filters.Add($"aresample={want}:resampler=soxr:precision=28");
        if (filters.Count == 0) return first;
        first.Dispose();
        log($"{Path.GetFileName(uri.Split('?')[0])}: {string.Join(", ", filters)}");
        FfmpegSource second;
        try { second = FfmpegSource.Open(ffmpeg, uri, offset, raw, filters, log, ct); }
        catch (Exception) when (filters.Any(f => f.Contains("soxr")) && !ct.IsCancellationRequested)
        {
            // An ffmpeg built without SoX: its own resampler, at its best.
            log("this ffmpeg has no SoX resampler; using its own");
            var plain = filters.Select(f => f.Replace(":resampler=soxr:precision=28", ":filter_size=64:phase_shift=10:cutoff=0.97")).ToList();
            second = FfmpegSource.Open(ffmpeg, uri, offset, raw, plain, log, ct);
        }
        second.Info = second.Info with
        {
            SrcRate = info.SrcRate, Bits = info.Bits, Codec = info.Codec, Float = info.Float,
            Duration = second.Info.Duration > 0 ? second.Info.Duration : info.Duration, Resampled = want != info.Rate
        };
        return second;
    }

    [GeneratedRegex(@"^audio/l(16|24|32)\b(.*)$", RegexOptions.IgnoreCase)] private static partial Regex RawMime();
    [GeneratedRegex(@"(^|/)(x-)?(dsf|dff|dsd)\b", RegexOptions.IgnoreCase)] private static partial Regex DsdMime();
    [GeneratedRegex(@"\.(dsf|dff)(\?|#|$)", RegexOptions.IgnoreCase)] private static partial Regex DsdExt();
    [GeneratedRegex(@"Stream #\d+:\d+[^:]*: Audio:\s*([^\s,(]+)[^,]*,\s*(\d+)\s*Hz,\s*([^,]+),\s*([a-z0-9]+)(?:\s*\((\d+) bit\))?")] private static partial Regex ProbeStream();
}

// ffmpeg decoding to 32-bit PCM in WAV, whose header says the rate and channels.
internal sealed class FfmpegSource : IPcmSource
{
    private readonly Process proc;
    private readonly Stream stdout;
    private readonly StringBuilder stderr = new();
    private byte[] leftover = [];
    private int leftoverAt;
    private bool killed;

    public TrackInfo Info { get; set; } = null!;

    private FfmpegSource(Process p) { proc = p; stdout = p.StandardOutput.BaseStream; }

    public static FfmpegSource Open(string ffmpeg, string uri, double offset, RawPcm? raw, List<string> filters, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "info" }) psi.ArgumentList.Add(a);
        if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            foreach (var a in new[] { "-reconnect", "1", "-reconnect_delay_max", "5", "-user_agent", Sources.UserAgent }) psi.ArgumentList.Add(a);
        if (offset > 0) { psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(offset.ToString("0.000", CultureInfo.InvariantCulture)); }
        if (raw != null) foreach (var a in new[] { "-f", raw.Format, "-ar", raw.Rate.ToString(), "-ac", raw.Channels.ToString() }) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-i", uri, "-map", "0:a:0", "-vn", "-sn" }) psi.ArgumentList.Add(a);
        if (filters.Count > 0) { psi.ArgumentList.Add("-af"); psi.ArgumentList.Add(string.Join(",", filters)); }
        foreach (var a in new[] { "-c:a", "pcm_s32le", "-f", "wav", "pipe:1" }) psi.ArgumentList.Add(a);

        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg didn't start"); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("ffmpeg isn't installed"); }
        var s = new FfmpegSource(p);
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (s.stderr) if (s.stderr.Length < 65536) s.stderr.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        using var reg = ct.Register(s.Dispose);
        try
        {
            // The WAV header: up to 'data'.
            var head = new byte[1 << 16];
            int have = 0;
            (int Rate, int Channels, int Start)? fmt = null;
            while (fmt == null)
            {
                if (have == head.Length) Array.Resize(ref head, head.Length * 2);
                if (head.Length > 1 << 20) throw new InvalidOperationException("ffmpeg's output has no WAV header");
                int n = s.stdout.Read(head, have, head.Length - have);
                if (n <= 0) break;
                have += n;
                fmt = ParseWav(head.AsSpan(0, have));
            }
            ct.ThrowIfCancellationRequested();
            if (fmt == null)
            {
                p.WaitForExit(2000);
                string last;
                lock (s.stderr) last = s.stderr.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l != "" && !l.StartsWith(' ')).LastOrDefault() ?? "";
                throw new InvalidOperationException(last == "" ? $"ffmpeg couldn't read {uri}" : last[..Math.Min(300, last.Length)]);
            }
            s.leftover = head;
            s.leftoverAt = fmt.Value.Start;
            Array.Resize(ref s.leftover, have);
            // The input's lines come before the output starts; give them a moment.
            for (int i = 0; i < 10; i++) { lock (s.stderr) if (s.stderr.ToString().Contains("Stream #")) break; Thread.Sleep(5); }
            string text;
            lock (s.stderr) text = s.stderr.ToString();
            var probe = Sources.ParseProbe(text);
            s.Info = new TrackInfo(fmt.Value.Rate, fmt.Value.Channels, raw?.Bits ?? probe.Bits, probe.Codec,
                probe.SrcRate > 0 ? probe.SrcRate : fmt.Value.Rate, probe.Float, probe.Duration, false);
            return s;
        }
        catch (Exception)
        {
            s.Dispose();
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    public static (int Rate, int Channels, int Start)? ParseWav(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12) return null;
        if (Encoding.Latin1.GetString(b[..4]) != "RIFF" || Encoding.Latin1.GetString(b[8..12]) != "WAVE") throw new InvalidOperationException("ffmpeg's output isn't WAV");
        int at = 12;
        (int, int)? f = null;
        while (at + 8 <= b.Length)
        {
            var id = Encoding.Latin1.GetString(b.Slice(at, 4));
            uint size = BitConverter.ToUInt32(b.Slice(at + 4, 4));
            if (id == "data") return f is { } x ? (x.Item1, x.Item2, at + 8) : null;
            if (at + 8 + size > b.Length) return null;
            if (id == "fmt ") f = ((int)BitConverter.ToUInt32(b.Slice(at + 12, 4)), BitConverter.ToUInt16(b.Slice(at + 10, 2)));
            at += 8 + (int)size + (int)(size & 1);
        }
        return null;
    }

    public int Read(Span<byte> buffer, CancellationToken ct)
    {
        if (leftoverAt < leftover.Length)
        {
            int n = Math.Min(buffer.Length, leftover.Length - leftoverAt);
            leftover.AsSpan(leftoverAt, n).CopyTo(buffer);
            leftoverAt += n;
            return n;
        }
        if (killed) return 0;
        try { return stdout.Read(buffer); }
        catch (Exception) when (killed || ct.IsCancellationRequested) { return 0; }
    }

    public void Dispose()
    {
        if (killed) return;
        killed = true;
        try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { /* gone */ }
        try { proc.Dispose(); } catch (Exception) { /* gone */ }
    }
}

// A DSF/DFF file over HTTP → DoP frames.
internal sealed class DsdSource : IPcmSource
{
    private readonly HttpResponseMessage response;
    private readonly Stream body;
    private readonly DopPacker packer;
    private byte[] pending = [];
    private int pendingAt;
    private bool finished, disposed;
    private readonly byte[] chunk = new byte[1 << 16];

    public TrackInfo Info { get; }

    private DsdSource(HttpResponseMessage r, Stream b, DopPacker p, TrackInfo info) { response = r; body = b; packer = p; Info = info; }

    public static DsdSource? Open(string uri, double offset, int[] dopRates, Action<string> log, CancellationToken ct)
    {
        var head = FetchHead(uri, ct);
        var h = Dsd.Parse(head);
        if (h == null) return null;
        int dopRate = h.Rate / 16;
        if (dopRates.Length > 0 && !dopRates.Contains(dopRate))
        {
            log($"{Dsd.Name(h.Rate)} needs {Devices.KHz(dopRate)} for DoP, which the DAC doesn't take: converting to PCM");
            return null;
        }
        var (off, consumed) = Dsd.SeekOffset(h, offset);
        long start = h.DataStart + off;
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);
        var res = Sources.Http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var stream = res.Content.ReadAsStream(ct);
        if (res.StatusCode != System.Net.HttpStatusCode.PartialContent) Skip(stream, start, ct);
        var info = new TrackInfo(dopRate, h.Channels, 1, Dsd.Name(h.Rate), h.Rate, false, h.Seconds, true);
        return new DsdSource(res, stream, new DopPacker(h, consumed), info);
    }

    private static byte[] FetchHead(string uri, CancellationToken ct, int bytes = 65536)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, bytes - 1);
        using var res = Sources.Http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using var s = res.Content.ReadAsStream(ct);
        var buf = new byte[bytes];
        int have = 0;
        while (have < bytes)
        {
            int n = s.Read(buf, have, bytes - have);
            if (n <= 0) break;
            have += n;
        }
        return buf[..have];
    }

    private static void Skip(Stream s, long n, CancellationToken ct)
    {
        var junk = new byte[1 << 16];
        while (n > 0)
        {
            ct.ThrowIfCancellationRequested();
            int r = s.Read(junk, 0, (int)Math.Min(junk.Length, n));
            if (r <= 0) return;
            n -= r;
        }
    }

    public int Read(Span<byte> buffer, CancellationToken ct)
    {
        while (pendingAt >= pending.Length)
        {
            if (finished || disposed) return 0;
            ct.ThrowIfCancellationRequested();
            int n = 0;
            if (!packer.Done)
            {
                try { n = body.Read(chunk, 0, chunk.Length); }
                catch (Exception) when (disposed || ct.IsCancellationRequested) { return 0; }
            }
            pending = n > 0 ? packer.Push(chunk.AsSpan(0, n)) : packer.Finish();
            pendingAt = 0;
            if (n <= 0) finished = true;
        }
        int c = Math.Min(buffer.Length, pending.Length - pendingAt);
        pending.AsSpan(pendingAt, c).CopyTo(buffer);
        pendingAt += c;
        return c;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { body.Dispose(); } catch (Exception) { /* closed */ }
        response.Dispose();
    }
}
