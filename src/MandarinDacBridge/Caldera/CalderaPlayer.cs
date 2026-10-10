// CalderaPlayer.cs — a DAC as a Plex player, through Caldera Headless
// (caldera-music: a Linux audio daemon that Plexamp, Plex for iOS and the
// Plex web app play to).
//
// Caldera plays to an ALSA device itself, bit-perfect when its output rate
// matches the source, so it doesn't play through the bridge: it shares the
// DAC with it, as Roon Bridge does. Each DAC gets its own caldera-music,
// named "<DAC> (Bridge)", pointed at the DAC's hw device and set up for
// bit-perfect playback: rate and channels follow the source, volume 100%,
// no loudness levelling, no SweetFades crossfades. Caldera lets go of the
// DAC a moment after it stops (audio.idleReleaseMs), and the bridge lets go
// of it while idle (share when idle, forced on while Caldera is), so each
// gets it in turn: whoever plays first has it until it stops.
//
// What Caldera plays is read from its Plex Companion timeline
// (/player/timeline/poll) and the Plex server's metadata, for the page and
// the now-playing screen; the cover is fetched by the bridge (the server
// wants the Plex token, which the page never sees).
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MandarinDacBridge.Caldera;

internal sealed record CalderaSetup(string Program, string Home, string Token);

internal sealed partial class CalderaPlayer : IDisposable
{
    private sealed class NotReady(string message) : Exception(message);

    private static readonly HttpClient Local = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly Bridge bridge;
    private readonly Func<CalderaSetup> setup;
    private readonly string configDir;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread runner;
    private readonly Timer poll;
    private readonly object gate = new();
    private Process? proc;
    private string lastError = "";
    private string ratingKey = "";
    private LiveMeta meta = new("", "", "", "", 0);
    private string format = "";
    private int polling;

    public int Port { get; }
    public string Status { get; private set; } = "starting";
    // What Caldera is playing (null when nothing), and where its cover is (server side, with the token).
    public NowPlaying? Now { get; private set; }
    public string ArtUrl { get; private set; } = "";
    public bool Holding => Now?.Transport is "PLAYING" or "PAUSED_PLAYBACK";
    // plex.tv refused the token: the sign-in has to be done again.
    public event Action? TokenRejected;

    public CalderaPlayer(Bridge bridge, string dataDir, int port, Func<CalderaSetup> setup)
    {
        this.bridge = bridge;
        this.setup = setup;
        Port = port;
        configDir = Path.Combine(CalderaDownload.Home(dataDir), "players", bridge.Id);
        runner = new Thread(Run) { IsBackground = true, Name = "caldera " + bridge.Id };
        runner.Start();
        poll = new Timer(_ => _ = Poll(), null, 2000, 1000);
    }

    private void Log(string m) => bridge.Log("caldera: " + m);

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        bridge.Notify();
    }

    // ------------------------------------------------------------ the daemon

    private void Run()
    {
        int quick = 0;
        while (!stop.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            bool notReady = false;
            try { RunOnce(); }
            catch (Exception e)
            {
                if (stop.IsCancellationRequested) return;
                notReady = e is NotReady;
                if (e.Message != lastError) Log(e.Message);
                lastError = e.Message;
                SetStatus(e.Message);
            }
            finally { Kill(); }
            if (stop.IsCancellationRequested) return;
            quick = notReady ? 0 : Environment.TickCount64 - started < 30_000 ? quick + 1 : 0;
            stop.Token.WaitHandle.WaitOne(notReady ? 5000 : Math.Min(60_000, 2000 * (1 << Math.Min(quick, 5))));
        }
    }

    private ProcessStartInfo Start(string program, string home, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Its own libraries (ffmpeg's), and HOME where its self-update looks for itself.
        psi.Environment["LD_LIBRARY_PATH"] = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(program)!)!, "lib");
        psi.Environment["HOME"] = home;
        return psi;
    }

    private void RunOnce()
    {
        var s = setup();
        if (!OperatingSystem.IsLinux()) throw new NotReady("Caldera Headless runs on Linux only");
        if (!File.Exists(s.Program)) throw new NotReady("needs Caldera Headless: download it on this page");
        if (s.Token == "") throw new NotReady("needs a Plex sign-in: link it on this page");
        Directory.CreateDirectory(configDir);

        // Bit-perfect, on this DAC only, under the bridge's name for it.
        var prefs = new List<string> { "--config", configDir };
        foreach (var kv in new[]
                 {
                     $"plex.token={s.Token}", $"companion.clientName={bridge.FriendlyName()}", $"companion.port={Port}",
                     $"audio.outputDeviceUid={bridge.Dev.Spec}", "audio.sampleRate=0", "audio.channels=0", "audio.masterVolume=100",
                     "player.loudnessLeveling=no", "player.sweetFades=no", "audio.idleReleaseMs=1500"
                 })
        {
            prefs.Add("--set");
            prefs.Add(kv);
        }
        using (var set = Process.Start(Start(s.Program, s.Home, prefs)) ?? throw new InvalidOperationException("caldera-music didn't start"))
        {
            var err = set.StandardError.ReadToEndAsync();
            set.StandardOutput.ReadToEnd();
            if (!set.WaitForExit(10_000)) { set.Kill(); throw new InvalidOperationException("caldera-music --set didn't finish"); }
            if (set.ExitCode != 0) throw new InvalidOperationException("caldera-music --set: " + Last(err.Result));
        }

        // Its own port for Caldera's multi-room audio too (9999 is taken by the first one).
        var psi = Start(s.Program, s.Home, ["--config", configDir, "--player-name", bridge.FriendlyName(), "--xita-port", (9999 + Port - 32500).ToString()]);
        bool rejected = false;
        var lines = new Queue<string>();
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("caldera-music didn't start"); }
        catch (System.ComponentModel.Win32Exception e) { throw new NotReady($"can't run {s.Program}: {e.Message}"); }
        void Line(string? l)
        {
            if (l == null) return;
            lock (lines) { lines.Enqueue(l); while (lines.Count > 20) lines.Dequeue(); }
            if (l.Contains("token rejected") || l.Contains("auth_expired")) rejected = true;
            if (l.Contains(" E (") || l.Contains("No auth token")) Log(l.Length > 200 ? l[..200] : l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (gate) proc = p;
        Log($"offering {bridge.FriendlyName()} to Plex (Caldera, port {Port})");
        SetStatus("ready · choose it in Plexamp");
        lastError = "";
        while (!p.WaitForExit(500)) if (stop.IsCancellationRequested) return;
        string last;
        lock (lines) last = lines.LastOrDefault(l => l.Trim() != "") ?? "";
        if (last.Contains("No auth token")) throw new NotReady("needs a Plex sign-in: link it on this page");
        if (rejected)
        {
            TokenRejected?.Invoke();
            throw new NotReady("Plex didn't accept the sign-in: sign in to Plex again on this page");
        }
        throw new InvalidOperationException($"caldera-music stopped ({p.ExitCode}){(last != "" ? ": " + last : "")}");
    }

    private static string Last(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l != "") ?? "";

    private void Kill()
    {
        Process? p;
        lock (gate) { p = proc; proc = null; }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch (Exception) { /* gone */ }
        p.Dispose();
        Now = null;
    }

    // ------------------------------------------------------------ what it plays

    private async Task Poll()
    {
        if (Interlocked.Exchange(ref polling, 1) == 1) return;
        try
        {
            lock (gate) if (proc is not { HasExited: false }) { Now = null; return; }
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/player/timeline/poll?wait=0&commandID=1");
            req.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", "mandarin-dac-bridge-" + bridge.Id);
            req.Headers.TryAddWithoutValidation("X-Plex-Device-Name", "Mandarin DAC Bridge");
            using var res = await Local.SendAsync(req, stop.Token);
            var xml = await res.Content.ReadAsStringAsync(stop.Token);
            await Apply(Timeline(xml));
        }
        catch (Exception) { /* not up yet */ }
        finally { Volatile.Write(ref polling, 0); }
    }

    internal sealed record TimelineState(string State, double Time, double Duration, string Key, string RatingKey, string Server, string Token);

    // The music timeline of a Plex Companion poll: state, position, the track and its server.
    public static TimelineState? Timeline(string xml)
    {
        var m = MusicTimeline().Match(xml);
        if (!m.Success) return null;
        var tag = m.Value;
        double Ms(string k) => double.TryParse(Xml.Attr(tag, k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v / 1000 : 0;
        var address = Xml.Attr(tag, "address");
        var port = Xml.Attr(tag, "port");
        var protocol = Xml.Attr(tag, "protocol") is { Length: > 0 } pr ? pr : "http";
        var server = address != "" ? $"{protocol}://{(address.Contains(':') ? $"[{address}]" : address)}{(port != "" ? ":" + port : "")}" : "";
        return new TimelineState(Xml.Attr(tag, "state"), Ms("time"), Ms("duration"), Xml.Attr(tag, "key"), Xml.Attr(tag, "ratingKey"), server, Xml.Attr(tag, "token"));
    }

    private async Task Apply(TimelineState? t)
    {
        var transport = t?.State switch { "playing" => "PLAYING", "paused" => "PAUSED_PLAYBACK", "buffering" => "TRANSITIONING", _ => "STOPPED" };
        if (t == null || transport == "STOPPED")
        {
            if (Now != null) { Now = null; SetStatus("ready · choose it in Plexamp"); bridge.Notify(); }
            return;
        }
        if (t.RatingKey != ratingKey && t.Key != "" && t.Server != "")
        {
            ratingKey = t.RatingKey;
            await FetchMetadata(t);
        }
        var m = meta;
        Now = new NowPlaying(transport, "OK", "", m.Title, m.Artist, m.Album, (long)Math.Round(t.Time),
            (long)Math.Round(t.Duration > 0 ? t.Duration : m.Duration), format, "Caldera", false,
            ArtUrl != "" ? $"/api/dacs/{bridge.Id}/art?k={Uri.EscapeDataString(ratingKey)}" : "");
        SetStatus(transport == "PLAYING" ? "playing" : "paused");
        bridge.Notify();
    }

    private async Task FetchMetadata(TimelineState t)
    {
        var token = t.Token != "" ? t.Token : setup().Token;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, t.Server + t.Key);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            using var res = await Sources.Http.SendAsync(req, stop.Token);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(stop.Token));
            var (m, f, thumb) = Metadata(doc.RootElement);
            meta = m;
            format = f;
            ArtUrl = thumb != "" ? $"{t.Server}{thumb}{(thumb.Contains('?') ? '&' : '?')}X-Plex-Token={Uri.EscapeDataString(token)}" : "";
        }
        catch (Exception e)
        {
            if (!stop.IsCancellationRequested) Log("Plex metadata: " + e.Message);
            meta = new LiveMeta("", "", "", "", t.Duration);
            format = "";
            ArtUrl = "";
        }
    }

    // Plex's metadata for a track → title, artist, album, its format ("96 kHz · 24-bit · FLAC") and cover path.
    public static (LiveMeta Meta, string Format, string Thumb) Metadata(JsonElement root)
    {
        static string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
        static int I(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var mc = root.TryGetProperty("MediaContainer", out var c) ? c : root;
        if (!mc.TryGetProperty("Metadata", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return (new LiveMeta("", "", "", "", 0), "", "");
        var t = list[0];
        var artist = S(t, "originalTitle") is { Length: > 0 } o ? o : S(t, "grandparentTitle");
        double duration = I(t, "duration") / 1000.0;
        var thumb = S(t, "thumb") is { Length: > 0 } th ? th : S(t, "parentThumb");
        var fmt = "";
        if (t.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Array && media.GetArrayLength() > 0)
        {
            var md = media[0];
            int rate = 0, bits = I(md, "bitDepth");
            var codec = S(md, "audioCodec");
            if (md.TryGetProperty("Part", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0
                && parts[0].TryGetProperty("Stream", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var st in streams.EnumerateArray())
                    if (I(st, "streamType") == 2)
                    {
                        rate = I(st, "samplingRate");
                        if (bits == 0) bits = I(st, "bitDepth");
                        if (codec == "") codec = S(st, "codec");
                        break;
                    }
            var parts2 = new List<string>();
            if (rate > 0) parts2.Add(Devices.KHz(rate));
            if (bits > 0) parts2.Add($"{bits}-bit");
            if (codec != "") parts2.Add(codec.ToUpperInvariant());
            fmt = string.Join(" · ", parts2);
        }
        return (new LiveMeta(S(t, "title"), artist, S(t, "parentTitle"), "", duration), fmt, thumb);
    }

    public void Dispose()
    {
        stop.Cancel();
        poll.Dispose();
        runner.Join(TimeSpan.FromSeconds(5));
        Kill();
    }

    [GeneratedRegex(@"<Timeline\b[^>]*\btype\s*=\s*""music""[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex MusicTimeline();
}
