// WorkflowTests.cs — the release workflow (.github/workflows/release.yml) and the script that names the
// version it builds (tools/release/version.sh): a release builds the .csproj's version, a tag that names
// another is refused, a build missing stops the release, and no workflow writes an event's or a matrix's
// value into a command.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace MandarinDacBridge.Tests;

public partial class WorkflowTests
{
    private static string Root => ComposeTests.RepoRoot();
    private static string Workflow(string name) => File.ReadAllText(Path.Combine(Root, ".github", "workflows", name));

    // tools/release/version.sh, as release.yml runs it: (exit code, what it printed, what it complained of).
    private static (int Code, string Out, string Err) Version(string tag, string csproj)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { Path.Combine(Root, "tools", "release", "version.sh"), tag, csproj }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result.Trim(), e.Result.Trim());
    }

    private static string Csproj(string version)
    {
        var f = Path.Combine(Directory.CreateTempSubdirectory("mdb-workflow-").FullName, "x.csproj");
        File.WriteAllText(f, $"<Project><PropertyGroup>{version}</PropertyGroup></Project>");
        return f;
    }

    // Before 1.1.19 the version came from the tag alone, and a tag that wasn't vX.Y.Z built "1.0.0".
    [Theory]
    [InlineData("v1.2.3", 0, "1.2.3")]          // the release's tag names the .csproj's version
    [InlineData("", 0, "1.2.3")]                // run by hand: the .csproj's version
    [InlineData("v1.2.4", 1, "")]               // another version: refused
    [InlineData("1.2.3", 1, "")]                // not vX.Y.Z: refused
    [InlineData("release-one", 1, "")]
    public void AReleaseBuildsTheCsprojsVersion_AndATagNamingAnotherIsRefused(string tag, int code, string printed)
    {
        if (OperatingSystem.IsWindows()) return;   // release.yml runs it with bash; the Linux and macOS runs check it
        var (c, o, e) = Version(tag, Csproj("<Version>1.2.3</Version>"));
        Assert.Equal(code, c);
        Assert.Equal(printed, o);
        if (code != 0) Assert.Contains("::error::", e);
    }

    [Fact]
    public void ACsprojWithoutAVersion_IsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(1, Version("", Csproj("<Nothing>1.2.3</Nothing>")).Code);
        Assert.Equal(1, Version("", Csproj("<Version>1.2</Version>")).Code);
    }

    [Fact]
    public void TheRealCsproj_HasAVersionTheScriptReads()
    {
        if (OperatingSystem.IsWindows()) return;
        var (c, o, _) = Version("", Path.Combine(Root, "src", "MandarinDacBridge", "MandarinDacBridge.csproj"));
        Assert.Equal(0, c);
        Assert.StartsWith(o, Config.Version);
    }

    // The release workflow: only the job that attaches the builds may write; every build must be there.
    [Fact]
    public void TheReleaseWorkflow_WritesOnlyToAttach_AndNeverWithABuildMissing()
    {
        var yml = Workflow("release.yml");
        Assert.Matches(new Regex(@"^permissions:\s*\n\s+contents: read\s*$", RegexOptions.Multiline), yml);
        Assert.Single(Regex.Matches(yml, @"contents: write"));
        Assert.Matches(new Regex(@"^  release:(\n    .*)*\n    permissions:\s*\n      contents: write", RegexOptions.Multiline), yml);
        Assert.Contains("bash tools/release/version.sh \"$TAG\"", yml);
        Assert.Contains("if-no-files-found: error", yml);
        Assert.Contains("fail_on_unmatched_files: true", yml);
        Assert.Contains("[ \"$n\" -eq 6 ]", yml);
        foreach (var rid in new[] { "osx-arm64", "osx-x64", "linux-x64", "linux-arm64", "win-x64", "win-arm64" })
            Assert.Contains($"rid: {rid} }}", yml);
    }

    // A value from the event (a tag, a title) written into a command runs as part of it: values reach
    // commands through env. Each workflow's run: lines, single or a block.
    [Theory]
    [InlineData("release.yml")]
    [InlineData("checks.yml")]
    [InlineData("test-image.yml")]
    public void NoWorkflowWritesAnExpressionIntoACommand(string name)
    {
        var found = new List<string>();
        int block = -1;   // the indent of the run: | block being read, or -1
        foreach (var line in Workflow(name).Split('\n'))
        {
            int indent = line.Length - line.TrimStart().Length;
            if (block >= 0 && (line.Trim() == "" || indent > block))
            {
                if (line.Contains("${{")) found.Add(line.Trim());
                continue;
            }
            block = -1;
            var m = RunLine().Match(line);
            if (!m.Success) continue;
            if (m.Groups[2].Value.Trim() is "|" or ">") block = m.Groups[1].Length;
            else if (m.Groups[2].Value.Contains("${{")) found.Add(line.Trim());
        }
        Assert.Empty(found);
    }

    [GeneratedRegex(@"^(\s*)(?:- )?run:(.*)$")] private static partial Regex RunLine();
}
