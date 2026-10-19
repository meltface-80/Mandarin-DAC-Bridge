// MacInstallerTests.cs — what the Mac installer's tidy-up of an earlier install removes (tools/mac/install.sh,
// dev/design/install.md, decision 3): installers before the bridge had its own ffmpeg added Homebrew's
// ffmpeg-full and about a hundred packages with it. Which of them go is worked out by the installer's own
// JavaScript (run by osascript, which every Mac has) from Homebrew's record of what is installed; here it
// is given made-up records, so nothing is installed or removed and nothing is fetched. macOS only (where
// osascript is); the run against the runner's real Homebrew is tests/mac/tidy-on-homebrew.sh, in CI.
using System.Diagnostics;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class MacInstallerTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mdb-mac-tidy-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { /* a temp folder left behind is harmless */ }
    }

    // The script as the installer writes it: from its first line to the heredoc's end.
    private static string Script()
    {
        var lines = File.ReadAllLines(Path.Combine(ComposeTests.RepoRoot(), "tools", "mac", "install.sh"));
        int start = Array.IndexOf(lines, "ObjC.import('Foundation');");
        int end = start < 0 ? -1 : Array.IndexOf(lines, "JS", start);
        Assert.True(start >= 0 && end > start, "the tidy-up's script isn't in install.sh");
        return string.Join('\n', lines[start..end]);
    }

    // A formula: its name, whether it was asked for, what it needs.
    private static string F(string name, bool request, params string[] deps) =>
        $$"""{"name":"{{name}}","installed":[{"installed_on_request":{{(request ? "true" : "false")}},"runtime_dependencies":[{{string.Join(",", deps.Select(d => $$"""{"full_name":"{{d}}"}"""))}}]}]}""";

    // What the script says should go, one name each, given Homebrew's record of these formulae and casks.
    private string[] Removable(string[] formulae, string casks = "")
    {
        var js = Path.Combine(dir, "tree.js");
        var json = Path.Combine(dir, "installed.json");
        File.WriteAllText(js, Script());
        File.WriteAllText(json, $$"""{"formulae":[{{string.Join(",", formulae)}}],"casks":[{{casks}}]}""");
        var psi = new ProcessStartInfo("osascript") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-l", "JavaScript", js, json, "ffmpeg-full" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, e.Result);
        return o.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // An earlier install beside the owner's own: Mandarin's ffmpeg, git, lame asked for.
    private static readonly string[] Mac =
    [
        F("ffmpeg-full", true, "lame", "tesseract", "gettext", "x265", "libpng"),
        F("lame", true),
        F("tesseract", false, "leptonica"),
        F("leptonica", false, "libpng", "giflib"),
        F("giflib", false),
        F("libpng", false),
        F("gettext", false),
        F("x265", false),
        F("git", true, "gettext"),
        F("ffmpeg", true, "x265"),
    ];

    [Fact]
    public void FfmpegFullGoes_WithWhatOnlyItNeeded_AndNothingAskedForOrNeededElsewhere()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(["ffmpeg-full", "giflib", "leptonica", "libpng", "tesseract"], Removable(Mac).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void APackageAnAppNeeds_Stays()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var gone = Removable(Mac, """{"token":"someapp","depends_on":{"formula":["libpng"]}}""");
        Assert.DoesNotContain("libpng", gone);
        Assert.Contains("giflib", gone);
    }

    [Fact]
    public void FfmpegFullNeededByAnything_StaysWithAll_AndSaysWhat()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(["NEEDED somethingelse"], Removable([.. Mac, F("somethingelse", true, "ffmpeg-full")]));
        Assert.Equal(["NEEDED the app someapp"], Removable(Mac, """{"token":"someapp","depends_on":{"formula":["ffmpeg-full"]}}"""));
    }

    [Fact]
    public void NoFfmpegFull_NothingGoes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Empty(Removable([F("lame", true), F("git", true)]));
    }
}
