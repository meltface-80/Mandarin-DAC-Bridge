// CalderaDownload.cs — Caldera Headless (caldera-music), fetched from Caldera
// on request, as its own install.sh does: the release for this processor from
// releases.caldera.homes, unpacked into DATA_DIR/caldera/caldera-music. The
// bridge runs it with HOME set to DATA_DIR/caldera, so Caldera's self-update
// (its upgrade.sh, which works on $HOME/caldera-music) updates this copy.
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MandarinDacBridge.Caldera;

internal static class CalderaDownload
{
    public const string Base = "https://releases.caldera.homes/music/headless";

    // Caldera's name for this processor's build, or null (none is made for it).
    public static string? Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        Architecture.Arm => "armv7",
        _ => null
    };

    public static string Home(string dataDir) => Path.Combine(dataDir, "caldera");
    public static string InstallDir(string dataDir) => Path.Combine(Home(dataDir), "caldera-music");
    public static string Program(string dataDir) => Path.Combine(InstallDir(dataDir), "bin", "caldera-music");

    // The installed version ("1.1.0"), or "" if it isn't installed.
    public static string Version(string dataDir)
    {
        try { return File.Exists(Program(dataDir)) ? File.ReadAllText(Path.Combine(InstallDir(dataDir), "VERSION")).Trim() : ""; }
        catch (Exception) { return ""; }
    }

    public static async Task<string> Fetch(string dataDir, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Caldera Headless runs on Linux only");
        var arch = Arch ?? throw new PlatformNotSupportedException($"Caldera makes no Headless build for {RuntimeInformation.ProcessArchitecture}");
        var target = InstallDir(dataDir);
        var tmp = target + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        using (var res = await Sources.Http.GetAsync($"{Base}/latest/caldera-music-linux-{arch}.tar.gz", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            await using var gz = new GZipStream(body, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, tmp, overwriteFiles: true, ct);
        }
        if (!File.Exists(Path.Combine(tmp, "bin", "caldera-music"))) throw new InvalidDataException("Caldera's archive has no caldera-music program in it");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(tmp, target);
        return Version(dataDir);
    }

    // The newest version Caldera publishes ("1.1.0"), or "" if it can't be read.
    public static async Task<string> Latest(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Sources.Http.GetStringAsync($"{Base}/latest/latest.json", ct));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception) { return ""; }
    }
}
