// AudioTests.cs — the sinks' own decisions, apart from any DAC (Audio/). What only a real DAC shows (WASAPI,
// Core Audio, ALSA on hardware) isn't tested here.
using MandarinDacBridge.Audio;
using Xunit;

namespace MandarinDacBridge.Tests;

public class AudioTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    // The end of a track plays out while the DAC plays it, and is waited on.
    [Fact]
    public void TheEndOfATrack_IsWaitedOn_WhilePlaying()
    {
        var w = new DrainWait(Limit, 0);
        Assert.True(w.Goes(pending: true, running: true, paused: false, 1000));
        Assert.False(w.Goes(pending: false, running: true, paused: false, 1500));
    }

    // Paused during the end of a track (Windows before 1.1.18: the wait ended, the end was thrown away and
    // the DAC unpaused): the wait goes on, however long the pause, and ends once the rest has played.
    [Fact]
    public void APauseDuringTheEndOfATrack_DoesntEndTheWait_NorCountAgainstIt()
    {
        var w = new DrainWait(Limit, 0);
        Assert.True(w.Goes(pending: true, running: true, paused: false, 1000));
        Assert.True(w.Goes(pending: true, running: false, paused: true, 2000));      // paused: stopped, still waited on
        Assert.True(w.Goes(pending: true, running: false, paused: true, 60_000));    // a minute paused doesn't count
        Assert.True(w.Goes(pending: true, running: true, paused: false, 60_500));    // resumed
        Assert.False(w.Goes(pending: false, running: true, paused: false, 61_000));  // and played
    }

    // A look that comes late (the sink's lock was busy) counts the time since the one before as it was then:
    // seconds paused aren't taken for seconds playing because the stream had just resumed.
    [Fact]
    public void ALateLook_CountsTheTimeBeforeIt_AsItWas()
    {
        var w = new DrainWait(Limit, 0);
        Assert.True(w.Goes(pending: true, running: false, paused: true, 100));
        Assert.True(w.Goes(pending: true, running: true, paused: false, 4_900));     // 4.8 s paused, then resumed
        Assert.True(w.Goes(pending: true, running: true, paused: false, 5_100));
    }

    // A WASAPI stream always has a buffer queued: after the track's last samples, silence. The samples have
    // all played once the clock passes their end, though silence was handed after them (before 1.1.18 the
    // end was taken as still to play until the drain's five seconds ran out, at every track's end).
    [Fact]
    public void TheLastSamples_HavePlayed_OnceTheClockPassesThem_ThoughSilenceIsQueued()
    {
        var h = new Handed();
        h.Buffer(0, 480);        // before the start
        h.Buffer(480, 480);
        h.Buffer(200, 480);      // the track's last 200 frames, then silence
        h.Buffer(0, 480);        // silence, queued
        Assert.True(h.Pending(480 + 480 + 199));
        Assert.False(h.Pending(480 + 480 + 200));
        Assert.Equal(680, h.Played(480 + 480 + 480 + 100));   // into the silence queued after it: all played
        Assert.True(h.Pending(-1));    // a clock that can't say for a moment: still waited on (the limit ends it)
        Assert.False(new Handed().Pending(-1));   // nothing handed: nothing to wait on
    }

    // The position shown: the clock less the silence it has passed (before the start, and while a live
    // source left the ring empty), never the silence only queued.
    [Fact]
    public void ThePositionPlayed_LeavesOut_OnlyTheSilencePlayed()
    {
        var h = new Handed();
        h.Buffer(0, 480);        // before the start
        h.Buffer(480, 480);
        h.Buffer(0, 480);        // the source was late: silence
        h.Buffer(480, 480);
        Assert.Equal(0, h.Played(400));
        Assert.Equal(100, h.Played(580));
        Assert.Equal(480, h.Played(1200));    // in the late silence: the samples before it
        Assert.Equal(580, h.Played(1540));
        Assert.Equal(960, h.Played(5000));    // at most what was handed
    }

    // A DAC that stops taking samples while playing: given up on after five seconds of playing.
    [Fact]
    public void ADacThatStopsTakingSamples_IsGivenUpOn()
    {
        var w = new DrainWait(Limit, 0);
        Assert.True(w.Goes(pending: true, running: true, paused: false, 4900));
        Assert.False(w.Goes(pending: true, running: true, paused: false, 5100));
    }

    // A stream that couldn't start (and isn't paused) has nothing to wait on.
    [Fact]
    public void AStreamThatIsntPlaying_IsntWaitedOn() =>
        Assert.False(new DrainWait(Limit, 0).Goes(pending: true, running: false, paused: false, 10));
}
