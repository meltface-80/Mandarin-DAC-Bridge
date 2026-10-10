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
// stays at 100%: bit-perfect, as for every other way in.
//
// Each user brings their own Soloist API key (Spotify for Developers), and
// Soloist itself: it may not be redistributed, so the bridge downloads it
// from Spotify on request (SoloistDownload), as RoPieee does Roon Bridge.
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MandarinDacBridge.Spotify;

internal sealed record SoloistSetup(string Soloist, string PulseAudio, string ApiKey);

internal sealed class SoloistPlayer : IDisposable
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

    private Dictionary<string, string> PrivateEnvironment() => new()
    {
        // Only the private server: no PipeWire to be found, PulseAudio at our socket.
        ["XDG_RUNTIME_DIR"] = runDir,
        ["PIPEWIRE_RUNTIME_DIR"] = runDir,
        ["PULSE_RUNTIME_PATH"] = Path.GetDirectoryName(socket)!,
        ["PULSE_STATE_PATH"] = Path.Combine(runDir, "state"),
        ["PULSE_SERVER"] = "unix:" + socket
    };

    private void StartPulse(string exe)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "-n", "--daemonize=no", "--exit-idle-time=-1", "--use-pid-file=no", "--disable-shm=yes", "--log-target=stderr",
                     $"--load=module-native-protocol-unix socket={socket} auth-anonymous=1",
                     $"--load=module-pipe-sink file={fifo} sink_name=bridge format=s32le rate={Rate} channels=2"
                 })
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in PrivateEnvironment()) psi.Environment[k] = v;
        var errors = new StringBuilder();
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("PulseAudio didn't start"); }
        catch (System.ComponentModel.Win32Exception e) { throw new NotReady($"can't run {exe}: {e.Message}"); }
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } l && l.StartsWith("E:")) lock (errors) errors.AppendLine(l); };
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        lock (gate) pulse = p;
        for (int i = 0; i < 100 && !File.Exists(socket); i++)
        {
            if (p.HasExited) break;
            Wait(50);
        }
        if (!File.Exists(socket))
        {
            string e;
            lock (errors) e = errors.ToString().Trim().Split('\n').LastOrDefault() ?? "";
            throw new InvalidOperationException("Soloist's sound server didn't start" + (e != "" ? ": " + e : ""));
        }
    }

    private void StartSoloist(SoloistSetup s)
    {
        foreach (var f in new[] { "ws.port", "ws.addr" }) File.Delete(Path.Combine(dataDir, f));
        var psi = new ProcessStartInfo(s.Soloist) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "--device-name", bridge.FriendlyName(), "--api-key", s.ApiKey, "--data-dir", dataDir, "--cache-dir", cacheDir,
                     "--initial-volume", "100", "--ws", "127.0.0.1:0"
                 })
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in PrivateEnvironment()) psi.Environment[k] = v;
        soloistError = "";
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("Soloist didn't start"); }
        catch (System.ComponentModel.Win32Exception e) { throw new NotReady($"can't run {s.Soloist}: {e.Message}"); }
        void Line(string? l)
        {
            if (l == null) return;
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

    // What the sound server writes: to the renderer while it plays it, else let go at the speed of a DAC.
    private void ReadSound()
    {
        FileStream pipe;
        try { pipe = new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0); }
        catch (Exception e) { Log("sound pipe: " + e.Message); return; }
        using (pipe)
        {
            var buf = new byte[1 << 14];
            int have = 0;
            var pace = Stopwatch.StartNew();
            long paced = 0;
            while (!readerStop && !stop.IsCancellationRequested)
            {
                int n;
                try { n = pipe.Read(buf, have, buf.Length - have); }
                catch (Exception) { return; }
                if (n <= 0 || readerStop) return;
                have += n;
                int usable = have - have % FrameBytes;
                if (usable == 0) continue;
                var chunk = buf[..usable];
                Array.Copy(buf, usable, buf, 0, have - usable);
                have -= usable;

                LiveSource? s;
                lock (gate) s = attached;
                if (s != null)
                {
                    if (s.Push(chunk, stop.Token)) { pace.Restart(); paced = 0; continue; }
                    Detach(s);
                }
                lock (gate)
                {
                    preroll.Enqueue(chunk);
                    while (preroll.Count > 8) preroll.Dequeue();
                }
                paced += chunk.Length / FrameBytes;
                var ahead = paced * 1000 / Rate - pace.ElapsedMilliseconds;
                if (ahead > 0) stop.Token.WaitHandle.WaitOne((int)ahead);
                if (pace.ElapsedMilliseconds > 2000) { pace.Restart(); paced = 0; }
            }
        }
    }

    private static bool HasSound(byte[] chunk)
    {
        foreach (var b in chunk) if (b != 0) return true;
        return false;
    }

    // The renderer opens the stream: the one being filled.
    private IPcmSource Open(CancellationToken ct)
    {
        lock (gate) return attached is { Closed: false } s ? s : throw new InvalidOperationException("Spotify isn't playing");
    }

    private void Detach(LiveSource? which)
    {
        lock (gate)
        {
            if (which != null && attached != which) return;
            attached?.Dispose();
            attached = null;
        }
    }

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

    private void Listen()
    {
        var portFile = Path.Combine(dataDir, "ws.port");
        var until = Environment.TickCount64 + 20_000;
        while (!File.Exists(portFile))
        {
            if (soloist is not { HasExited: false } || stop.IsCancellationRequested) return;
            if (Environment.TickCount64 > until) throw new InvalidOperationException("Soloist didn't open its WebSocket" + (soloistError != "" ? ": " + soloistError : ""));
            Wait(100);
        }
        Wait(100);
        var port = File.ReadAllText(portFile).Trim();
        var addrFile = Path.Combine(dataDir, "ws.addr");
        var addr = File.Exists(addrFile) ? File.ReadAllText(addrFile).Trim() : "127.0.0.1";
        if (addr is "" or "0.0.0.0") addr = "127.0.0.1";
        if (addr.Contains(':') && !addr.StartsWith('[')) addr = $"[{addr}]";
        var w = new ClientWebSocket();
        lock (gate) ws = w;
        w.ConnectAsync(new Uri($"ws://{addr}:{port}"), stop.Token).Wait(TimeSpan.FromSeconds(10));
        if (w.State != WebSocketState.Open) throw new InvalidOperationException("couldn't reach Soloist's WebSocket");
        SetStatus("ready · choose it in Spotify");
        lastError = "";

        var buf = new byte[1 << 16];
        using var msg = new MemoryStream();
        while (!stop.IsCancellationRequested && w.State == WebSocketState.Open)
        {
            WebSocketReceiveResult got;
            try { got = w.ReceiveAsync(buf, stop.Token).GetAwaiter().GetResult(); }
            catch (Exception) { return; }
            if (got.MessageType == WebSocketMessageType.Close) return;
            msg.Write(buf, 0, got.Count);
            if (!got.EndOfMessage) continue;
            try
            {
                using var doc = JsonDocument.Parse(msg.ToArray());
                OnMessage(doc.RootElement);
            }
            catch (JsonException) { /* not JSON: skip it */ }
            catch (Exception e) { if (!stop.IsCancellationRequested) Log("event: " + e.Message); }
            msg.SetLength(0);
        }
    }

    private async Task Command(string json)
    {
        ClientWebSocket? w;
        lock (gate) w = ws;
        if (w is not { State: WebSocketState.Open }) return;
        await sendGate.WaitAsync();
        try { await w.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, stop.Token); }
        catch (Exception) { /* closing */ }
        finally { sendGate.Release(); }
    }

    private void OnMessage(JsonElement m)
    {
        switch (Str(m, "type"))
        {
            case "auth_state":
                if (!Bool(m, "logged_in")) SetStatus("ready · choose it in Spotify");
                else if (!Bool(m, "is_active") && !Ours()) SetStatus("connected · choose it in Spotify to play here");
                break;
            case "playback_state":
                if (m.TryGetProperty("item", out var item)) SetItem(item);
                if (m.TryGetProperty("position", out var pos)) SetPosition(pos);
                KeepVolume(m);
                Apply(Str(m, "status"));
                break;
            case "track_changed":
                if (m.TryGetProperty("item", out var it)) SetItem(it);
                break;
            case "playback_changed":
                Apply(Str(m, "status"));
                break;
            case "position_sync":
                if (m.TryGetProperty("position", out var p)) SetPosition(p);
                break;
            case "volume_changed":
                KeepVolume(m);
                break;
            case "error":
                Log("Soloist: " + Str(m, "message"));
                break;
        }
    }

    private void Apply(string status)
    {
        var r = bridge.Renderer;
        switch (status)
        {
            case "playing":
                Play();
                break;
            case "paused":
                if (Ours() && r.IsActive) Try(r.Pause);
                if (Ours()) SetStatus("paused");
                break;
            case "idle":
                if (Ours() && (r.IsActive || r.Transport == "PAUSED_PLAYBACK")) Try(r.Stop);
                if (Ours() || Status is "playing" or "paused") SetStatus("ready · choose it in Spotify");
                break;
        }
    }

    // Bit-perfect: Spotify's volume stays at 100% (use the DAC's or the amplifier's).
    private void KeepVolume(JsonElement m)
    {
        if (m.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 100)
            _ = Command("{\"type\":\"command\",\"command\":\"set_volume\",\"volume\":100}");
    }

    private void SetItem(JsonElement item)
    {
        var e = Entity(item);
        if (e == null) return;
        lock (gate) meta = e with { Position = meta.Position, At = meta.At };
        Publish();
    }

    private void SetPosition(JsonElement p)
    {
        double ms = p.TryGetProperty("position_ms", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0;
        long at = p.TryGetProperty("timestamp_ms", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : Arbiter.Now;
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
        Sources.UnregisterLive(liveName);
        runner.Join(TimeSpan.FromSeconds(8));
        if (Ours()) { try { bridge.Renderer.Stop().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* closing */ } }
    }
}
