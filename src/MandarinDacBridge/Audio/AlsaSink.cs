// AlsaSink.cs — a USB DAC on Linux, held through ALSA's hw device.
//
// An ALSA hw device can be open in one program at a time, so holding it
// open (even while nothing plays) keeps every other program off it. hw does
// no conversion: the DAC is set to each track's rate, and the samples reach
// it unchanged — a 24-bit DAC gets the top 24 bits of the 32 sent, which is
// all a 24-bit source has.
using MandarinDacBridge.Native;

namespace MandarinDacBridge.Audio;

internal sealed class AlsaSink(string device) : ISink
{
    private static readonly int[] Candidates = [Alsa.FormatS32Le, Alsa.FormatS24Le, Alsa.FormatS24_3Le, Alsa.FormatS16Le];

    private readonly object gate = new();
    private readonly ManualResetEventSlim running = new(true);   // reset while paused
    private IntPtr pcm;
    private int format, sampleBytes, devChannels, srcChannels, rate;
    private bool configured, canPause, hwPaused, disposed, shared, started;
    private long written;
    private byte[] scratch = new byte[1 << 16];
    private Thread? opener;

    public bool Exclusive { get; private set; }
    public string Message { get; private set; } = "starting";
    public int HolderPid { get; private set; }
    public event Action? StatusChanged;
    public event Action? Gone;

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
        opener = new Thread(Hold) { IsBackground = true, Name = "alsa-open " + device };
        opener.Start();
    }

    // Opens the DAC, and keeps trying every 2 s while another program has it.
    private void Hold()
    {
        int failures = 0;
        string last = "";
        while (!disposed && !shared)
        {
            int err;
            lock (gate)
            {
                if (disposed || shared) return;
                if (pcm != IntPtr.Zero) return;
                err = Alsa.Open(out pcm, device, Alsa.StreamPlayback, Alsa.NonBlock);
                if (err >= 0)
                {
                    Exclusive = true;
                    Message = "";
                    HolderPid = Environment.ProcessId;
                }
                else pcm = IntPtr.Zero;
            }
            if (err >= 0) { StatusChanged?.Invoke(); return; }
            if (err != Alsa.EBUSY && ++failures >= 5) { Gone?.Invoke(); return; }
            var why = err == Alsa.EBUSY ? "another program is using the DAC" : Alsa.StrError(err);
            if (why != last)
            {
                last = why;
                Message = why;
                HolderPid = 0;
                StatusChanged?.Invoke();
            }
            Thread.Sleep(2000);
        }
    }

    public string Configure(int r, int ch)
    {
        lock (gate)
        {
            if (pcm == IntPtr.Zero) throw new DeviceException(Message == "" ? "the DAC isn't open" : Message);
            configured = false;
            Alsa.Drop(pcm);
            Alsa.HwFree(pcm);
            Alsa.HwParamsMalloc(out var hw);
            try
            {
                int err;
                if ((err = Alsa.HwParamsAny(pcm, hw)) < 0) throw Fail(err, "the DAC's settings can't be read");
                Alsa.HwSetRateResample(pcm, hw, 0);
                if ((err = Alsa.HwSetAccess(pcm, hw, Alsa.AccessRwInterleaved)) < 0) throw Fail(err, "interleaved access");
                format = Candidates.FirstOrDefault(f => Alsa.HwTestFormat(pcm, hw, f) == 0, -1);
                if (format < 0) throw new DeviceException("the DAC takes none of S32_LE, S24_LE, S24_3LE, S16_LE");
                Alsa.HwSetFormat(pcm, hw, format);
                sampleBytes = Alsa.FormatPhysicalWidth(format) / 8;
                Alsa.HwGetChannelsMin(hw, out var cmin);
                Alsa.HwGetChannelsMax(hw, out var cmax);
                devChannels = (int)Math.Max(cmin, (uint)ch);
                if (devChannels > cmax) throw new DeviceException($"the DAC takes at most {cmax} channels");
                if ((err = Alsa.HwSetChannels(pcm, hw, (uint)devChannels)) < 0) throw Fail(err, $"{devChannels} channels");
                if ((err = Alsa.HwSetRate(pcm, hw, (uint)r, 0)) < 0) throw new DeviceException($"the DAC doesn't take {Devices.KHz(r)}");
                uint periodUs = 50_000, bufferUs = 500_000;
                Alsa.HwSetPeriodTimeNear(pcm, hw, ref periodUs, IntPtr.Zero);
                Alsa.HwSetBufferTimeNear(pcm, hw, ref bufferUs, IntPtr.Zero);
                if ((err = Alsa.HwParamsApply(pcm, hw)) < 0) throw Fail(err, "the DAC refused the settings");
                canPause = Alsa.HwCanPause(hw) != 0;
                if ((err = Alsa.Prepare(pcm)) < 0) throw Fail(err, "prepare");
            }
            finally { Alsa.HwParamsFree(hw); }
            rate = r;
            srcChannels = ch;
            written = 0;
            hwPaused = false;
            configured = true;
            return $"{Alsa.FormatName(format)}, {devChannels} ch";
        }
    }

    private DeviceException Fail(int err, string what)
    {
        if (Alsa.IsGone(err)) Gone?.Invoke();
        return new DeviceException($"{what}: {Alsa.StrError(err)}");
    }

    public unsafe void Write(ReadOnlySpan<byte> pcmIn, CancellationToken ct)
    {
        int frames = pcmIn.Length / (4 * srcChannels);
        int frameBytes = devChannels * sampleBytes;
        if (scratch.Length < frames * frameBytes) scratch = new byte[frames * frameBytes];
        Convert(pcmIn, frames);
        int off = 0;
        while (off < frames)
        {
            ct.ThrowIfCancellationRequested();
            running.Wait(ct);
            nint n;
            lock (gate)
            {
                if (!configured || pcm == IntPtr.Zero) throw new DeviceException("the DAC isn't set up");
                fixed (byte* p = &scratch[off * frameBytes]) n = Alsa.WriteI(pcm, p, (nuint)(frames - off));
                if (n == Alsa.EPIPE || n == Alsa.ESTRPIPE || n == Alsa.EINTR) { Alsa.Recover(pcm, (int)n, 1); continue; }
                if (n < 0 && n != Alsa.EAGAIN)
                {
                    if (Alsa.IsGone((int)n)) { Gone?.Invoke(); }
                    throw new DeviceException("the DAC stopped: " + Alsa.StrError((int)n));
                }
                if (n > 0) { off += (int)n; written += n; }
            }
            if (n == Alsa.EAGAIN || n == 0)
            {
                // Full: a stream not yet running starts once its buffer is (start threshold).
                lock (gate) { if (pcm != IntPtr.Zero && Alsa.State(pcm) == Alsa.StatePrepared) Alsa.Start(pcm); }
                Alsa.Wait(pcm, 100);
            }
        }
    }

    // 32-bit samples → the DAC's format and channel count, into scratch.
    private void Convert(ReadOnlySpan<byte> src, int frames)
    {
        var o = scratch.AsSpan();
        int w = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < devChannels; c++)
            {
                int v = c < srcChannels ? BitConverter.ToInt32(src.Slice((f * srcChannels + c) * 4, 4)) : 0;
                switch (format)
                {
                    case Alsa.FormatS32Le: BitConverter.TryWriteBytes(o.Slice(w, 4), v); w += 4; break;
                    case Alsa.FormatS24Le: BitConverter.TryWriteBytes(o.Slice(w, 4), v >> 8); w += 4; break;
                    case Alsa.FormatS24_3Le: { int x = v >> 8; o[w] = (byte)x; o[w + 1] = (byte)(x >> 8); o[w + 2] = (byte)(x >> 16); w += 3; break; }
                    default: BitConverter.TryWriteBytes(o.Slice(w, 2), (short)(v >> 16)); w += 2; break;
                }
            }
        }
    }

    public void Drain(CancellationToken ct)
    {
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            running.Wait(ct);
            lock (gate)
            {
                if (pcm == IntPtr.Zero || !configured) break;
                int st = Alsa.State(pcm);
                if (st == Alsa.StatePrepared && written > 0) { Alsa.Start(pcm); st = Alsa.State(pcm); }
                // Out of samples: XRUN (or never started) means everything has played.
                if (st != Alsa.StateRunning && st != Alsa.StateDraining) break;
                if (Alsa.Delay(pcm, out var d) == 0 && d <= 0) break;
            }
            Thread.Sleep(10);
        }
        lock (gate)
        {
            if (pcm != IntPtr.Zero && configured) { Alsa.Drop(pcm); Alsa.Prepare(pcm); }
            written = 0;
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            if (pcm != IntPtr.Zero && configured) { Alsa.Drop(pcm); Alsa.Prepare(pcm); }
            written = 0;
            hwPaused = false;
            running.Set();
        }
    }

    public void Pause(bool on)
    {
        lock (gate)
        {
            if (on)
            {
                running.Reset();
                if (pcm != IntPtr.Zero && configured && Alsa.State(pcm) == Alsa.StateRunning)
                {
                    if (canPause && Alsa.Pause(pcm, 1) == 0) hwPaused = true;
                    else
                    {
                        // Can't hold it where it is: what it held is dropped, and the clock stops there.
                        long p = PlayedLocked();
                        Alsa.Drop(pcm);
                        Alsa.Prepare(pcm);
                        written = p;
                    }
                }
            }
            else
            {
                if (hwPaused && pcm != IntPtr.Zero) Alsa.Pause(pcm, 0);
                hwPaused = false;
                running.Set();
            }
        }
    }

    public void Share(bool on)
    {
        lock (gate)
        {
            if (disposed) return;
            shared = on;
            if (on)
            {
                if (pcm != IntPtr.Zero) { Alsa.Drop(pcm); Alsa.Close(pcm); pcm = IntPtr.Zero; }
                configured = false;
                Exclusive = false;
                HolderPid = 0;
                Message = Sharing.Idle;
                running.Set();
            }
        }
        if (!on && started) StartHolding();
        StatusChanged?.Invoke();
    }

    public bool Reclaim()
    {
        lock (gate)
        {
            if (disposed) return false;
            if (pcm != IntPtr.Zero) return true;
            int err = Alsa.Open(out pcm, device, Alsa.StreamPlayback, Alsa.NonBlock);
            if (err < 0)
            {
                pcm = IntPtr.Zero;
                Message = err == Alsa.EBUSY ? "another program is using the DAC" : Alsa.StrError(err);
                return false;
            }
            Exclusive = true;
            Message = "";
            HolderPid = Environment.ProcessId;
        }
        StatusChanged?.Invoke();
        return true;
    }

    public long Played { get { lock (gate) return PlayedLocked(); } }

    private long PlayedLocked()
    {
        if (pcm == IntPtr.Zero || !configured) return 0;
        int st = Alsa.State(pcm);
        nint d = 0;
        if (st is Alsa.StateRunning or Alsa.StatePaused or Alsa.StatePrepared or Alsa.StateDraining)
            if (Alsa.Delay(pcm, out d) < 0) d = 0;
        return Math.Max(0, written - Math.Max(0, (long)d));
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            if (pcm != IntPtr.Zero) { Alsa.Drop(pcm); Alsa.Close(pcm); pcm = IntPtr.Zero; }
            configured = false;
            Exclusive = false;
            running.Set();
        }
    }
}
