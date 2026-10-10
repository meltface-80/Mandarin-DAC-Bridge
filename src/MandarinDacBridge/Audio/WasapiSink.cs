// WasapiSink.cs — a USB DAC on Windows, held through WASAPI exclusive mode.
//
// An exclusive-mode stream keeps every other program off the DAC (shared-
// mode apps and the Windows mixer included) for as long as it is open,
// playing or not. It is opened in the DAC's own integer format at each
// track's rate — 32-bit, 24 in 32, packed 24 or 16, the first the DAC takes
// — so nothing is converted or resampled on the way (Windows' mixer is not
// in the path at all).
//
// WASAPI's event mode asks for one buffer at a time; a thread at "Pro Audio"
// priority answers each request from a ring the renderer fills, and with
// silence when the ring is empty (never with stale samples). The position is
// the DAC's own clock (IAudioClock).
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using MandarinDacBridge.Native;

namespace MandarinDacBridge.Audio;

internal sealed class WasapiSink(string endpointId) : ISink
{
    private static readonly (int Container, int Valid)[] Candidates = [(32, 32), (32, 24), (24, 24), (16, 16)];

    private readonly object gate = new();
    private readonly AutoResetEvent bufferReady = new(false);
    private IAudioClient? client;
    private IAudioRenderClient? render;
    private IAudioClock? clock;
    private Thread? renderThread;
    private volatile bool renderStop;
    private Thread? opener;

    private int rate = 44100, srcChannels = 2, devChannels = 2, container, valid, frameBytes;
    private uint bufferFrames;
    private ulong clockFreq = 1;
    private byte[] ring = [];
    private long ringWrite, ringRead;          // bytes, ever increasing
    private long deliveredReal, silence;       // frames handed to the DAC: samples, and silence while running
    private long written;                      // frames written since the clock last started at 0
    private bool running, paused, configured, shared, started, disposed, gone;
    private byte[] scratch = new byte[1 << 16];

    public bool Exclusive { get; private set; }
    public string Message { get; private set; } = "starting";
    public int HolderPid { get; private set; }
    public event Action? StatusChanged;
    public event Action? Gone;

    // ------------------------------------------------------------ holding the DAC

    public void Start()
    {
        lock (gate)
        {
            started = true;
            if (shared) { Message = Sharing.Idle; return; }
        }
        StartHolding();
    }

    private void StartHolding()
    {
        if (opener is { IsAlive: true }) return;
        opener = new Thread(Hold) { IsBackground = true, Name = "wasapi-open" };
        opener.Start();
    }

    // Opens the DAC (exclusive), and keeps trying every 2 s while another program has it.
    private void Hold()
    {
        Wasapi.Init();
        string last = "";
        int failures = 0;
        while (!disposed && !shared)
        {
            try
            {
                lock (gate)
                {
                    if (disposed || shared || client != null) return;
                    Open(rate, srcChannels);
                    Exclusive = true;
                    Message = "";
                    HolderPid = Environment.ProcessId;
                }
                StatusChanged?.Invoke();
                return;
            }
            catch (Exception e)
            {
                var why = e.Message;
                if (why.Contains("is gone") || e is COMException { HResult: Wasapi.DeviceInvalidated } || ++failures > 30 && why.Contains("not found"))
                {
                    Lost();
                    return;
                }
                if (why != last)
                {
                    last = why;
                    Message = why;
                    HolderPid = 0;
                    StatusChanged?.Invoke();
                }
            }
            Thread.Sleep(2000);
        }
    }

    // A client in exclusive mode at this rate, in the DAC's best integer format. With gate held.
    private void Open(int r, int ch)
    {
        Release();
        var enumerator = Wasapi.Enumerator();
        try
        {
            if (enumerator.GetDevice(endpointId, out var device) != 0 || device == null) throw new DeviceException("the DAC was not found");
            var c = Wasapi.Activate(device);
            int useCh = ch;
            var fmt = Pick(c, r, ref useCh) ?? throw new DeviceException($"the DAC doesn't take {Devices.KHz(r)} in exclusive mode");
            Wasapi.Check(c.GetDevicePeriod(out var period, out _), "the DAC's period");
            if (period <= 0) period = 100_000;
            int hr = Init(c, fmt, period);
            if (hr == Wasapi.BufferSizeNotAligned)
            {
                // The DAC wants a buffer of whole blocks: ask again with the size it gave.
                c.GetBufferSize(out var aligned);
                period = (long)(10_000_000.0 * aligned / r + 0.5);
                Free(c);
                c = Wasapi.Activate(device);
                hr = Init(c, fmt, period);
            }
            if (hr < 0) { Free(c); Wasapi.Check(hr, "exclusive mode"); }
            Wasapi.Check(c.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()), "the DAC's event");
            Wasapi.Check(c.GetBufferSize(out bufferFrames), "the DAC's buffer");
            client = c;
            render = Wasapi.Service<IAudioRenderClient>(c, Wasapi.IidRenderClient);
            clock = Wasapi.Service<IAudioClock>(c, Wasapi.IidAudioClock);
            clock.GetFrequency(out clockFreq);
            if (clockFreq == 0) clockFreq = 1;
            rate = r;
            srcChannels = ch;
            devChannels = useCh;
            container = fmt.BitsPerSample;
            valid = fmt.ValidBitsPerSample;
            frameBytes = devChannels * container / 8;
            ring = new byte[Math.Max(rate / 2, (int)bufferFrames * 4) * frameBytes];
            ResetCounters();
            Prefill();
            configured = true;
            renderStop = false;
            renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "wasapi-render", Priority = ThreadPriority.Highest };
            renderThread.Start();
        }
        finally { Free(enumerator); }
    }

    private static unsafe int Init(IAudioClient c, WaveFormatExtensible fmt, long period) =>
        c.Initialize(Wasapi.ShareExclusive, Wasapi.FlagEventCallback, period, period, (nint)(&fmt), 0);

    // The first format the DAC takes at this rate: 32-bit, 24 in 32, packed 24, 16; at these channels, else stereo.
    private static unsafe WaveFormatExtensible? Pick(IAudioClient c, int r, ref int ch)
    {
        foreach (var channels in ch == 2 ? new[] { 2 } : new[] { ch, 2 })
            foreach (var (cont, val) in Candidates)
            {
                var f = WaveFormatExtensible.Of(r, channels, cont, val);
                if (c.IsFormatSupported(Wasapi.ShareExclusive, (nint)(&f), 0) == 0) { ch = channels; return f; }
            }
        return null;
    }

    private static void Free(object? com)
    {
        if (com is ComObject o) { try { o.FinalRelease(); } catch (Exception) { /* gone */ } }
    }

    // Lets go of the DAC: the render thread stops, the stream closes. With gate held (it is let go meanwhile).
    private void Release()
    {
        var t = renderThread;
        renderStop = true;
        bufferReady.Set();
        if (t != null && t != Thread.CurrentThread)
        {
            Monitor.Exit(gate);
            try { t.Join(2000); } finally { Monitor.Enter(gate); }
        }
        renderThread = null;
        try { client?.Stop(); } catch (Exception) { /* closing */ }
        Free(render);
        Free(clock);
        Free(client);
        render = null;
        clock = null;
        client = null;
        configured = false;
        running = false;
    }

    private void Lost()
    {
        lock (gate)
        {
            if (gone) return;
            gone = true;
        }
        ThreadPool.QueueUserWorkItem(_ => Gone?.Invoke());
    }

    // ------------------------------------------------------------ the stream

    private void ResetCounters()
    {
        ringWrite = ringRead = 0;
        deliveredReal = silence = written = 0;
        running = false;
    }

    // One buffer of silence before the stream starts (exclusive event mode wants it full at Start).
    private void Prefill()
    {
        if (render == null) return;
        if (render.GetBuffer(bufferFrames, out _) == 0)
        {
            render.ReleaseBuffer(bufferFrames, Wasapi.BufferSilent);
            silence += bufferFrames;
        }
    }

    private unsafe void RenderLoop()
    {
        Wasapi.Init();
        Wasapi.ProAudioThread();
        while (!renderStop)
        {
            bufferReady.WaitOne(500);
            lock (gate)
            {
                if (renderStop || render == null || !running) continue;
                int hr = render.GetBuffer(bufferFrames, out var p);
                if (hr < 0)
                {
                    if (hr == Wasapi.DeviceInvalidated) Lost();
                    continue;
                }
                int want = (int)bufferFrames * frameBytes;
                int have = (int)Math.Min(want, ringWrite - ringRead);
                have -= have % frameBytes;
                var dst = new Span<byte>((void*)p, want);
                int at = (int)(ringRead % ring.Length);
                int first = Math.Min(have, ring.Length - at);
                ring.AsSpan(at, first).CopyTo(dst);
                ring.AsSpan(0, have - first).CopyTo(dst[first..]);
                dst[have..].Clear();
                ringRead += have;
                render.ReleaseBuffer(bufferFrames, have == 0 ? Wasapi.BufferSilent : 0);
                deliveredReal += have / frameBytes;
                silence += (want - have) / frameBytes;
                Monitor.PulseAll(gate);
            }
        }
    }

    public string Configure(int r, int ch)
    {
        Wasapi.Init();
        lock (gate)
        {
            if (shared && client == null) throw new DeviceException(Message);
            if (!configured || client == null || r != rate || ch != srcChannels)
            {
                try { Open(r, ch); }
                catch (Exception)
                {
                    Exclusive = false;
                    ThreadPool.QueueUserWorkItem(_ => { StatusChanged?.Invoke(); if (!shared) StartHolding(); });
                    throw;
                }
            }
            else FlushLocked();
            return $"{(container == valid ? $"{valid}-bit" : $"{valid}-bit in {container}")} integer, {devChannels} ch";
        }
    }

    public void Write(ReadOnlySpan<byte> pcm, CancellationToken ct)
    {
        int frames = pcm.Length / (4 * srcChannels);
        int need = frames * frameBytes;
        if (scratch.Length < need) scratch = new byte[need];
        Convert(pcm, frames);
        int off = 0;
        lock (gate)
        {
            while (off < need)
            {
                ct.ThrowIfCancellationRequested();
                if (!configured || client == null) throw new DeviceException("the DAC isn't set up");
                long space = ring.Length - (ringWrite - ringRead);
                if (space < frameBytes)
                {
                    StartIfReady();
                    Monitor.Wait(gate, 50);
                    continue;
                }
                int n = (int)Math.Min(space, need - off);
                n -= n % frameBytes;
                int at = (int)(ringWrite % ring.Length);
                int first = Math.Min(n, ring.Length - at);
                scratch.AsSpan(off, first).CopyTo(ring.AsSpan(at));
                scratch.AsSpan(off + first, n - first).CopyTo(ring);
                ringWrite += n;
                off += n;
                written += n / frameBytes;
            }
            StartIfReady();
        }
    }

    // The stream runs once two buffers are waiting (and it isn't paused). With gate held.
    private void StartIfReady()
    {
        if (running || paused || client == null) return;
        if (ringWrite - ringRead < Math.Min(ring.Length, 2L * bufferFrames * frameBytes)) return;
        if (client.Start() >= 0) running = true;
    }

    // 32-bit samples → the DAC's container and channel count, into scratch.
    private void Convert(ReadOnlySpan<byte> src, int frames)
    {
        var o = scratch.AsSpan();
        int w = 0;
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < devChannels; c++)
            {
                int v = c < srcChannels ? BitConverter.ToInt32(src.Slice((f * srcChannels + c) * 4, 4)) : 0;
                switch (container)
                {
                    case 32: BitConverter.TryWriteBytes(o.Slice(w, 4), valid == 24 ? v & ~0xFF : v); w += 4; break;
                    case 24: { int x = v >> 8; o[w] = (byte)x; o[w + 1] = (byte)(x >> 8); o[w + 2] = (byte)(x >> 16); w += 3; break; }
                    default: BitConverter.TryWriteBytes(o.Slice(w, 2), (short)(v >> 16)); w += 2; break;
                }
            }
    }

    public void Drain(CancellationToken ct)
    {
        Wasapi.Init();
        var until = DateTime.UtcNow.AddSeconds(5);
        lock (gate)
        {
            StartIfReadyForce();
            while (client != null && running && (ringWrite > ringRead || PlayedLocked() < deliveredReal) && DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                Monitor.Wait(gate, 10);
            }
            FlushLocked();
        }
    }

    // At the end of a track there may be less than two buffers waiting: play them anyway.
    private void StartIfReadyForce()
    {
        if (running || paused || client == null || ringWrite == ringRead) return;
        if (client.Start() >= 0) running = true;
    }

    public void Flush()
    {
        Wasapi.Init();
        lock (gate) FlushLocked();
    }

    private void FlushLocked()
    {
        if (client != null)
        {
            client.Stop();
            client.Reset();
        }
        ResetCounters();
        paused = false;
        Prefill();
        Monitor.PulseAll(gate);
    }

    public void Pause(bool on)
    {
        Wasapi.Init();
        lock (gate)
        {
            paused = on;
            if (on && running && client != null) { client.Stop(); running = false; }
            if (!on) StartIfReadyForce();
        }
    }

    public long Played { get { Wasapi.Init(); lock (gate) return PlayedLocked(); } }

    private long PlayedLocked()
    {
        if (clock == null || !configured) return 0;
        if (clock.GetPosition(out var pos, out _) < 0) return 0;
        long frames = (long)(pos * (double)rate / clockFreq);
        return Math.Clamp(frames - silence, 0, deliveredReal);
    }

    // ------------------------------------------------------------ sharing

    public void Share(bool on)
    {
        Wasapi.Init();
        lock (gate)
        {
            if (disposed) return;
            shared = on;
            if (on)
            {
                Release();
                Exclusive = false;
                HolderPid = 0;
                Message = Sharing.Idle;
            }
        }
        if (!on && started) StartHolding();
        StatusChanged?.Invoke();
    }

    public bool Reclaim()
    {
        Wasapi.Init();
        lock (gate)
        {
            if (disposed) return false;
            if (client != null) return true;
            try { Open(rate, srcChannels); }
            catch (Exception e)
            {
                Message = e.Message;
                return false;
            }
            Exclusive = true;
            Message = "";
            HolderPid = Environment.ProcessId;
        }
        StatusChanged?.Invoke();
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Release();
            Exclusive = false;
            Monitor.PulseAll(gate);
        }
    }
}
