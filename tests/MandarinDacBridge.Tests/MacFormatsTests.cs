// MacFormatsTests.cs — on a Mac the DAC is set to a track's rate by choosing one of the physical formats Core
// Audio lists for its stream (Audio/MacFormats.cs). A FiiO K5 Pro on macOS 27 listed none: its panel showed
// 44.1 to 768 kHz (the device's nominal rates) and the bridge said "the DAC doesn't take 44.1 kHz". These
// pin the choice, what each refusal says, and the report of what Core Audio said, on any system: what Core
// Audio answers is given here as data (Native/CoreAudioProbe.cs reads it on a Mac).
using MandarinDacBridge.Audio;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class MacFormatsTests
{
    private static MacRawFormat Pcm(int bits, double min, double max, int ch = 2, bool isFloat = false) => new("lpcm", min, max, bits, isFloat, ch);

    [Fact]
    public void TheWidestIntegerFormatThatTakesTheRate_IsChosen()
    {
        MacRawFormat[] formats = [Pcm(32, 44100, 384000, isFloat: true), Pcm(16, 44100, 768000), Pcm(24, 44100, 768000), new("ac-3", 48000, 48000, 16, false, 2)];
        Assert.Equal(2, MacFormats.Pick(formats, 44100, 2));
        Assert.Equal(2, MacFormats.Pick(formats, 768000, 2));
    }

    [Fact]
    public void NoFormatThatTakesTheRateOrTheChannels_IsNoChoice()
    {
        MacRawFormat[] formats = [Pcm(24, 48000, 96000), Pcm(24, 44100, 44100, ch: 1)];
        Assert.Equal(-1, MacFormats.Pick(formats, 44100, 2));
        Assert.Equal(-1, MacFormats.Pick([], 44100, 2));
        Assert.Equal(-1, MacFormats.Pick([new MacRawFormat("ac-3", 44100, 44100, 16, false, 2)], 44100, 2));
    }

    // What the probe read just after the sink's read came up empty: a stream, and its list ('pft?') read this way.
    private static CoreAudioProbe Probe(int rate, CoreAudioRead? list, bool stream = true) => new("FiiO K5 Pro", "macOS 27.0", rate, [],
        stream ? [new CoreAudioStreamProbe(117, list == null ? [] : [list])] : []);

    private static CoreAudioRead List(bool has = true, int sizeStatus = 0, uint size = 0, int readStatus = 0, params string[] values) =>
        new("pft?", "available physical formats", has, sizeStatus, size, readStatus, values, 56);

    // The report: an empty list is Core Audio's answer, not the DAC refusing the rate.
    [Fact]
    public void NoFormatsListed_SaysCoreAudioListsNone_NotThatTheDacRefusesTheRate()
    {
        var said = MacFormats.Refusal([], Probe(44100, List()), 2);
        Assert.Contains("Core Audio lists no formats for this DAC (macOS 27.0), so it can't be set to 44.1 kHz", said, StringComparison.Ordinal);
        Assert.DoesNotContain("doesn't take", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AListThatCantBeRead_SaysWhichStepFailed_WithItsCode()
    {
        Assert.Contains("Core Audio has no list of formats for this DAC (macOS 27.0)",
            MacFormats.Refusal([], Probe(44100, List(has: false)), 2), StringComparison.Ordinal);
        Assert.Contains("Core Audio couldn't read this DAC's formats (asking their size: 2003332927 ('who?'))",
            MacFormats.Refusal([], Probe(44100, List(sizeStatus: 2003332927)), 2), StringComparison.Ordinal);
        Assert.Contains("Core Audio couldn't read this DAC's formats (reading them: 560947818 ('!obj'))",
            MacFormats.Refusal([], Probe(44100, List(size: 56, readStatus: 560947818)), 2), StringComparison.Ordinal);
        Assert.Contains("Core Audio's list of this DAC's formats is 40 bytes, not whole entries of 56",
            MacFormats.Refusal([], Probe(44100, List(size: 40, values: "40 bytes left over (an entry is 56)")), 2), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOtherKindsOfFormatListed_SaysSo()
    {
        var said = MacFormats.Refusal([new MacRawFormat("ac-3", 48000, 48000, 16, false, 2)], Probe(48000, List(size: 56, values: "ac-3 16-bit integer 2 ch 48000")), 2);
        Assert.Contains("Core Audio lists no PCM formats for this DAC, only ac-3", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AListThatCameBackOnlyTheSecondTime_SaysTryAgain()
    {
        var said = MacFormats.Refusal([], Probe(44100, List(size: 56, values: "lpcm 24-bit integer 2 ch 44100-768000")), 2);
        Assert.Contains("changed between two reads; try again", said, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOutputStream_SaysSo()
    {
        var said = MacFormats.Refusal([], Probe(44100, null, stream: false), 2);
        Assert.Contains("Core Audio shows no output stream for this DAC", said, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatsListedButNotTheRate_SaysTheDacDoesntTakeIt_AndWhatItTakes()
    {
        var said = MacFormats.Refusal([Pcm(24, 48000, 48000), Pcm(24, 96000, 96000)], Probe(44100, List(size: 112)), 2);
        Assert.Contains("the DAC doesn't take 44.1 kHz (it lists 48 kHz, 96 kHz)", said, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRateListedOnlyWithFewerChannels_SaysThat()
    {
        var said = MacFormats.Refusal([Pcm(24, 44100, 44100, ch: 1)], Probe(44100, List(size: 56)), 2);
        Assert.Contains("the DAC takes 44.1 kHz only with fewer than 2 channels", said, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRefusal_SaysWhereTheDetailsAre()
    {
        foreach (var said in new[]
        {
            MacFormats.Refusal([], Probe(44100, List()), 2),
            MacFormats.Refusal([], Probe(44100, List(sizeStatus: 1)), 2),
            MacFormats.Refusal([], Probe(44100, null, stream: false), 2),
            MacFormats.Refusal([Pcm(24, 48000, 48000)], Probe(44100, List(size: 56)), 2),
        })
            Assert.EndsWith("what Core Audio said is in the log, and mandarin-dac-bridge --diagnose prints it all", said, StringComparison.Ordinal);
    }

    // Core Audio's selectors end in a space where they have three letters: kept, so 'pft ' isn't 'pft'.
    [Fact]
    public void ASelector_IsSpelledAsCoreAudioSpellsIt()
    {
        Assert.Equal("pft ", MacFormats.Selector(MandarinDacBridge.Native.CoreAudio.StreamPhysicalFormat));
        Assert.Equal("pft?", MacFormats.Selector(MandarinDacBridge.Native.CoreAudio.StreamAvailablePhysicalFormats));
        Assert.Equal(MacFormats.ListSelector, MacFormats.Selector(MandarinDacBridge.Native.CoreAudio.StreamAvailablePhysicalFormats));
        Assert.Equal("7", MacFormats.Selector(7));
    }

    // The panel: with no formats listed its rates are only the nominal ones, and it says so.
    [Fact]
    public void ThePanel_OfADacWithNoFormatsListed_SaysTheBridgeCantSetAny()
    {
        var raw = new MacRawDevice("AppleUSBAudioEngine:FiiO:K5 Pro:1", "FiiO K5 Pro", "FiiO", "usb", 2, 44100, -1, null, [(44100, 768000)], []);
        var dac = Assert.Single(Devices.FromMac([raw]));
        Assert.Contains(44100, dac.Rates);
        Assert.Equal([Devices.NoneListed], dac.Formats);

        var listed = raw with { Formats = [Pcm(24, 44100, 768000)] };
        Assert.DoesNotContain(Devices.NoneListed, Assert.Single(Devices.FromMac([listed])).Formats);
    }

    // What is logged and printed by --diagnose: every step of every read, so which one failed is known.
    [Fact]
    public void TheReport_ShowsEveryStepOfEveryRead_AndCoreAudiosCodes()
    {
        var probe = new CoreAudioProbe("FiiO K5 Pro", "macOS 27.0", 44100,
            [
                new CoreAudioRead("nsr#", "available nominal rates", true, 0, 16, 0, ["44100-768000"]),
                new CoreAudioRead("stm#", "output streams", true, 0, 4, 0, ["117"]),
            ],
            [
                new CoreAudioStreamProbe(117,
                [
                    new CoreAudioRead("pft?", "available physical formats", false, 0, 0, 0, []),
                    new CoreAudioRead("pft ", "physical format", true, 0, 40, 2003332927, [], 40),
                    new CoreAudioRead("sfma", "available virtual formats", true, 0, 112, 0, ["lpcm 32-bit float 2 ch 44100-768000", "lpcm 32-bit float 2 ch 44100-768000"]),
                ])
            ]);
        var report = MacFormats.Report(probe);
        Assert.Contains("FiiO K5 Pro", report, StringComparison.Ordinal);
        Assert.Contains("macOS 27.0", report, StringComparison.Ordinal);
        Assert.Contains("44100", report, StringComparison.Ordinal);
        Assert.Contains("stream 117", report, StringComparison.Ordinal);
        Assert.Contains("'pft?' available physical formats: not there", report, StringComparison.Ordinal);
        Assert.Contains("'pft ' physical format: size 40 (0), read failed: 2003332927 ('who?')", report, StringComparison.Ordinal);
        Assert.Contains("'sfma' available virtual formats: size 112 (0), 2 read", report, StringComparison.Ordinal);
        Assert.Contains("lpcm 32-bit float 2 ch 44100-768000", report, StringComparison.Ordinal);
        Assert.Contains("'nsr#' available nominal rates: size 16 (0), 1 read", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ASizeThatFailed_IsShownWithItsCode()
    {
        var report = MacFormats.Report(new CoreAudioProbe("DAC", "macOS 27.0", 0,
            [new CoreAudioRead("stm#", "output streams", true, 560947818, 0, 0, [])], []));
        Assert.Contains("'stm#' output streams: size failed: 560947818 ('!obj')", report, StringComparison.Ordinal);
        Assert.Contains("no output stream", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AFormat_IsDescribedTheSameWayEverywhere()
    {
        Assert.Equal("lpcm 24-bit integer 2 ch 44100-768000", MacFormats.Describe(Pcm(24, 44100, 768000)));
        Assert.Equal("lpcm 32-bit float 8 ch 48000", MacFormats.Describe(Pcm(32, 48000, 48000, ch: 8, isFloat: true)));
    }
}
