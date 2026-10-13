// Handed.cs — what a WASAPI stream has been handed, buffer by buffer (WasapiSink): where in it the track's
// samples are and where silence is, so that the DAC's clock (frames played, silence included) can be read as
// the track's frames played, and as whether any are still to play.
//
// The DAC asks for a buffer at a time and always has the next one queued while it plays; once the ring is
// empty that next one is silence. Silence counts only once the clock has passed it. Before 1.1.18 it was
// subtracted as soon as it was handed: the track's end looked unplayed until the drain's five seconds ran
// out, at every track's end, and the position lagged by the silence queued.
namespace MandarinDacBridge.Audio;

internal sealed class Handed
{
    private readonly LinkedList<(long Start, long End)> silent = new();   // spans of silence the clock hasn't passed
    private long all, realEnd, silencePassed;

    public long Real { get; private set; }   // frames of the track's samples

    // One buffer of bufferFrames: its first realFrames the track's samples, the rest silence.
    public void Buffer(long realFrames, long bufferFrames)
    {
        if (realFrames > 0) realEnd = all + realFrames;
        Real += realFrames;
        long from = all + realFrames, to = all + bufferFrames;
        if (to > from)
        {
            if (silent.Last is { } last && last.Value.End == from) last.Value = (last.Value.Start, to);
            else silent.AddLast((from, to));
        }
        all = to;
    }

    public void Reset()
    {
        silent.Clear();
        all = realEnd = silencePassed = Real = 0;
    }

    // Whether samples handed are still to play, at the clock's position (frames since the reset; -1 when the
    // clock can't say, and then they are taken as still to play: the drain's limit ends the wait).
    public bool Pending(long position)
    {
        if (position < 0) return realEnd > 0;
        Pass(position);
        return position < realEnd;
    }

    // The track's frames played, at the clock's position: the clock less the silence it has passed.
    public long Played(long position)
    {
        if (position < 0) return 0;
        Pass(position);
        long within = silent.First is { } first && first.Value.Start < position ? position - first.Value.Start : 0;
        return Math.Clamp(position - silencePassed - within, 0, Real);
    }

    // The spans of silence the clock has passed, counted and let go.
    private void Pass(long position)
    {
        while (silent.First is { } first && first.Value.End <= position)
        {
            silencePassed += first.Value.End - first.Value.Start;
            silent.RemoveFirst();
        }
    }
}
