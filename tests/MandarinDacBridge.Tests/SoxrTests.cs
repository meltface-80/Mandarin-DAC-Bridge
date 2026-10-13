// SoxrTests.cs — a track at a rate the DAC doesn't take is resampled with SoX or not at all (Audio/Soxr.cs):
// the ffmpeg with SoX is the one picked, and one without it refuses such a track and says why. The pretend
// ffmpegs are shell scripts, or .cmd files on Windows.
using System.Diagnostics;
using MandarinDacBridge.Audio;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class SoxrTests : IDisposable
{
    private const string WithSox = "  configuration: --enable-gpl --enable-libsoxr --enable-libmp3lame";
    private const string WithoutSox = "  configuration: --enable-gpl --enable-libmp3lame";

    // Nothing in it may contain "soxr": the pretend ffmpegs look for that in their arguments.
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mdb-resampler-" + Guid.NewGuid().ToString("N"));

    private readonly FileServer files;

    // The tones are fetched over http, as every track is (Sources.Open plays nothing else).
    public SoxrTests()
    {
        Directory.CreateDirectory(dir);
        files = new FileServer(dir);
    }

    public void Dispose()
    {
        files.Dispose();
        try { Directory.Delete(dir, true); }
        catch (IOException) { /* a temporary folder; the system clears it */ }
        catch (UnauthorizedAccessException) { /* the same, on Windows while a killed stand-in lets go */ }
    }

    private enum Way { Answers, Hangs, Fails }

    // A pretend ffmpeg in its own folder: answers -buildconf as given, fails when asked for SoX's resampler,
    // and hands anything else to the real one. Written again with the same name, it replaces the last.
    private string Ffmpeg(string name, string buildconf, Way way = Way.Answers)
    {
        var d = Path.Combine(dir, name);
        Directory.CreateDirectory(d);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(d, "ffmpeg.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                way == Way.Hangs ? "ping -n 31 127.0.0.1 >nul" : "",
                way == Way.Fails ? "exit /b 1" : "",
                $"echo %* | findstr /C:\"-buildconf\" >nul && (echo {buildconf}& exit /b 0)",
                "echo %* | findstr /C:\"resampler=soxr\" >nul && (echo Requested resampler soxr is not available 1>&2& exit /b 1)",
                "ffmpeg %*",
                ""));
            return cmd;
        }
        var f = Path.Combine(d, "ffmpeg");
        File.WriteAllText(f, $"""
            #!/bin/sh
            {(way == Way.Hangs ? "sleep 30" : way == Way.Fails ? "exit 1" : "")}
            case "$*" in
              *-buildconf*) echo "{buildconf}"; exit 0 ;;
              *resampler=soxr*) echo "Requested resampler soxr is not available" >&2; exit 1 ;;
            esac
            exec ffmpeg "$@"
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return f;
    }

    private static bool HasRealFfmpeg()
    {
        try { using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true }); p!.WaitForExit(); return p.ExitCode == 0; }
        catch (Exception) { return false; }   // not installed: the tests that need it return early
    }

    private string Tone(int rate)
    {
        var f = Path.Combine(dir, $"tone{rate}.flac");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"sine=frequency=1000:sample_rate={rate}:duration=1", "-ac", "2", f }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return $"{files.Base}/{Path.GetFileName(f)}";
    }

    [Theory]
    [InlineData(WithSox, true)]
    [InlineData(WithoutSox, false)]
    [InlineData("", false)]
    public void WhetherAnFfmpegHasSox_ComesFromItsBuildConfiguration(string buildconf, bool has) =>
        Assert.Equal(has, Soxr.In(buildconf));

    [Fact]
    public void TheFfmpegWithSox_IsPicked_ElseOneThatRuns()
    {
        var without = Ffmpeg("plain", WithoutSox);
        var with = Ffmpeg("full", WithSox);
        var broken = Ffmpeg("broken", WithSox, Way.Fails);
        var missing = Path.Combine(dir, "nowhere", "ffmpeg");

        Assert.Equal(with, Soxr.Pick([without, missing, with]));
        Assert.Equal(with, Soxr.Pick([with, without]));
        Assert.Equal(without, Soxr.Pick([broken, missing, without]));   // none with SoX: one that runs decodes
        Assert.Equal(broken, Soxr.Pick([broken, missing]));             // none answers: the first found
        Assert.Equal("", Soxr.Pick([]));
        Assert.True(Soxr.Has(with));
        Assert.False(Soxr.Has(without));
        Assert.Null(Soxr.Has(missing));
        Assert.Null(Soxr.Has(broken));
        Assert.True(Soxr.Lacks(without));
        Assert.False(Soxr.Lacks(with));
        Assert.False(Soxr.Lacks(broken));                               // unknown is not "no"
    }

    [Fact]
    public void AnFfmpegThatDoesntAnswer_IsUnknown_GivenUpOnInTime_AndAskedAgainLater()
    {
        var slow = Ffmpeg("slow", WithSox, Way.Hangs);

        var clock = Stopwatch.StartNew();
        Assert.Null(Soxr.Has(slow));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12), $"waited {clock.Elapsed}");
        Assert.Contains("didn't answer", Soxr.Describe(slow), StringComparison.Ordinal);

        // Not remembered as "no": once it answers, it is used.
        Assert.Equal(slow, Ffmpeg("slow", WithSox));
        Assert.True(Soxr.Has(slow));
        Assert.Contains("(with SoX)", Soxr.Describe(slow), StringComparison.Ordinal);
    }

    [Fact]
    public void ATrackTheDacCantTake_WithNoSox_IsRefusedAndSaysWhy_NeverResampledAnotherWay()
    {
        if (!HasRealFfmpeg()) return;
        var plain = Ffmpeg("plain", WithoutSox);
        var track = Tone(96000);
        var log = new List<string>();
        var dac = new DacTraits([44100, 48000], 2, "pcm");

        // Not known yet: SoX's resampler is tried, fails, and then the ffmpeg is asked why.
        var e = Assert.Throws<InvalidOperationException>(() => Sources.Open(track, "audio/flac", 0, dac, plain, log.Add, CancellationToken.None));
        Assert.Contains("this track is 96 kHz", e.Message, StringComparison.Ordinal);
        Assert.Contains("no SoX resampler to convert it to 48 kHz", e.Message, StringComparison.Ordinal);
        Assert.Contains("ffmpeg-full", e.Message, StringComparison.Ordinal);
        Assert.Single(log, l => l.Contains("aresample=48000", StringComparison.Ordinal));

        // Known now (as it is from the start for the bridge's own ffmpeg): refused before a resampler is started.
        e = Assert.Throws<InvalidOperationException>(() => Sources.Open(track, "audio/flac", 0, dac, plain, log.Add, CancellationToken.None));
        Assert.Contains("no SoX resampler to convert it to 48 kHz", e.Message, StringComparison.Ordinal);
        Assert.Single(log, l => l.Contains("aresample=48000", StringComparison.Ordinal));
        Assert.Contains("no SoX resampler", Soxr.Describe(plain), StringComparison.Ordinal);

        // A track the DAC takes at its own rate plays with the same ffmpeg, with no resampler at all.
        using var same = Sources.Open(track, "audio/flac", 0, new DacTraits([44100, 48000, 96000], 2, "pcm"), plain, log.Add, CancellationToken.None);
        Assert.Equal(96000, same.Info.Rate);
        Assert.False(same.Info.Resampled);
    }

    [Fact]
    public void ATrackTheDacCantTake_WithSox_IsResampledToTheNearestRateItTakes()
    {
        if (!HasRealFfmpeg()) return;
        // CI installs an ffmpeg with SoX on every system: there, this must really run.
        if (Environment.GetEnvironmentVariable("CI") == "true") Assert.True(Soxr.Has("ffmpeg"), "CI's ffmpeg has no SoX");
        else if (Soxr.Has("ffmpeg") != true) return;
        var log = new List<string>();
        using var s = Sources.Open(Tone(96000), "audio/flac", 0, new DacTraits([44100, 48000], 2, "pcm"), "ffmpeg", log.Add, CancellationToken.None);
        Assert.Equal(48000, s.Info.Rate);
        Assert.True(s.Info.Resampled);
        Assert.Contains(log, l => l.Contains("resampler=soxr", StringComparison.Ordinal));
    }
}
