// CoreAudioSink.cs — a USB DAC on a Mac, held in Core Audio's hog mode.
//
// Hog mode is exclusive access: while the bridge holds it, no other program
// (Audirvana, Mandarin, Music, the system sounds) can play to the DAC. The
// DAC is set to each track's own rate by choosing its physical format
// (integer, at its widest), so nothing is resampled. Samples reach the HAL as
// 32-bit float, which carries 24-bit audio — and DoP — exactly.
//
// The HAL's real-time thread calls the IOProc for every buffer. It reads only
// native memory (the Shared block: a ring of 32-bit samples and its
// positions), never the managed heap.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MandarinDacBridge.Native;

namespace MandarinDacBridge.Audio;

internal sealed unsafe class CoreAudioSink(string uid) : ISink
{
    // What the IOProc and the writer share, in native memory.
    [StructLayout(LayoutKind.Sequential)]
    private struct Shared
    {
        public int* Ring;
        public long Capacity;     // frames
        public int Channels;      // in the ring
        public long WritePos, ReadPos, Played;
        public int Draining, DrainedFlag, UnderrunFlag;
    }

    private readonly object gate = new();
    private readonly Shared* sh = (Shared*)NativeMemory.AllocZeroed((nuint)sizeof(Shared));
    private uint dev;
    private IntPtr procId;
    private bool haveHog, configured, paused, ioRunning, disposed, shared;
    private int rate;
    private int myPid;
    private Timer? watch;

    public bool Exclusive => haveHog;
    public string Message { get; private set; } = "starting";
    public int HolderPid { get; private set; }
    public event Action? StatusChanged;
    public event Action? Gone;

    public void Start()
    {
        myPid = Environment.ProcessId;
        dev = CoreAudio.FindDevice(uid);
        if (dev == CoreAudio.Unknown) { Gone?.Invoke(); return; }
        int err = CoreAudio.CreateIOProcId(dev, &IoProc, sh, out procId);
        if (err != 0 || procId == IntPtr.Zero) { Message = $"Core Audio wouldn't play to this device ({err})"; StatusChanged?.Invoke(); return; }
        Check();
        watch = new Timer(_ => Check(), null, 2000, 2000);
    }

    // The DAC still there, and still ours: hog mode is taken back if it was lost, retried while another program has it.
    private void Check()
    {
        bool changed;
        lock (gate)
        {
            if (disposed) return;
            var now = CoreAudio.FindDevice(uid);
            var alive = now == CoreAudio.Unknown ? 0u : CoreAudio.Get<uint>(now, CoreAudio.DeviceIsAlive, CoreAudio.ScopeGlobal) ?? 1u;
            if (now != dev || alive == 0)
            {
                ThreadPool.QueueUserWorkItem(_ => Gone?.Invoke());
                return;
            }
            bool was = haveHog;
            var before = Message;
            int owner = CoreAudio.HogOwner(dev);
            if (owner != myPid)
            {
                if (ioRunning) StopIo();
                // Shared: not taken back here, only when the bridge is asked to play (Reclaim).
                haveHog = !shared && owner == -1 && TakeHog();
            }
            else haveHog = true;
            int holder = haveHog ? myPid : CoreAudio.HogOwner(dev);
            Message = haveHog ? "" : shared && holder <= 0 ? Sharing.Idle : "another program has the DAC in exclusive mode";
            changed = was != haveHog || holder != HolderPid || before != Message;
            HolderPid = holder;
        }
        if (changed) StatusChanged?.Invoke();
    }

    // Hog mode is a toggle: setting it while nobody has it takes it.
    private bool TakeHog()
    {
        CoreAudio.Set(dev, CoreAudio.DeviceHogMode, CoreAudio.ScopeGlobal, myPid);
        return CoreAudio.HogOwner(dev) == myPid;
    }

    public string Configure(int r, int ch)
    {
        lock (gate)
        {
            StopIo();
            ResetRing();
            configured = false;
            if (!haveHog) throw new DeviceException(Message == "" ? "the DAC isn't the bridge's yet" : Message);
            var stream = CoreAudio.FirstOutputStream(dev);
            if (stream == CoreAudio.Unknown) throw new DeviceException("the DAC has no output stream");
            var formats = CoreAudio.GetArray<AudioStreamRangedDescription>(stream, CoreAudio.StreamAvailablePhysicalFormats, CoreAudio.ScopeGlobal);
            int best = -1, bestScore = int.MinValue;
            for (int k = 0; k < formats.Length; k++)
            {
                var f = formats[k].Format;
                if (f.FormatId != CoreAudio.FormatLinearPcm) continue;
                if (r + 0.5 < formats[k].SampleRateRange.Minimum || r - 0.5 > formats[k].SampleRateRange.Maximum) continue;
                if (f.ChannelsPerFrame < ch) continue;
                int score = ((f.FormatFlags & CoreAudio.FlagIsFloat) != 0 ? 0 : 1000) + (int)f.BitsPerChannel - (int)f.ChannelsPerFrame;
                if (score > bestScore) { bestScore = score; best = k; }
            }
            if (best < 0) throw new DeviceException($"the DAC doesn't take {Devices.KHz(r)}");
            var phys = formats[best].Format;
            phys.SampleRate = r;
            if (CoreAudio.Set(stream, CoreAudio.StreamPhysicalFormat, CoreAudio.ScopeGlobal, phys) != 0)
                CoreAudio.Set(dev, CoreAudio.DeviceNominalSampleRate, CoreAudio.ScopeGlobal, (double)r);   // some drivers only take it here
            for (int i = 0; i < 150 && (int)Math.Round(CoreAudio.NominalRate(dev)) != r; i++) Thread.Sleep(20);
            if ((int)Math.Round(CoreAudio.NominalRate(dev)) != r) throw new DeviceException($"the DAC didn't change to {Devices.KHz(r)}");

            var virt = CoreAudio.Get<AudioStreamBasicDescription>(stream, CoreAudio.StreamVirtualFormat, CoreAudio.ScopeGlobal) ?? default;
            if ((virt.FormatFlags & CoreAudio.FlagIsFloat) == 0 || virt.BitsPerChannel != 32 || (virt.FormatFlags & CoreAudio.FlagIsNonInterleaved) != 0)
            {
                uint vch = virt.ChannelsPerFrame != 0 ? virt.ChannelsPerFrame : phys.ChannelsPerFrame;
                var want = new AudioStreamBasicDescription
                {
                    SampleRate = r, FormatId = CoreAudio.FormatLinearPcm, FormatFlags = CoreAudio.FlagIsFloat | CoreAudio.FlagIsPacked,
                    ChannelsPerFrame = vch, BitsPerChannel = 32, BytesPerFrame = 4 * vch, FramesPerPacket = 1, BytesPerPacket = 4 * vch
                };
                if (CoreAudio.Set(stream, CoreAudio.StreamVirtualFormat, CoreAudio.ScopeGlobal, want) != 0)
                    throw new DeviceException("the DAC's stream can't take 32-bit float");
            }

            long capacity = (long)r * 2;   // two seconds
            NativeMemory.Free(sh->Ring);
            sh->Ring = (int*)NativeMemory.AllocZeroed((nuint)(capacity * ch * sizeof(int)));
            sh->Capacity = capacity;
            sh->Channels = ch;
            ResetRing();
            rate = r;
            configured = true;
            return $"{phys.BitsPerChannel}-bit {((phys.FormatFlags & CoreAudio.FlagIsFloat) != 0 ? "float" : "integer")}, {phys.ChannelsPerFrame} ch";
        }
    }

    private long Buffered => Volatile.Read(ref sh->WritePos) - Volatile.Read(ref sh->ReadPos);

    public void Write(ReadOnlySpan<byte> pcm, CancellationToken ct)
    {
        int ch = sh->Channels;
        long frames = pcm.Length / (4 * ch);
        long off = 0;
        fixed (byte* src = pcm)
        {
            var s = (int*)src;
            while (off < frames)
            {
                ct.ThrowIfCancellationRequested();
                if (!configured) throw new DeviceException("the DAC isn't set up");
                long w = sh->WritePos;
                long space = sh->Capacity - (w - Volatile.Read(ref sh->ReadPos));
                long n = Math.Min(space, frames - off);
                long done = 0;
                while (done < n)
                {
                    long at = (w + done) % sh->Capacity;
                    long run = Math.Min(sh->Capacity - at, n - done);
                    Buffer.MemoryCopy(s + (off + done) * ch, sh->Ring + at * ch, run * ch * sizeof(int), run * ch * sizeof(int));
                    done += run;
                }
                Volatile.Write(ref sh->WritePos, w + n);
                off += n;
                MaybeStart();
                if (off < frames) Thread.Sleep(5);
            }
        }
    }

    private void MaybeStart()
    {
        lock (gate)
        {
            if (!ioRunning && !paused && configured && haveHog && (Buffered >= rate / 4 || Volatile.Read(ref sh->Draining) != 0)) StartIo();
        }
    }

    public void Drain(CancellationToken ct)
    {
        Volatile.Write(ref sh->Draining, 1);
        MaybeStart();
        while (Buffered > 0 && Volatile.Read(ref sh->DrainedFlag) == 0)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(10);
        }
        Thread.Sleep(60);   // the last buffer is still on its way out
        lock (gate)
        {
            StopIo();
            ResetRing();
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            StopIo();
            ResetRing();
            paused = false;
        }
    }

    public void Pause(bool on)
    {
        lock (gate)
        {
            paused = on;
            if (on) StopIo();
            else if (configured && (Buffered > 0 || Volatile.Read(ref sh->Draining) != 0)) StartIo();
        }
    }

    public long Played => Volatile.Read(ref sh->Played);

    private void StartIo()
    {
        if (!ioRunning && haveHog && configured && procId != IntPtr.Zero && CoreAudio.DeviceStart(dev, procId) == 0) ioRunning = true;
    }

    private void StopIo()
    {
        if (ioRunning) { CoreAudio.DeviceStop(dev, procId); ioRunning = false; }
    }

    // With the IO stopped.
    private void ResetRing()
    {
        Volatile.Write(ref sh->ReadPos, Volatile.Read(ref sh->WritePos));
        Volatile.Write(ref sh->Played, 0);
        Volatile.Write(ref sh->Draining, 0);
        Volatile.Write(ref sh->DrainedFlag, 0);
        Volatile.Write(ref sh->UnderrunFlag, 0);
    }

    // The HAL's real-time thread: the ring → the device's float buffer. Silence when it runs dry.
    [UnmanagedCallersOnly]
    private static int IoProc(uint device, void* now, AudioBufferList* input, void* inputTime, AudioBufferList* output, void* outputTime, void* client)
    {
        var s = (Shared*)client;
        if (output == null || output->NumberBuffers == 0) return 0;
        var buffers = &output->First;
        for (int i = 1; i < output->NumberBuffers; i++)
            if (buffers[i].Data != null) Unsafe.InitBlockUnaligned(buffers[i].Data, 0, buffers[i].DataByteSize);
        int ch = (int)buffers[0].NumberChannels;
        if (ch == 0 || buffers[0].Data == null) return 0;
        var dst = (float*)buffers[0].Data;
        long frames = buffers[0].DataByteSize / (sizeof(float) * ch);
        long r = s->ReadPos;
        long avail = Volatile.Read(ref s->WritePos) - r;
        long n = Math.Min(avail, frames);
        int src = s->Channels;
        const float scale = 1.0f / 2147483648.0f;
        for (long f = 0; f < n; f++)
        {
            int* smp = s->Ring + ((r + f) % s->Capacity) * src;
            float* o = dst + f * ch;
            for (int c = 0; c < ch; c++) o[c] = c < src ? smp[c] * scale : 0f;
        }
        if (n < frames) Unsafe.InitBlockUnaligned(dst + n * ch, 0, (uint)((frames - n) * ch * sizeof(float)));
        Volatile.Write(ref s->ReadPos, r + n);
        Volatile.Write(ref s->Played, s->Played + n);
        if (n < frames)
        {
            if (Volatile.Read(ref s->Draining) != 0) Volatile.Write(ref s->DrainedFlag, 1);
            else Volatile.Write(ref s->UnderrunFlag, 1);
        }
        return 0;
    }

    public void Share(bool on)
    {
        lock (gate)
        {
            if (disposed) return;
            shared = on;
            if (on && haveHog)
            {
                StopIo();
                ResetRing();
                configured = false;
                // Hog mode is a toggle: setting it again gives it back.
                if (CoreAudio.HogOwner(dev) == myPid) CoreAudio.Set(dev, CoreAudio.DeviceHogMode, CoreAudio.ScopeGlobal, myPid);
                haveHog = false;
                HolderPid = 0;
                Message = Sharing.Idle;
            }
        }
        if (!on) Check();
        StatusChanged?.Invoke();
    }

    public bool Reclaim()
    {
        lock (gate)
        {
            if (disposed || dev == CoreAudio.Unknown) return false;
            if (haveHog) return true;
            int owner = CoreAudio.HogOwner(dev);
            haveHog = owner == myPid || (owner == -1 && TakeHog());
            if (!haveHog) { Message = "another program has the DAC in exclusive mode"; return false; }
            HolderPid = myPid;
            Message = "";
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
            watch?.Dispose();
            StopIo();
            if (procId != IntPtr.Zero) CoreAudio.DestroyIOProcId(dev, procId);
            procId = IntPtr.Zero;
            // Giving hog mode back: it is a toggle, and ours.
            if (haveHog && CoreAudio.HogOwner(dev) == myPid) CoreAudio.Set(dev, CoreAudio.DeviceHogMode, CoreAudio.ScopeGlobal, myPid);
            haveHog = false;
            NativeMemory.Free(sh->Ring);
            sh->Ring = null;
        }
    }
}
