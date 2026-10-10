// LiveSource.cs — samples handed over by a producer inside the bridge (Spotify
// Soloist's sound, read from its private sound server), read by the renderer
// as a "live:" track. The queue is short: the producer waits while the DAC is
// behind, so the DAC's clock sets the pace.
using System.Collections.Concurrent;

namespace MandarinDacBridge.Spotify;

internal sealed class LiveSource(TrackInfo info, int capacity = 12) : IPcmSource
{
    private readonly BlockingCollection<byte[]> queue = new(boundedCapacity: capacity);
    private readonly CancellationTokenSource closed = new();
    private byte[] cur = [];
    private int at;

    public TrackInfo Info { get; } = info;
    public bool Closed => closed.IsCancellationRequested;

    public void Preload(byte[] chunk) => queue.TryAdd(chunk);

    // Without waiting (a producer on someone else's clock): false when full or closed.
    public bool TryPush(byte[] chunk) => !closed.IsCancellationRequested && queue.TryAdd(chunk);

    public int Count => queue.Count;

    // Blocks while the renderer is behind; false once the renderer has let go.
    public bool Push(byte[] chunk, CancellationToken ct)
    {
        if (closed.IsCancellationRequested) return false;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, closed.Token);
        try { queue.Add(chunk, both.Token); return true; }
        catch (Exception) { return false; }
    }

    public int Read(Span<byte> buffer, CancellationToken ct)
    {
        while (at >= cur.Length)
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, closed.Token);
            try { cur = queue.Take(both.Token); }
            catch (Exception) { return 0; }
            at = 0;
        }
        int n = Math.Min(buffer.Length, cur.Length - at);
        cur.AsSpan(at, n).CopyTo(buffer);
        at += n;
        return n;
    }

    public void Dispose()
    {
        if (closed.IsCancellationRequested) return;
        closed.Cancel();
    }
}
