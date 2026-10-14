// DrainWait.cs — how long the end of a track is waited on, as it plays out of the DAC's buffer (a sink's
// Drain).
//
// Up to five seconds of playing: a DAC that has stopped taking samples can't hold the next track up for ever.
// Time paused doesn't count, and a pause doesn't end the wait: the end of the track is held while paused and
// plays when the pause ends, as on the other sinks (ClockSink's clock stops; ALSA's and Core Audio's drains
// wait). Before 1.1.18 WASAPI's drain ended at a pause, threw the end of the track away and unpaused the DAC.
namespace MandarinDacBridge.Audio;

internal sealed class DrainWait(TimeSpan limit, long now)
{
    private long last = now;
    private long playingMs;
    private bool wasPlaying = true;   // Drain starts the stream first

    // Whether to wait on, at now (ms): something written is still to play, the stream is playing it or paused,
    // and it hasn't played for longer than the limit. The time since the last look counts as it was then (a
    // look can come late, and a pause just ended isn't time spent playing).
    public bool Goes(bool pending, bool running, bool paused, long now)
    {
        if (wasPlaying) playingMs += now - last;
        last = now;
        wasPlaying = running && !paused;
        return pending && (running || paused) && playingMs < limit.TotalMilliseconds;
    }
}
