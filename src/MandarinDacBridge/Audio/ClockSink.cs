// ClockSink.cs — the tests' DAC: it takes samples at the speed a DAC would
// play them (half a second of buffer, emptied by the clock) and plays them
// nowhere. With busy set it acts as if another program had the DAC.
using System.Diagnostics;

namespace MandarinDacBridge.Audio;

internal sealed class ClockSink(bool busy = false) : ISink
{
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private int rate, channels;
    private long buffered, played;
    private double carry;
    private bool paused, configured;

    public bool Exclusive => !busy;
    public string Message => busy ? "another program has the DAC in exclusive mode" : "";
    public int HolderPid => busy ? 4242 : Environment.ProcessId;
    public event Action? StatusChanged;
    public event Action? Gone { add { } remove { } }

    public void Start() => StatusChanged?.Invoke();

    public string Configure(int r, int ch)
    {
        if (busy) throw new DeviceException(Message + " (pid 4242)");
        lock (gate) { rate = r; channels = ch; buffered = 0; played = 0; carry = 0; configured = true; clock.Restart(); }
        return $"S32_LE, {ch} ch (clock)";
    }

    // The clock: what the "DAC" has played since last asked.
    private void Tick()
    {
        double ms = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        if (paused || !configured) return;
        double want = rate * ms / 1000 + carry;
        long n = Math.Min(buffered, (long)want);
        carry = buffered > 0 ? want - (long)want : 0;
        buffered -= n;
        played += n;
    }

    public void Write(ReadOnlySpan<byte> pcm, CancellationToken ct)
    {
        long frames = pcm.Length / (4 * Math.Max(1, channels));
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                Tick();
                if (!configured) throw new DeviceException("not set up");
                if (buffered == 0 || buffered + frames <= rate / 2) { buffered += frames; return; }
            }
            Thread.Sleep(10);
        }
    }

    public void Drain(CancellationToken ct)
    {
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) { Tick(); if (buffered == 0) { played = 0; return; } }
            Thread.Sleep(10);
        }
    }

    public void Flush() { lock (gate) { buffered = 0; played = 0; carry = 0; paused = false; clock.Restart(); } }

    public void Pause(bool on) { lock (gate) { Tick(); paused = on; } }

    public long Played { get { lock (gate) { Tick(); return played; } } }

    public void Dispose() { }
}
