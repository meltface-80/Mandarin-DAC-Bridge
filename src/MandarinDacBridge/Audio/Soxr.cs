// Soxr.cs — SoX, the one resampler the bridge uses (CLAUDE.md, bit-perfect: a track is resampled only when
// the DAC can't take its rate, then with SoX). ffmpeg resamples with SoX only when it was built with libsoxr,
// and many aren't: Homebrew's plain ffmpeg and Chocolatey's ffmpeg have none, while Homebrew's ffmpeg-full, winget's
// Gyan.FFmpeg, Chocolatey's ffmpeg-full, Debian's and Ubuntu's (the Docker image's) have it. So: whether an ffmpeg has it, asked of the
// ffmpeg itself (-buildconf) once per program; and, where several are installed, the one that has it. A track
// that needs resampling with an ffmpeg that has none is refused, and says why (Sources.Open), never
// resampled another way. (Chocolatey's ffmpeg package is Gyan's essentials build, without SoX; its
// ffmpeg-full package has it.)
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MandarinDacBridge.Audio;

internal static class Soxr
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    // Answers only: an ffmpeg that couldn't be asked is asked again next time.
    private static readonly ConcurrentDictionary<string, bool> Known = new();

    internal static bool In(string buildconf) => buildconf.Contains("--enable-libsoxr", StringComparison.Ordinal);

    // Whether this ffmpeg has SoX; null when it couldn't be asked (not there, failed, or didn't answer).
    public static bool? Has(string ffmpeg)
    {
        if (Known.TryGetValue(ffmpeg, out var known)) return known;
        if (Ask(ffmpeg) is not { } buildconf) return null;
        return Known[ffmpeg] = In(buildconf);
    }

    // Of the ffmpegs found, in order, the first with SoX; else the first that answered (it still decodes
    // every track the DAC takes at its own rate), over one that couldn't be asked (it may not run at all);
    // else the first found; "" when none was.
    public static string Pick(IEnumerable<string> found)
    {
        string first = "", answered = "";
        foreach (var f in found.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (first == "") first = f;
            var has = Has(f);
            if (has == true) return f;
            if (has == false && answered == "") answered = f;
        }
        return answered != "" ? answered : first;
    }

    // Known, from asking it before (at start), to have no SoX. Asks nothing: safe on the play path.
    public static bool Lacks(string ffmpeg) => Known.TryGetValue(ffmpeg, out var has) && !has;

    // What the log says at start, so a missing SoX is known before a track needs it.
    public static string Describe(string ffmpeg) => Has(ffmpeg) switch
    {
        true => $"ffmpeg: {ffmpeg} (with SoX)",
        false => $"ffmpeg: {ffmpeg} has no SoX resampler: a track at a rate the DAC can't take will be refused. {Remedy}",
        null => $"ffmpeg: {ffmpeg} didn't answer: is it installed?",
    };

    public static string Refusal(string ffmpeg, int rate, int want) =>
        $"this track is {Devices.KHz(rate)}, which the DAC doesn't take, and this ffmpeg ({ffmpeg}) has no SoX resampler " +
        $"to convert it to {Devices.KHz(want)}, the only one the bridge uses. {Remedy}";

    private const string Remedy =
        "Install an ffmpeg built with SoX (libsoxr): on a Mac, run the installer again (it adds Homebrew's ffmpeg-full); " +
        "on Windows, winget install Gyan.FFmpeg or choco install ffmpeg-full; or set FFMPEG to one.";

    // ffmpeg -buildconf's output, or null when it can't be run, fails, or hasn't answered within Patience
    // (then it is ended, with whatever it started).
    private static string? Ask(string ffmpeg)
    {
        Process? p = null;
        var clock = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-buildconf");
            p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Patience - clock.Elapsed)) return null;
            var left = Patience - clock.Elapsed;
            if (!Task.WaitAll([output, errors], left > TimeSpan.Zero ? left : TimeSpan.Zero)) return null;
            return p.ExitCode == 0 ? output.Result + errors.Result : null;
        }
        catch (Exception)
        {
            // Not there, not runnable, or its output unreadable: it can't be asked, which Has reports as null.
            return null;
        }
        finally
        {
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch (Exception) { /* it ended on its own meanwhile, or can't be ended: nothing more to do with it */ }
                p.Dispose();
            }
        }
    }
}
