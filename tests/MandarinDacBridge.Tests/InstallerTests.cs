// InstallerTests.cs — the installers fetch the latest release's build and its SHA256SUMS from that one
// release (release.yml makes the sums), and check one against the other: a build that differs, or that
// the sums don't list, is refused. Each installer's own functions are run here, taken from the script as
// it is (tools/mac/install.sh: latest_tag, fetch, sum_check; tools/windows/install.ps1: Get-LatestTag,
// Get-File, Test-Sum), against a pretend GitHub on a local port.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly HttpListener github = new();
    private readonly int port = Free();

    public InstallerTests()
    {
        github.Prefixes.Add($"http://127.0.0.1:{port}/");
        github.Start();
        _ = Task.Run(async () =>
        {
            while (github.IsListening)
            {
                HttpListenerContext c;
                try { c = await github.GetContextAsync(); } catch (Exception) { return; }   // stopped
                var at = c.Request.Url!.AbsolutePath;
                if (at.EndsWith("/releases/latest", StringComparison.Ordinal))
                {
                    c.Response.StatusCode = 302;
                    c.Response.RedirectLocation = at.StartsWith("/none/", StringComparison.Ordinal)
                        ? $"http://127.0.0.1:{port}/none/releases" : $"http://127.0.0.1:{port}/repo/releases/tag/v1.2.3";
                }
                else c.Response.StatusCode = at switch { "/file" or "/repo/releases/tag/v1.2.3" or "/none/releases" => 200, "/broken" => 500, _ => 404 };
                if (at == "/file") c.Response.OutputStream.Write("sums"u8);
                c.Response.Close();
            }
        });
    }

    public void Dispose() => github.Close();

    private static int Free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string Root => ComposeTests.RepoRoot();
    private static string Script(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path])).ReplaceLineEndings("\n");

    // A build, and a SHA256SUMS as sha256sum writes it: (the build's path, the sums' path).
    private static (string File, string Sums) Release(string sumsLine)
    {
        var dir = Directory.CreateTempSubdirectory("mdb-sums-").FullName;
        var file = Path.Combine(dir, "bridge.tar.gz");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
        var sums = Path.Combine(dir, "SHA256SUMS");
        File.WriteAllText(sums, sumsLine.Replace("HASH", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))));
        return (file, sums);
    }

    private const string Name = "mandarin-dac-bridge-osx-arm64.tar.gz";

    public static TheoryData<string, int> Cases => new()
    {
        { $"HASH  {Name}\n", 0 },                                                             // matches
        { $"0000000000000000000000000000000000000000000000000000000000000000  {Name}\n", 1 },  // differs: refused
        { $"HASH  mandarin-dac-bridge-osx-x64.tar.gz\nHASH  mandarin-dac-bridge-win-x64.zip\n", 1 },  // not listed: refused
        { "", 1 },
        { $"HASH *{Name}\n", 0 },                                                             // sha256sum's binary mark
    };

    private static string Bash(string function) =>
        Regex.Match(Script("tools", "mac", "install.sh"), $@"^{function}\(\) \{{.*?^\}}", RegexOptions.Multiline | RegexOptions.Singleline).Value;

    private static string Pwsh(string function) =>
        Regex.Match(Script("tools", "windows", "install.ps1"), $@"^function {function}\(.*?^\}}", RegexOptions.Multiline | RegexOptions.Singleline).Value;

    // Runs a bash function from install.sh: (exit code, what it printed).
    private static (int Code, string Out) RunBash(string function, params string[] args)
    {
        var body = Bash(function);
        Assert.NotEqual("", body);
        return RunOut("bash", ["-c", body + $"\n{function} \"$@\"", "bash", .. args]);
    }

    // Runs a PowerShell function from install.ps1: (exit code: 0, or 9 when it threw; what it returned).
    private static (int Code, string Out) RunPwsh(string function, params string[] args)
    {
        var body = Pwsh(function);
        Assert.NotEqual("", body);
        var call = function + " " + string.Join(" ", args.Select(a => $"'{a}'"));
        return RunOut("pwsh", ["-NoProfile", "-NonInteractive", "-Command", body + $"\ntry {{ {call} }} catch {{ exit 9 }}"]);
    }

    private static (int Code, string Out) RunOut(string program, string[] args)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, o.Result.Trim());
    }

    // The latest release's tag from where releases/latest leads; none when there's no release; GitHub out
    // of reach is a failure, never "no release" (which would build main from the source).
    [Fact]
    public void TheInstallers_FindTheLatestReleasesTag()
    {
        var gone = $"http://127.0.0.1:{Free()}";
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal((0, "v1.2.3"), RunBash("latest_tag", $"http://127.0.0.1:{port}/repo"));
            Assert.Equal((0, ""), RunBash("latest_tag", $"http://127.0.0.1:{port}/none"));
            Assert.NotEqual(0, RunBash("latest_tag", gone).Code);
        }
        if (Has("pwsh"))
        {
            Assert.Equal((0, "v1.2.3"), RunPwsh("Get-LatestTag", $"http://127.0.0.1:{port}/repo"));
            Assert.Equal((0, ""), RunPwsh("Get-LatestTag", $"http://127.0.0.1:{port}/none"));
            Assert.Equal(9, RunPwsh("Get-LatestTag", gone).Code);
        }
    }

    // A file that is there, one that isn't (an older release's SHA256SUMS), and a failure, told apart.
    [Fact]
    public void TheInstallers_TellAMissingFile_FromAFailedDownload()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("mdb-fetch-").FullName, "f");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal((0, "200"), RunBash("fetch", $"http://127.0.0.1:{port}/file", file));
            Assert.Equal("sums", File.ReadAllText(file));
            Assert.Equal((0, "404"), RunBash("fetch", $"http://127.0.0.1:{port}/SHA256SUMS", file));
            Assert.NotEqual(0, RunBash("fetch", $"http://127.0.0.1:{port}/broken", file).Code);
            Assert.NotEqual(0, RunBash("fetch", $"http://127.0.0.1:{Free()}/file", file).Code);
        }
        if (Has("pwsh"))
        {
            Assert.Equal((0, "200"), RunPwsh("Get-File", $"http://127.0.0.1:{port}/file", file));
            Assert.Equal((0, "404"), RunPwsh("Get-File", $"http://127.0.0.1:{port}/SHA256SUMS", file));
            Assert.Equal(9, RunPwsh("Get-File", $"http://127.0.0.1:{port}/broken", file).Code);
            Assert.Equal(9, RunPwsh("Get-File", $"http://127.0.0.1:{Free()}/file", file).Code);
        }
    }

    // A build whose SHA-256 can't be worked out is neither a match nor a mismatch: the Mac's says so (3).
    [Fact]
    public void ABuildThatCantBeRead_IsntCalledAMatch()
    {
        var (_, sums) = Release($"HASH  {Name}\n");
        var missing = Path.Combine(Path.GetTempPath(), "mdb-no-such-build");
        if (!OperatingSystem.IsWindows()) Assert.Equal(3, RunBash("sum_check", missing, Name, sums).Code);
        if (Has("pwsh")) Assert.Equal(9, RunPwsh("Test-Sum", missing, Name, sums).Code);
    }

    private static bool Has(string program)
    {
        try { return RunOut(program, ["-c", "exit 0"]).Code == 0; }
        catch (Exception) { return false; }   // not installed here
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheMacInstaller_ChecksTheBuildAgainstTheReleasesSums(string sumsLine, int expected)
    {
        if (OperatingSystem.IsWindows()) return;   // a bash script: the Linux and macOS runs check it
        var (file, sums) = Release(sumsLine);
        Assert.Equal(expected, RunBash("sum_check", file, Name, sums).Code);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheWindowsInstaller_ChecksTheBuildAgainstTheReleasesSums(string sumsLine, int expected)
    {
        if (!Has("pwsh")) return;   // PowerShell is on the Windows and Linux runners
        var (file, sums) = Release(sumsLine);
        Assert.Equal((0, expected.ToString(System.Globalization.CultureInfo.InvariantCulture)), RunPwsh("Test-Sum", file, Name, sums));
    }

    // The release attaches the sums of the six builds the installers download.
    [Fact]
    public void TheReleaseAttachesTheSums_OfTheSixBuilds()
    {
        var yml = Script(".github", "workflows", "release.yml");
        Assert.Contains("sha256sum mandarin-dac-bridge-*.tar.gz mandarin-dac-bridge-*.zip > SHA256SUMS", yml);
        Assert.Contains("[ \"$(wc -l < SHA256SUMS)\" -eq 6 ]", yml);
        Assert.Matches(new Regex(@"files: \|\n(\s+mandarin-dac-bridge-\*\.\w+(\.gz)?\n)+\s+SHA256SUMS\n"), yml);
        // And the installers fetch it from the same release as the build: the tag found once.
        var mac = Script("tools", "mac", "install.sh");
        Assert.Contains("AT=\"https://github.com/$OWNER_REPO/releases/download/$TAG\"", mac);
        Assert.Contains("fetch \"$AT/$NAME\"", mac);
        Assert.Contains("fetch \"$AT/SHA256SUMS\"", mac);
        Assert.DoesNotContain("releases/latest/download", mac);
        var win = Script("tools", "windows", "install.ps1");
        Assert.Contains("$at = \"https://github.com/$OwnerRepo/releases/download/$tag\"", win);
        Assert.Contains("Get-File \"$at/$name\"", win);
        Assert.Contains("Get-File \"$at/SHA256SUMS\"", win);
        Assert.DoesNotContain("releases/latest/download", win);
    }
}
