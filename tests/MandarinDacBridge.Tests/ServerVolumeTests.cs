// ServerVolumeTests.cs — a Squeezebox server's volume and the DAC's, kept as one (Slim/ServerVolume.cs).
using MandarinDacBridge.Slim;
using Xunit;

namespace MandarinDacBridge.Tests;

public class ServerVolumeTests
{
    // The gains LMS sends a squeezeplay player for these volumes, worked out with LMS's own formulas
    // (Slim::Player::SqueezePlay::getVolumeParameters, Squeezebox2::getVolume and dBToFixed) by hand.
    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 16u)]
    [InlineData(10, 72u)]
    [InlineData(25, 926u)]
    [InlineData(26, 980u)]
    [InlineData(39, 2050u)]     // just below -30 dB: full precision
    [InlineData(40, 2048u)]     // just above: 8 bits, two away from 39's
    [InlineData(50, 3840u)]
    [InlineData(60, 6656u)]
    [InlineData(75, 15872u)]
    [InlineData(100, 65536u)]
    public void TheServersGain_IsReadAsTheNumberItsSliderShows(int volume, uint gain)
    {
        Assert.Equal(gain, ServerVolume.GainOf(volume));
        Assert.Equal(volume, ServerVolume.VolumeOf(gain));
    }

    [Fact]
    public void EveryStepReadsBack_AsItselfFromWhereTheDacIs_AndAsTheSameSoundFromAnywhere()
    {
        for (int v = 0; v <= 100; v++)
        {
            var g = ServerVolume.GainOf(v);
            Assert.Equal(v, ServerVolume.VolumeOf(g, near: v));
            Assert.Equal(g, ServerVolume.GainOf(ServerVolume.VolumeOf(g)));
        }
    }

    [Theory]
    [InlineData(41, 42)]
    [InlineData(44, 45)]
    [InlineData(47, 48)]
    public void TwoStepsWithOneGain_ReadAsTheOneNearestTheDac(int low, int high)
    {
        var g = ServerVolume.GainOf(low);
        Assert.Equal(g, ServerVolume.GainOf(high));
        Assert.Equal(low, ServerVolume.VolumeOf(g, near: low));
        Assert.Equal(high, ServerVolume.VolumeOf(g, near: high));
        Assert.Equal(low, ServerVolume.VolumeOf(g, near: 0));
        Assert.Equal(high, ServerVolume.VolumeOf(g, near: 100));
    }

    private static int? Take(ServerVolume sv, int volume, int dac) => sv.Take(volume, dac, refused: false, out _);

    [Fact]
    public void WhileDeciding_NothingItSendsIsTaken()
    {
        var sv = new ServerVolume();
        sv.Connected();
        Assert.Null(Take(sv, 100, 30));      // what LMS had stored, sent as the player connects
        Assert.Null(Take(sv, 60, 30));       // anything else before the bridge knows whether it can tell it
        Assert.Null(sv.ToTell(45));          // and nothing is sent until then
    }

    [Fact]
    public void Told_ItsSliderIsTheDacs_FromItsFirstEcho()
    {
        var sv = new ServerVolume();
        sv.Connected();
        Assert.Null(Take(sv, 100, 60));      // stored, sent on connecting
        sv.Telling(60);                      // the bridge sets LMS to the DAC's level
        sv.Told();
        Assert.Null(Take(sv, 100, 60));      // a late copy of what it had: not taken
        Assert.Null(Take(sv, 60, 60));       // its echo: from now on its slider is the DAC's
        Assert.Null(Take(sv, 60, 60));       // the same again with every stream it starts
        Assert.Equal(30, Take(sv, 30, 60));  // its slider moved: the DAC goes to the number it shows
        Assert.Null(sv.ToTell(30));          // which isn't sent back
        Assert.Equal(45, sv.ToTell(45));     // the DAC moved elsewhere: the server is told
    }

    [Fact]
    public void ToldALevelItAlreadyHas_ItSendsNothingBack_AndIsFollowedAtOnce()
    {
        var sv = new ServerVolume();
        sv.Connected();
        Assert.Null(Take(sv, 60, 60));       // it connected with the DAC's level already
        sv.Telling(60);
        sv.Told();
        Assert.Equal(50, Take(sv, 50, 60));
    }

    [Fact]
    public void EchoesOfLevelsToldOneAfterAnother_NeverPutTheDacBack()
    {
        var sv = new ServerVolume();
        sv.Connected();
        sv.Telling(50);
        sv.Told();
        Assert.Null(Take(sv, 50, 50));
        // The page's slider dragged 50 -> 40 -> 30: LMS is told both, and echoes them late.
        sv.Telling(40);
        sv.Telling(30);
        Assert.Null(Take(sv, 40, 30));       // not a move back up to 40
        Assert.Null(Take(sv, 30, 30));
        Assert.Equal(20, Take(sv, 20, 30));  // its own slider still counts
    }

    [Fact]
    public void Told_AChangeRefusedWhileAnotherAppHasTheDac_IsPutBack_NotTakenLater()
    {
        var sv = new ServerVolume();
        sv.Connected();
        sv.Telling(20);
        sv.Told();
        Assert.Null(Take(sv, 20, 20));
        Assert.Null(sv.Take(90, 20, refused: true, out bool putBack));
        Assert.True(putBack);                // the bridge tells LMS 20 again
        sv.Telling(20);
        Assert.Null(Take(sv, 20, 20));       // its echo
        Assert.Null(Take(sv, 20, 20));       // and its re-send at the next stream: nothing moves
    }

    [Fact]
    public void Told_AnEchoArrivingWhileAnotherAppHasTheDac_StillCounts()
    {
        var sv = new ServerVolume();
        sv.Connected();
        sv.Telling(30);
        sv.Told();
        Assert.Null(sv.Take(30, 30, refused: true, out bool putBack));
        Assert.False(putBack);
        Assert.Equal(50, Take(sv, 50, 30));  // its slider works once the other app is done
    }

    [Fact]
    public void NotTold_OnlyItsChangesMoveTheDac_ByAsMuch()
    {
        var sv = new ServerVolume();
        sv.Connected();
        Assert.Null(Take(sv, 100, 30));      // where its slider is as it connects: nothing happens, however loud
        sv.NotTold();                        // no JSON-RPC (Roon's)
        Assert.Null(Take(sv, 100, 30));      // the same again (a stream starting)
        Assert.Equal(25, Take(sv, 95, 30));  // turned down 5: the DAC down 5, not up to 95
        Assert.Equal(30, Take(sv, 100, 25)); // and up 5 again
        Assert.Null(sv.ToTell(40));          // can't be told
    }

    [Fact]
    public void NotTold_AChangeRefusedWhileAnotherAppHasTheDac_IsntAddedToTheNext()
    {
        var sv = new ServerVolume();
        sv.Connected();
        sv.NotTold();
        Assert.Null(Take(sv, 50, 30));
        Assert.Null(sv.Take(80, 30, refused: true, out _));
        Assert.Equal(31, Take(sv, 81, 30)); // up 1, not 31
    }

    [Fact]
    public void NotTold_WithNothingSentYet_ItsFirstVolumeIsWhereItsSliderIs()
    {
        var sv = new ServerVolume();
        sv.Connected();
        sv.NotTold();
        Assert.Null(Take(sv, 100, 30));
        Assert.Equal(20, Take(sv, 90, 30));
    }

    [Fact]
    public void NotTold_AChangeNeverTakesTheDacPastTheEnds()
    {
        var sv = new ServerVolume();
        sv.Connected();
        sv.NotTold();
        Assert.Null(Take(sv, 10, 5));
        Assert.Equal(0, Take(sv, 0, 5));     // down 10 from 5: silent, not below
        Assert.Null(Take(sv, 0, 0));
        Assert.Equal(100, Take(sv, 100, 0)); // up 100 from 0: the top, no further
    }

    [Fact]
    public void AReconnect_StartsOver()
    {
        var sv = new ServerVolume();
        sv.Connected();
        sv.Telling(50);
        sv.Told();
        Assert.Null(Take(sv, 50, 50));
        Assert.Equal(20, Take(sv, 20, 50));
        sv.Connected();
        Assert.Null(Take(sv, 100, 20));      // what it sends as it comes back isn't applied
        Assert.Null(sv.ToTell(25));          // and nothing is told until the bridge tells it again
    }
}
