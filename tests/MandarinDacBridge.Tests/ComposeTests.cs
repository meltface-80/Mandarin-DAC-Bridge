// The Compose files the owner and users are given: the repository's docker-compose.yml and the one the
// site's command builder writes. Each builds the bridge itself (there is no published image), so each
// service with a build must say pull_policy: build. Without it, `docker compose pull` (what Dockge's
// and Portainer's Update run first) asks Docker Hub for an image named mandarin-dac-bridge, is
// refused, and the update fails; and a plain `up` keeps the image it already has instead of building.
using System.Text.RegularExpressions;
using Xunit;

namespace MandarinDacBridge.Tests;

public class ComposeTests
{
    // The services, under services:, that have a build: but no pull_policy: build. Compose's own layout
    // only (two-space indents, as both files are written); enough for these two files, not a YAML parser.
    internal static List<string> BuiltWithoutPullPolicyBuild(string yaml)
    {
        var missing = new List<string>();
        string? service = null;
        bool inServices = false, build = false, pullBuild = false;
        void Close()
        {
            if (service != null && build && !pullBuild) missing.Add(service);
            service = null; build = pullBuild = false;
        }
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            int indent = line.Length - line.TrimStart().Length;
            if (indent == 0) { Close(); inServices = line.StartsWith("services:", StringComparison.Ordinal); continue; }
            if (!inServices) continue;
            if (indent == 2) { Close(); service = line.Trim().TrimEnd(':'); continue; }
            if (indent != 4 || service == null) continue;
            var key = line.Trim();
            if (key.StartsWith("build:", StringComparison.Ordinal)) build = true;
            if (Regex.IsMatch(key, @"^pull_policy:\s*[""']?build[""']?\s*$")) pullBuild = true;
        }
        Close();
        return missing;
    }

    internal static string RepoRoot() => Root();

    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "docker-compose.yml")) && File.Exists(Path.Combine(d.FullName, "CLAUDE.md")))
                return d.FullName;
        throw new InvalidOperationException("the repository's root wasn't found above " + AppContext.BaseDirectory);
    }

    // The Compose text the site's builder writes: its string literals, from "services:" to the volumes,
    // joined, with the script's \n made new lines. The parts it fills in (time zone, port) aren't needed.
    internal static string SiteCompose(string html)
    {
        int from = html.IndexOf("\"services:", StringComparison.Ordinal);
        int to = html.IndexOf("name: dac-bridge-data", from + 1, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "the site's Compose builder wasn't found in docs/index.html");
        var literals = Regex.Matches(html[from..to], @"""((?:[^""\\]|\\.)*)""").Select(m => m.Groups[1].Value);
        return string.Concat(literals).Replace("\\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepositorysComposeFile_BuildsOnUpdate_AndNeverPullsTheBridge()
    {
        var yaml = File.ReadAllText(Path.Combine(Root(), "docker-compose.yml"));
        Assert.Contains("build:", yaml, StringComparison.Ordinal);
        Assert.Empty(BuiltWithoutPullPolicyBuild(yaml));
    }

    [Fact]
    public void TheSitesComposeBuilder_BuildsOnUpdate_AndNeverPullsTheBridge()
    {
        var yaml = SiteCompose(File.ReadAllText(Path.Combine(Root(), "docs", "index.html")));
        Assert.Contains("dac-bridge:", yaml, StringComparison.Ordinal);
        Assert.Contains("build: ", yaml, StringComparison.Ordinal);
        Assert.Empty(BuiltWithoutPullPolicyBuild(yaml));
    }

    [Fact]
    public void TheCheck_FindsABuildWithoutPullPolicyBuild_AndOnlyThat()
    {
        const string Without = "services:\n  dac-bridge:\n    build: .\n    image: mandarin-dac-bridge\nvolumes:\n  data:\n";
        const string With = "services:\n  dac-bridge:\n    build: .\n    image: mandarin-dac-bridge\n    pull_policy: build\n";
        const string Pulled = "services:\n  other:\n    image: debian\n";
        const string Always = "services:\n  dac-bridge:\n    build: .\n    pull_policy: always\n";
        const string Two = "services:\n  a:\n    build: .\n    pull_policy: build\n  b:\n    build: x\n";
        Assert.Equal(["dac-bridge"], BuiltWithoutPullPolicyBuild(Without));
        Assert.Empty(BuiltWithoutPullPolicyBuild(With));
        Assert.Empty(BuiltWithoutPullPolicyBuild(Pulled));
        Assert.Equal(["dac-bridge"], BuiltWithoutPullPolicyBuild(Always));
        Assert.Equal(["b"], BuiltWithoutPullPolicyBuild(Two));
        Assert.Equal(["dac-bridge"], BuiltWithoutPullPolicyBuild(Without.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    // Before 1.1.29 every Docker command ran the bridge privileged, to open a DAC plugged in after it started
    // (--device hands over only the ones there at the start). A rule allowing every ALSA device (character
    // major 116), with /dev/snd mounted, does that and nothing more; privileged also gave every other device
    // on the host and every capability to the bridge and to the programs it downloads.
    [Fact]
    public void EveryDockerCommand_OpensSoundDevicesOnly_NotPrivileged()
    {
        var root = Root();
        var site = File.ReadAllText(Path.Combine(root, "docs", "index.html")).ReplaceLineEndings("\n");
        var compose = new Dictionary<string, string>
        {
            ["docker-compose.yml"] = File.ReadAllText(Path.Combine(root, "docker-compose.yml")).ReplaceLineEndings("\n"),
            ["the site's Compose builder"] = SiteCompose(site),
        };
        foreach (var (name, yaml) in compose)
        {
            Assert.False(Regex.IsMatch(yaml, @"^\s*privileged:", RegexOptions.Multiline), name + " runs privileged");
            Assert.True(Regex.IsMatch(yaml, @"device_cgroup_rules:\n\s+- ['""]c 116:\* rmw['""]"), name + " has no rule for the sound devices");
            Assert.Contains("- /dev/snd:/dev/snd", yaml, StringComparison.Ordinal);
            Assert.Contains("- SYS_NICE", yaml, StringComparison.Ordinal);
        }
        // The command itself: a line of it, or the site's builder writing one (not prose about it).
        const string Rule = @"--device-cgroup-rule 'c 116:\* rmw'";
        var run = new Dictionary<string, (string Text, string Rule)>
        {
            ["README.md"] = (File.ReadAllText(Path.Combine(root, "README.md")), @"^[ \t]*" + Rule),
            ["dev/release.md"] = (File.ReadAllText(Path.Combine(root, "dev", "release.md")), @"^[ \t]*" + Rule),
            ["the site's docker run builder"] = (site, @"K\(""--device-cgroup-rule""\) \+ "" 'c 116:\* rmw'"),
        };
        foreach (var (name, (text, rule)) in run)
        {
            Assert.False(Regex.IsMatch(text, @"(^[ \t]*|--device[^\n]*)--privileged\b|K\(""--privileged""\)", RegexOptions.Multiline), name + " runs privileged");
            Assert.True(Regex.IsMatch(text, rule, RegexOptions.Multiline), name + " has no rule for the sound devices");
        }
    }
}
