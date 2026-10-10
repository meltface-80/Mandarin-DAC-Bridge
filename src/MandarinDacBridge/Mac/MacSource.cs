// MacSource.cs — the Music app and the Spotify app on this Mac, played
// through the bridge to a USB DAC (macOS; off unless switched on).
//
// Neither app can play to a DAC the bridge holds, nor switches the DAC's
// rate per track. So, as Arco does for Roon: the "DAC Bridge" virtual output
// (tools/mac/driver, adapted from Arco's driver) becomes the Mac's output
// while this is on; the app plays to it; the bridge reads the same samples
// back from its input and plays them to the chosen DAC, through the same
// exclusive path and arbiter as everything else.
//
//   Music / Spotify ──▶ "DAC Bridge" output ══ loopback ══▶ its input ──▶ "live:mac" ──▶ renderer ──▶ DAC
//
// Bit-perfect, three ways:
//   - the device runs at the track's own rate: the Music app names it
//     (AppleScript: sample rate of current track), the bridge sets the
//     device to it, pausing the app around the change and taking it back to
//     the start of a track that has only just begun; Spotify is 44.1 kHz;
//   - samples come back as float, which carries 24 bits exactly; ×2^23 gives
//     the integers back;
//   - the device's clock follows the DAC's: the driver takes a rate scalar,
//     which the bridge steers from how full its buffer for the DAC runs.
//     The app is paced by the DAC, as a file read from disk would be.
//
// Reading the device's input is "microphone" access to macOS: the first time,
// macOS asks whether mandarin-dac-bridge may (the bridge never listens to a
// microphone).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MandarinDacBridge.Native;
using MandarinDacBridge.Spotify;

namespace MandarinDacBridge.Mac;

internal sealed unsafe class MacSource : IDisposable
{
    public const string LoopbackUid = "app.mandarin.dacbridge.loopback";
    private const int ChunkFrames = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct Shared
    {
        public int* Ring;          // stereo, 24-bit samples in 32 (low byte 0)
        public long Capacity;      // frames
        public long WritePos, ReadPos;
        public long Callbacks, SoundCallbacks;
    }

    private readonly Func<Bridge?> target;
    private readonly string dataDir;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread control, reader;
    private readonly object gate = new();
    private readonly Shared* sh;
    private readonly Queue<byte[]> preroll = new();
    private readonly Dictionary<string, int> rateMemory = new();
    private uint dev;
    private IntPtr procId;
    private bool capturing;
    private LiveSource? live;
    private Bridge? playingOn;
    private Track? track;
    private string trackId = "", app = "";
    private int rate;
    private double scalar = 1, integral;
    private long lastSound, quietSince;
    private bool starting;

    public string Status { get; private set; } = "starting";
    public string ArtFile { get; private set; } = "";
    public string ArtUrl { get; private set; } = "";

    public MacSource(string dataDir, Func<Bridge?> target)
    {
        this.dataDir = dataDir;
        this.target = target;
        sh = (Shared*)NativeMemory.AllocZeroed((nuint)sizeof(Shared));
        sh->Capacity = 768000;     // 4 s at 192 kHz, 1 s at 768 kHz
        sh->Ring = (int*)NativeMemory.AllocZeroed((nuint)(sh->Capacity * 2 * sizeof(int)));
        control = new Thread(Control) { IsBackground = true, Name = "mac-source" };
        reader = new Thread(Read) { IsBackground = true, Name = "mac-capture", Priority = ThreadPriority.AboveNormal };
        control.Start();
        reader.Start();
    }

    private static void Log(string m) => MandarinDacBridge.Log.Write("This Mac: " + m);

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        playingOn?.Notify();
    }

    // ------------------------------------------------------------ the loopback's input

    [UnmanagedCallersOnly]
    private static int Capture(uint device, void* now, AudioBufferList* input, void* inputTime, AudioBufferList* output, void* outputTime, void* client)
    {
        var s = (Shared*)client;
        s->Callbacks++;
        if (input == null || input->NumberBuffers == 0) return 0;
        var b = &input->First;
        int ch = (int)b->NumberChannels;
        if (b->Data == null || ch == 0) return 0;
        long frames = b->DataByteSize / (sizeof(float) * ch);
        var src = (float*)b->Data;
        long w = s->WritePos;
        long space = s->Capacity - (w - Volatile.Read(ref s->ReadPos));
        long n = Math.Min(frames, space);
        bool sound = false;
        for (long f = 0; f < n; f++)
        {
            int* d = s->Ring + (w + f) % s->Capacity * 2;
            float l = src[f * ch], r = ch > 1 ? src[f * ch + 1] : l;
            d[0] = ToInt(l);
            d[1] = ToInt(r);
            sound |= (d[0] | d[1]) != 0;
        }
        Volatile.Write(ref s->WritePos, w + n);
        if (sound) s->SoundCallbacks++;
        return 0;
    }

    // Core Audio makes floats of 24-bit samples by dividing by 2^23: multiplying gives them back exactly.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ToInt(float x)
    {
        double v = Math.Round(x * 8388608.0);
        if (v > 8388607) v = 8388607;
        if (v < -8388608) v = -8388608;
        return (int)v << 8;
    }

    private bool StartCapture()
    {
        if (capturing) return true;
        int err = CoreAudio.CreateIOProcId(dev, &Capture, sh, out procId);
        if (err != 0 || procId == IntPtr.Zero) { SetStatus($"couldn't read the DAC Bridge output ({err})"); return false; }
        Volatile.Write(ref sh->ReadPos, Volatile.Read(ref sh->WritePos));
        err = CoreAudio.DeviceStart(dev, procId);
        if (err != 0)
        {
            CoreAudio.DestroyIOProcId(dev, procId);
            procId = IntPtr.Zero;
            SetStatus($"couldn't read the DAC Bridge output ({err}): allow Microphone access for mandarin-dac-bridge");
            return false;
        }
        capturing = true;
        return true;
    }

    private void StopCapture()
    {
        if (!capturing) return;
        CoreAudio.DeviceStop(dev, procId);
        CoreAudio.DestroyIOProcId(dev, procId);
        procId = IntPtr.Zero;
        capturing = false;
    }

    // Every 10 ms: what came in, in chunks, to the renderer (or kept a moment, for the start of playback).
    private void Read()
    {
        var chunk = new byte[ChunkFrames * 8];
        int have = 0;
        while (!stop.IsCancellationRequested)
        {
            stop.Token.WaitHandle.WaitOne(10);
            long r = Volatile.Read(ref sh->ReadPos), w = Volatile.Read(ref sh->WritePos);
            while (r < w)
            {
                int n = (int)Math.Min(w - r, ChunkFrames - have / 8);
                for (int f = 0; f < n; f++)
                {
                    int* s = sh->Ring + (r + f) % sh->Capacity * 2;
                    BitConverter.TryWriteBytes(chunk.AsSpan(have + f * 8), s[0]);
                    BitConverter.TryWriteBytes(chunk.AsSpan(have + f * 8 + 4), s[1]);
                }
                have += n * 8;
                r += n;
                Volatile.Write(ref sh->ReadPos, r);
                if (have < chunk.Length) continue;
                var full = chunk;
                chunk = new byte[ChunkFrames * 8];
                have = 0;
                LiveSource? l;
                lock (gate) l = live;
                if (l != null && !l.Closed)
                {
                    if (!l.TryPush(full)) Log("the DAC fell behind: a moment of sound was dropped");
                }
                else
                    lock (gate)
                    {
                        preroll.Enqueue(full);
                        while (preroll.Count > 6) preroll.Dequeue();
                    }
            }
        }
    }

    // ------------------------------------------------------------ following the apps

    private void Control()
    {
        int tick = 0;
        while (!stop.IsCancellationRequested)
        {
            try { Step(tick++ % 4 == 0); }
            catch (Exception e) { if (!stop.IsCancellationRequested) { Log(e.Message); SetStatus(e.Message); } }
            stop.Token.WaitHandle.WaitOne(250);
        }
    }

    private void Step(bool poll)
    {
        if (!OperatingSystem.IsMacOS()) { SetStatus("for macOS only"); return; }
        var d = CoreAudio.FindDevice(LoopbackUid);
        if (d == CoreAudio.Unknown)
        {
            if (capturing) StopCapture();
            dev = d;
            SetStatus("needs the DAC Bridge output: install it (see the page's link), then this starts by itself");
            return;
        }
        if (d != dev) { if (capturing) StopCapture(); dev = d; MakeDefault(); }
        if (!StartCapture()) return;
        Servo();
        if (!poll) return;

        var b = target();
        if (b == null) { SetStatus("no DAC to play to"); return; }
        var now = MacPlayers.Now(MacPlayers.Music) is { State: "playing" } m ? m
            : MacPlayers.Now(MacPlayers.Spotify) is { State: "playing" } sp ? sp
            : app != "" ? MacPlayers.Now(app) : null;
        Follow(b, now);
    }

    private void Follow(Bridge b, MacTrack? now)
    {
        var r = b.Renderer;
        bool ours = track != null && r.Current == track && playingOn == b;
        if (now == null || now.State == "stopped")
        {
            if (ours && (r.IsActive || r.Transport == "PAUSED_PLAYBACK")) Try(r.Stop);
            Detach();
            app = "";
            SetStatus("ready · play in the Music app or the Spotify app");
            return;
        }
        if (now.App != app) { app = now.App; trackId = ""; }

        // A new track: its own rate (Music), or 44.1 kHz (Spotify).
        if (now.Id != trackId)
        {
            trackId = now.Id;
            ArtFile = "";
            ArtUrl = "";
            int want = now.App == MacPlayers.Spotify ? 44100
                : MacPlayers.ReadMusicRate(() => MacPlayers.Now(MacPlayers.Music)?.Id == now.Id, rateMemory.GetValueOrDefault(now.Id), stop.Token);
            if (want <= 0) want = 44100;
            rateMemory[now.Id] = want;
            want = Sources.PickRate(want, b.Dev.Rates.Length > 0 ? b.Dev.Rates : Devices.StandardRates);
            if ((int)CoreAudio.NominalRate(dev) != want) ChangeRate(want, now);
            var cover = MacPlayers.Cover(now.App, Path.Combine(dataDir, "mac-cover"));
            if (cover != null && cover.StartsWith("http")) ArtUrl = cover; else if (cover != null) ArtFile = cover;
        }
        rate = (int)CoreAudio.NominalRate(dev);

        if (now.State == "paused")
        {
            if (ours && r.IsActive) Try(r.Pause);
            SetStatus($"paused · {now.App}");
        }
        else if (ours && r.Transport == "PAUSED_PLAYBACK")
        {
            if (b.Arbiter.Claim(Caller(now.App), "Play", r.IsActive) is null) Try(r.Play);
            else Refuse(b, now.App);
        }
        else if (!ours || !r.IsActive || live?.Info.Rate != rate) Start(b, now.App);
        else SetStatus($"playing · {now.App} → {b.FriendlyName()}");

        var art = ArtUrl != "" ? ArtUrl : ArtFile != "" ? $"/api/dacs/{b.Id}/art?k={Uri.EscapeDataString(trackId)}" : "";
        var meta = new LiveMeta(now.Title, now.Artist, now.Album, art, now.Duration, now.Position, Arbiter.Now);
        if (track != null && r.Current == track) r.SetLive(track, meta);
    }

    private static Caller Caller(string app) => new("mac|" + app, app == MacPlayers.Music ? "Apple Music" : "Spotify (Mac)", "127.0.0.1");

    // The device to the track's rate: the app paused around it, back to the start if the track only just began.
    private void ChangeRate(int want, MacTrack now)
    {
        bool wasPlaying = now.State == "playing";
        if (wasPlaying) MacPlayers.Pause(now.App);
        CoreAudio.SetRate(dev, want);
        for (int i = 0; i < 20 && (int)CoreAudio.NominalRate(dev) != want; i++) stop.Token.WaitHandle.WaitOne(50);
        Volatile.Write(ref sh->ReadPos, Volatile.Read(ref sh->WritePos));
        ResetServo();
        if (playingOn != null && track != null && playingOn.Renderer.Current == track) Try(playingOn.Renderer.Stop);
        Detach();
        if (now.Position < 3) MacPlayers.ToStart(now.App);
        if (wasPlaying) MacPlayers.Play(now.App);
        Log($"{now.App}: {Devices.KHz(want)} for \"{now.Title}\"");
    }

    private void Start(Bridge b, string appName)
    {
        lock (gate) if (starting) return;
        var r = b.Renderer;
        if (b.Arbiter.Claim(Caller(appName), "Play", r.IsActive) is not null) { Refuse(b, appName); return; }
        MakeDefault();
        var s = new LiveSource(new TrackInfo(rate, 2, 24, appName == MacPlayers.Music ? "Apple Music" : "Spotify", rate, false, 0, false),
            capacity: Math.Max(8, rate * 2 / ChunkFrames));
        lock (gate)
        {
            bool sound = false;
            foreach (var c in preroll) { sound |= c.AsSpan().IndexOfAnyExcept((byte)0) >= 0; if (sound) s.TryPush(c); }
            preroll.Clear();
            live?.Dispose();
            live = s;
            playingOn = b;
            starting = true;
        }
        Sources.RegisterLive("mac", _ => s);
        ResetServo();
        quietSince = Environment.TickCount64;
        lastSound = Volatile.Read(ref sh->SoundCallbacks);
        _ = Task.Run(() =>
        {
            try
            {
                var t = r.SetUri("live:mac", "").GetAwaiter().GetResult();
                lock (gate) track = t;
                r.Play().GetAwaiter().GetResult();
                SetStatus($"playing · {appName} → {b.FriendlyName()}");
                Log($"{appName} → {b.FriendlyName()} at {Devices.KHz(rate)}");
            }
            catch (Exception e) { Log("couldn't play: " + e.Message); Detach(); }
            finally { lock (gate) starting = false; }
        });
    }

    private void Refuse(Bridge b, string appName)
    {
        MacPlayers.Pause(appName);
        SetStatus($"kept out · {b.Arbiter.Owner?.Name} is using {b.FriendlyName()}");
        Log($"{appName} paused: {b.Arbiter.Owner?.Name} is using the DAC");
        b.Notify();
    }

    private void Detach()
    {
        lock (gate)
        {
            live?.Dispose();
            live = null;
        }
    }

    // The Mac's output is the DAC Bridge device while this is on (the one before is remembered, to give back).
    private void MakeDefault()
    {
        var cur = CoreAudio.DefaultOutput();
        if (cur == dev || dev == CoreAudio.Unknown) return;
        var uid = CoreAudio.GetString(cur, CoreAudio.DeviceUid);
        if (uid != "" && uid != LoopbackUid) File.WriteAllText(Path.Combine(dataDir, "mac-previous-output"), uid);
        CoreAudio.SetDefaultOutput(dev);
        Log("the Mac's sound output is now DAC Bridge");
    }

    private void GiveBackDefault()
    {
        try
        {
            if (dev == CoreAudio.Unknown || CoreAudio.DefaultOutput() != dev) return;
            var file = Path.Combine(dataDir, "mac-previous-output");
            var prev = File.Exists(file) ? CoreAudio.FindDevice(File.ReadAllText(file).Trim()) : CoreAudio.Unknown;
            if (prev != CoreAudio.Unknown) { CoreAudio.SetDefaultOutput(prev); Log("the Mac's sound output is back"); }
        }
        catch (Exception) { /* best effort */ }
    }

    // ------------------------------------------------------------ the clock follows the DAC

    private void ResetServo()
    {
        integral = 0;
        if (scalar != 1 && dev != CoreAudio.Unknown) CoreAudio.SetScalar(dev, 1);
        scalar = 1;
    }

    // Four times a second: the buffer for the DAC is kept near half a second by nudging the device's clock (±0.1%).
    private void Servo()
    {
        LiveSource? l;
        lock (gate) l = live;
        if (l == null || l.Closed || rate <= 0 || playingOn?.Renderer.Transport != "PLAYING") return;
        // Nothing heard for a while though the app plays: macOS is likely refusing the input.
        long sound = Volatile.Read(ref sh->SoundCallbacks);
        if (sound != lastSound) { lastSound = sound; quietSince = Environment.TickCount64; }
        else if (Environment.TickCount64 - quietSince > 8000)
            SetStatus("no sound comes back from DAC Bridge: allow Microphone access for mandarin-dac-bridge (System Settings → Privacy & Security → Microphone)");
        double fill = (double)l.Count * ChunkFrames / rate;
        double err = fill - 0.5;
        integral = Math.Clamp(integral + err * 0.25, -20, 20);
        double want = Math.Clamp(1 - (0.0005 * err + 0.00005 * integral), 0.999, 1.001);
        if (Math.Abs(want - scalar) < 0.000002) return;
        if (CoreAudio.SetScalar(dev, want) == 0) scalar = want;
    }

    private static void Try(Func<Task> action)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (Exception e) { Log(e.Message); }
    }

    public void Dispose()
    {
        stop.Cancel();
        control.Join(TimeSpan.FromSeconds(5));
        reader.Join(TimeSpan.FromSeconds(2));
        if (playingOn != null && track != null && playingOn.Renderer.Current == track) Try(playingOn.Renderer.Stop);
        Detach();
        Sources.UnregisterLive("mac");
        if (OperatingSystem.IsMacOS())
        {
            StopCapture();
            ResetServo();
            GiveBackDefault();
        }
        NativeMemory.Free(sh->Ring);
        NativeMemory.Free(sh);
    }
}
