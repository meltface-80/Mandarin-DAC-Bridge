// SoloistPlayer.cs — a DAC as a Spotify Connect speaker, through Spotify
// Soloist (Spotify's own headless Connect client for Linux).
//
// Soloist plays to PipeWire or PulseAudio, never to a file or a pipe of its
// own, and the bridge holds the DAC. So each DAC's Soloist gets a private
// PulseAudio server of its own, with no sound card in it, only a pipe sink:
// what Soloist plays comes out of a named pipe as 32-bit samples at 44.1 kHz,
// and the bridge plays them through the same renderer as everything else.
// The pipe sink waits for its reader, so the DAC's clock sets the pace: no
// drift, nothing resampled, 16- and 24-bit audio carried exactly.
//
//   Soloist ──libpulse──▶ private PulseAudio ──pipe sink, S32 44.1 kHz──▶ "live:spotify/<id>" ─▶ renderer ─▶ DAC
//   Soloist ──WebSocket (127.0.0.1)──▶ here: playing / paused / idle, title, artists, album, cover, position
//
// Soloist finds the private server by the environment it is started with
// (XDG_RUNTIME_DIR, PULSE_SERVER), with nowhere to find a PipeWire, so it
// falls back to PulseAudio. "playing" starts the DAC (if the arbiter lets
// Spotify have it: otherwise Soloist is told to pause, so the Spotify app
// shows it didn't play); "paused" and "idle" pause and stop it. The volume
// is the DAC's own, where it has one (set in the DAC: bit-perfect): Spotify's
// slider moves it, and a change made elsewhere is passed back to Spotify.
// Otherwise it stays at 100%.
//
// Each user brings their own Soloist API key (Spotify for Developers), and
// Soloist itself: it may not be redistributed, so the bridge downloads it
// from Spotify on request (SoloistDownload), as RoPieee does Roon Bridge.
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace MandarinDacBridge.Spotify;

internal sealed record SoloistSetup(string Soloist, string PulseAudio, string ApiKey);

internal sealed partial class SoloistPlayer : IDisposable
{
    public const int Rate = 44100;
    private const int FrameBytes = 8;      // 2 channels of S32
    private const int ExitExpired = 10;    // Soloist: this build is past its 90 days

    private sealed class NotReady(string message) : Exception(message);

    private readonly Bridge bridge;
    private readonly Func<SoloistSetup> setup;
    private readonly string dataDir, cacheDir, runDir, fifo, socket, liveName;
    private readonly Caller caller;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Thread runner;
    private readonly Queue<byte[]> preroll = new();    // the last moments heard, for the start of playback
    private Process? pulse, soloist;
    private ClientWebSocket? ws;
    private Thread? reader;
    private volatile bool readerStop;
    private LiveSource? attached;
    private Track? track;
    private LiveMeta meta = new("", "", "", "", 0);
    private string lastError = "";
    private string soloistError = "";
    private readonly object logGate = new();
    private bool? signedIn, active;
    private bool starting;                             // a start is on its way to the renderer

    public string Status { get; private set; } = "starting";
    public event Action? Expired;

    public SoloistPlayer(Bridge bridge, string dataRoot, int port, Func<SoloistSetup> setup)
    {
        this.bridge = bridge;
        this.setup = setup;
        dataDir = Path.Combine(dataRoot, "soloist", bridge.Id, "data");
        cacheDir = Path.Combine(dataRoot, "soloist", bridge.Id, "cache");
        // Short: a unix socket's path has to fit in 108 bytes.
        runDir = Path.Combine(Path.GetTempPath(), $"mdb-{port}-{bridge.Id}");
        fifo = Path.Combine(runDir, "sound");
        socket = Path.Combine(runDir, "pulse", "native");
        liveName = "spotify/" + bridge.Id;
        caller = new Caller("spotify|" + bridge.Id, "Spotify", "127.0.0.1");
        Sources.RegisterLive(liveName, Open);
        bridge.VolumeChanged += TellVolume;
        runner = new Thread(Run) { IsBackground = true, Name = "soloist " + bridge.Id };
        runner.Start();
    }

    private void Log(string m) => bridge.Log("spotify: " + m);

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        bridge.Notify();
    }

    private void Wait(int ms) => stop.Token.WaitHandle.WaitOne(ms);

    // ------------------------------------------------------------ Soloist and its sound server

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
                // Switched off: stopping on purpose (and nothing may escape this thread).
                if (stop.IsCancellationRequested) return;
                notReady = e is NotReady;
                var why = e is AggregateException a ? a.InnerException?.Message ?? a.Message : e.Message;
                if (why != lastError) Log(why);
                lastError = why;
                SetStatus(why);
            }
            finally { Teardown(); }
            if (stop.IsCancellationRequested) return;
            // Something missing (a key, Soloist, PulseAudio): look again soon. A crash: back off.
            quick = notReady ? 0 : Environment.TickCount64 - started < 30_000 ? quick + 1 : 0;
            Wait(notReady ? 5000 : Math.Min(60_000, 2000 * (1 << Math.Min(quick, 5))));
        }
    }

    private void RunOnce()
    {
        var s = setup();
        if (!OperatingSystem.IsLinux()) throw new NotReady("Spotify Soloist runs on Linux only");
        if (s.Soloist == "") throw new NotReady("needs Spotify Soloist: download it on this page");
        if (s.ApiKey == "") throw new NotReady("needs your Soloist API key");
        if (s.PulseAudio == "") throw new NotReady("needs PulseAudio for Soloist's sound: sudo apt install pulseaudio");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(Path.GetDirectoryName(socket)!);
        File.Delete(fifo);
        File.Delete(socket);
        if (!Native.Libc.MakeFifo(fifo)) throw new IOException("couldn't make the pipe " + fifo);

        StartPulse(s.PulseAudio);
        readerStop = false;
        reader = new Thread(ReadSound) { IsBackground = true, Name = "soloist-sound " + bridge.Id, Priority = ThreadPriority.AboveNormal };
        reader.Start();
        StartSoloist(s);
        Listen();

        var p = soloist;
        if (p != null && p.WaitForExit(3000))
        {
            if (p.ExitCode == ExitExpired)
            {
                Expired?.Invoke();
                throw new NotReady("this Soloist build has expired (Spotify's builds last 90 days): download it again on this page");
            }
            if (soloistError.StartsWith("Soloist needs")) throw new NotReady(soloistError);
            throw new InvalidOperationException($"Soloist stopped ({p.ExitCode}){(soloistError != "" ? ": " + soloistError : "")}");
        }
        throw new InvalidOperationException("Soloist's WebSocket closed");
    }

    private void StartSoloist(SoloistSetup s)
    {
        foreach (var f in new[] { "ws.port", "ws.addr" }) File.Delete(Path.Combine(dataDir, f));
        var psi = new ProcessStartInfo(s.Soloist) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "--device-name", bridge.FriendlyName(), "--api-key", s.ApiKey, "--data-dir", dataDir, "--cache-dir", cacheDir,
                     "--initial-volume", (bridge.Level ?? 100).ToString(System.Globalization.CultureInfo.InvariantCulture), "--ws", "127.0.0.1:0"
                 })
            psi.ArgumentList.Add(a);
        // SOLOIST_VERBOSE=1: Soloist's detailed log, into soloist.log (for finding out why a connection fails).
        if (Environment.GetEnvironmentVariable("SOLOIST_VERBOSE") == "1") psi.ArgumentList.Add("--verbose");
        foreach (var (k, v) in PrivateEnvironment()) psi.Environment[k] = v;
        soloistError = "";
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("Soloist didn't start"); }
        catch (System.ComponentModel.Win32Exception e) { throw new NotReady($"can't run {s.Soloist}: {e.Message}"); }
        void Line(string? l)
        {
            if (l == null) return;
            Keep(l);
            if (l.Contains("GLIBC_") || l.Contains("error while loading shared libraries")) soloistError = SoloistDownload.Explain(l);
            else if (l.Contains("rror") || l.Contains("WARN") || l.Contains("arning")) soloistError = l.Length > 200 ? l[..200] : l;
            else return;
            Log(l.Length > 200 ? l[..200] : l);
        }
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        lock (gate) soloist = p;
        Log($"offering {bridge.FriendlyName()} to Spotify (Soloist)");
    }

    // All of Soloist's own output, in soloist.log beside its data (readable by its owner only; up to 2 MB, then
    // the old one is kept as soloist.log.1). It can name the account: don't post it publicly.
    private string LogFile => Path.Combine(Path.GetDirectoryName(dataDir)!, "soloist.log");

    private void Keep(string line)
    {
        lock (logGate)
        {
            try
            {
                var f = LogFile;
                var info = new FileInfo(f);
                if (info.Exists && info.Length > 2_000_000) File.Move(f, f + ".1", overwrite: true);
                bool fresh = !File.Exists(f);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}\n");
                if (fresh && !OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception) { /* a log, at best */ }
        }
    }

    private void Teardown()
    {
        readerStop = true;
        // The reader holds the pipe open both ways, so a byte wakes it.
        if (reader is { IsAlive: true })
            try { using var nudge = new FileStream(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1); nudge.Write(new byte[FrameBytes]); }
        catch (Exception) { /* no pipe, or no reader */ }
        Process? s, p;
        ClientWebSocket? w;
        lock (gate) { s = soloist; p = pulse; w = ws; soloist = null; pulse = null; ws = null; }
        foreach (var x in new[] { s, p })
        {
            if (x == null) continue;
            try { if (!x.HasExited) x.Kill(entireProcessTree: true); x.WaitForExit(2000); } catch (Exception) { /* gone */ }
            x.Dispose();
        }
        try { w?.Abort(); w?.Dispose(); } catch (Exception) { /* closing */ }
        reader?.Join(TimeSpan.FromSeconds(2));
        reader = null;
        Detach(null);
        if (Ours() && bridge.Renderer.IsActive) Try(bridge.Renderer.Stop);
        try { File.Delete(fifo); } catch (Exception) { /* gone */ }
    }

    // ------------------------------------------------------------ the sound

    private bool Ours() { lock (gate) return track != null && bridge.Renderer.Current == track; }

    // Spotify is playing: the DAC is taken (if the arbiter allows) and the renderer opens the stream.
    private void Play()
    {
        var r = bridge.Renderer;
        if (Ours() && r.IsActive) { SetStatus("playing"); return; }
        lock (gate) { if (starting) return; }
        if (bridge.Arbiter.Claim(caller, "Play", r.IsActive) is not null) { Refuse(); return; }
        if (Ours() && r.Transport == "PAUSED_PLAYBACK")
        {
            Try(r.Play);
            SetStatus("playing");
            return;
        }
        var s = new LiveSource(new TrackInfo(Rate, 2, 0, "Spotify", Rate, true, 0, false));
        lock (gate)
        {
            // From the first sound already heard, so the first notes aren't lost.
            bool sound = false;
            foreach (var c in preroll)
            {
                sound |= HasSound(c);
                if (sound) s.Preload(c);
            }
            preroll.Clear();
            attached?.Dispose();
            attached = s;
            starting = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var t = await r.SetUri("live:" + liveName, Didl());
                lock (gate) { track = t; t.Live = meta; }
                await r.Play();
                Log("playing");
                SetStatus("playing");
            }
            catch (Exception e)
            {
                Log("couldn't play: " + e.Message);
                Detach(s);
                SetStatus("couldn't play: " + e.Message);
            }
            finally { lock (gate) starting = false; }
        });
    }

    // Another app has the DAC: Spotify is paused, so its app shows that it didn't play here.
    private void Refuse()
    {
        Log($"refused: {bridge.Arbiter.Owner?.Name} is using the DAC");
        bridge.Notify();
        SetStatus($"kept out · {bridge.Arbiter.Owner?.Name} is using the DAC");
        _ = Command("{\"type\":\"command\",\"command\":\"pause\"}");
    }

    private static string Didl() =>
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">"
        + "<item id=\"spotify\" parentID=\"0\" restricted=\"1\"><dc:title>Spotify</dc:title><upnp:class>object.item.audioItem.musicTrack</upnp:class></item></DIDL-Lite>";

    // ------------------------------------------------------------ Soloist's WebSocket

    private static string Clip(string s) => s.Length > 4000 ? s[..4000] + "…" : s;

    private void SetItem(JsonElement item)
    {
        var e = Entity(item);
        if (e == null) { Log("a track without details from Soloist (see soloist.log)"); return; }
        bool changed;
        lock (gate) { changed = e.Title != meta.Title || e.Artist != meta.Artist; meta = e with { Position = meta.Position, At = meta.At }; }
        if (changed && e.Title != "") Log($"now playing: {e.Title}{(e.Artist != "" ? " — " + e.Artist : "")}");
        Publish();
    }

    private void SetPosition(JsonElement p)
    {
        double ms = p.TryGetProperty("position_ms", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0;
        long at = p.TryGetProperty("timestamp_ms", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : Arbiter.Now;
        // speed 0: not advancing (paused, buffering): the position as it stands, from now.
        if (p.TryGetProperty("speed", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.GetDouble() == 0) at = Arbiter.Now;
        lock (gate) meta = meta with { Position = ms / 1000, At = at };
        Publish();
    }

    private void Publish()
    {
        Track? t;
        LiveMeta m;
        lock (gate) { t = track; m = meta; }
        if (t != null && bridge.Renderer.Current == t) bridge.Renderer.SetLive(t, m);
    }

    // A Soloist entity → title, artists, album, cover, duration.
    public static LiveMeta? Entity(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("entity", out var inner) && inner.ValueKind == JsonValueKind.Object) e = inner;
        if (!e.TryGetProperty("decorations", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        static string Name(JsonElement x)
        {
            if (x.TryGetProperty("entity", out var i) && i.ValueKind == JsonValueKind.Object) x = i;
            return x.TryGetProperty("decorations", out var dd) && dd.TryGetProperty("identity", out var id) ? Str(id, "name") : "";
        }
        var title = d.TryGetProperty("identity", out var ident) ? Str(ident, "name") : "";
        var artists = d.TryGetProperty("creators", out var cr) && cr.ValueKind == JsonValueKind.Array
            ? string.Join(", ", cr.EnumerateArray().Select(Name).Where(n => n != ""))
            : "";
        var album = d.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object ? Name(parent) : "";
        var art = "";
        if (d.TryGetProperty("visual_identity", out var vi) && vi.TryGetProperty("cover", out var covers) && covers.ValueKind == JsonValueKind.Array)
        {
            var list = covers.EnumerateArray().Select(c => (Size: Str(c, "size"), Url: Str(c, "url"))).Where(c => c.Url != "").ToList();
            foreach (var want in new[] { "large", "xlarge", "default", "small" })
                if (list.FirstOrDefault(c => c.Size == want) is { Url: { Length: > 0 } u }) { art = u; break; }
            if (art == "" && list.Count > 0) art = list[0].Url;
        }
        double duration = d.TryGetProperty("playback", out var pb) && pb.TryGetProperty("duration_ms", out var dm) && dm.ValueKind == JsonValueKind.Number
            ? dm.GetDouble() / 1000 : 0;
        return new LiveMeta(title, artists, album, art, duration);
    }

    private static string Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private void Try(Func<Task> action)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (Exception e) { Log(e.Message); }
    }

    public void Dispose()
    {
        stop.Cancel();
        bridge.VolumeChanged -= TellVolume;
        Sources.UnregisterLive(liveName);
        runner.Join(TimeSpan.FromSeconds(8));
        if (Ours()) { try { bridge.Renderer.Stop().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* closing */ } }
    }
}
