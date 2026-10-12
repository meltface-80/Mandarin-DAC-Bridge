// OwnerOnlyFile.cs — the files that hold secrets (settings.json with the Soloist key and the Plex
// token; roon-api.json with Roon's tokens): their owner's only from the moment they exist, written
// whole or not at all, and never replaced unread when they can't be read.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MandarinDacBridge;

internal static class OwnerOnlyFile
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // Written to a new file beside it, created owner-only, then moved into place: a crash midway
    // leaves the old file whole, and there is no moment when others can read the secrets. (On Windows
    // the folder's permissions decide, as for every file in %LOCALAPPDATA%.)
    public static void Write(string path, string text)
    {
        // Its own name, so two writers never share (or delete) each other's.
        var next = $"{path}.{Guid.NewGuid():N}.new";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerReadWrite;
        try
        {
            using (var fs = new FileStream(next, options))
            {
                fs.Write(Encoding.UTF8.GetBytes(text));
                fs.Flush(flushToDisk: true);
            }
            File.Move(next, path, overwrite: true);
        }
        finally { File.Delete(next); }
    }

    // The file's text, or null when it hasn't been written yet.
    public static string? Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }    // not written yet: nothing to keep
    }

    // A file that is there but isn't what it should be (cut short, edited by hand) is moved aside under a
    // name that says so, and why is logged: starting from defaults must not overwrite what was in it.
    // False when it couldn't be moved: then nothing may write over it.
    public static bool KeepAside(string path, string why)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var aside = $"{path}.unreadable-{stamp}-{Guid.NewGuid().ToString("N")[..6]}";
        try
        {
            File.Move(path, aside);
            Log.Write($"{Path.GetFileName(path)} couldn't be read ({why}); kept as {Path.GetFileName(aside)}, starting afresh");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"{Path.GetFileName(path)} couldn't be read ({why}), nor kept aside ({e.Message}); it won't be written over");
            return false;
        }
    }

    // A file of names and strings ({"core-1":"token"}): empty when it isn't there, or wasn't what it should
    // be and has been kept aside. Throws when it can't be opened or kept aside, so nobody writes over it.
    public static Dictionary<string, string> ReadStrings(string path)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Read(path) is not { } text) return all;
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) all[p.Name] = p.Value.GetString()!;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            if (!KeepAside(path, e.Message)) throw new IOException($"{Path.GetFileName(path)} couldn't be read or kept aside", e);
            all.Clear();
        }
        return all;
    }

    public static void WriteStrings(string path, Dictionary<string, string> all)
    {
        var o = new JsonObject();
        foreach (var (k, v) in all) o[k] = v;
        Write(path, o.ToJsonString());
    }
}
