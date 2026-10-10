// SoloistDownload.cs — Spotify Soloist, fetched from Spotify on request.
//
// Soloist may not be redistributed, so the bridge doesn't carry it: the
// page's Download button (and an expired build, if the bridge downloaded it)
// fetches the official archive for this machine's processor from Spotify's
// download page, and keeps the soloist program in DATA_DIR/soloist. Builds
// last 90 days; downloading again replaces it.
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MandarinDacBridge.Spotify;

internal static class SoloistDownload
{
    public const string Page = "https://developer.spotify.com/documentation/soloist/reference/downloads-and-updates";

    // Spotify's name for this processor's build, or null (Soloist isn't made for it).
    public static string? Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm32",
        _ => null
    };

    public static string Url(string arch) => $"https://soloist-builds.spotifycdn.com/soloist_release_{arch}.tar.gz";

    // Downloads and unpacks the soloist program into dir; returns its path.
    public static async Task<string> Fetch(string dir, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Spotify Soloist runs on Linux only");
        var arch = Arch ?? throw new PlatformNotSupportedException($"Spotify makes no Soloist for {RuntimeInformation.ProcessArchitecture}");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "soloist");
        var tmp = target + ".new";
        using (var res = await Sources.Http.GetAsync(Url(arch), HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            await using var gz = new GZipStream(body, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            bool found = false;
            while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } e)
            {
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                var name = Path.GetFileName(e.Name);
                if (name == "soloist") { await e.ExtractToFileAsync(tmp, overwrite: true, ct); found = true; }
                else if (name is "CHANGELOG.md" or "THIRD_PARTY_LICENSES.txt") await e.ExtractToFileAsync(Path.Combine(dir, name), overwrite: true, ct);
            }
            if (!found) throw new InvalidDataException("Spotify's archive has no soloist program in it");
        }
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                  | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    // The loader's complaint, said plainly ("" when there's none).
    public static string Explain(string line)
    {
        if (line.Contains("GLIBC_")) return "Soloist needs a newer Linux (glibc 2.38 or later, e.g. Debian 13), or the bridge in Docker";
        var m = System.Text.RegularExpressions.Regex.Match(line, @"error while loading shared libraries: ([^:\s]+)");
        if (!m.Success) return line;
        var lib = m.Groups[1].Value;
        var package = lib.StartsWith("libatomic") ? "libatomic1" : lib.Split(".so")[0];
        return $"Soloist needs {lib}: sudo apt install {package}";
    }

    // A build lasts 90 days from its date: "soloist 1.3.9.7 build … (20261010) …" → 8 January 2027.
    public static DateOnly? Expires(string version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(version, @"\((\d{4})(\d{2})(\d{2})\)");
        if (!m.Success) return null;
        try { return new DateOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)).AddDays(90); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static readonly Dictionary<string, (DateTime Stamp, string Version)> versions = new();

    // "soloist 1.3.9 …" as the program says it (cached until the file changes); "" if it won't say.
    public static string Version(string path)
    {
        if (path == "" || !File.Exists(path)) return "";
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (versions) if (versions.TryGetValue(path, out var v) && v.Stamp == stamp) return v.Version;
        var text = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p != null)
            {
                var read = p.StandardOutput.ReadToEndAsync();
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(3000)) p.Kill();
                text = (read.Result + "\n" + err.Result).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l != "") ?? "";
                text = Explain(text);
            }
        }
        catch (Exception) { text = ""; }
        if (text.Length > 120) text = text[..120];
        lock (versions) versions[path] = (stamp, text);
        return text;
    }
}
