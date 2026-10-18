// QobuzSource.cs — QobuzProxy installed only as it was when the bridge was made (QobuzConnect.cs installs
// it on request).
//
// QobuzProxy runs as the bridge (as root, in Docker). Before 1.1.27, pip installed whatever GitHub's
// archive of the tag held, with the newest versions of everything it needs, unchecked. Now:
// - its dependencies, and setuptools to build it, come from QobuzProxy.lock: QobuzProxy's own uv.lock at
//   the tag, exported with the SHA-256 of every file PyPI has for each (pip --require-hashes installs
//   nothing else);
// - the archive is fetched by the bridge (Download.ToFile, which logs its SHA-256) and its files are
//   checked against Tree: the SHA-256 of their paths and contents, as `git archive` of the tag gives
//   them. Not the archive's own bytes: GitHub doesn't promise to compress it the same way for ever; the
//   files are the tag's;
// - then it is built and installed from that file, with nothing else fetched.
// QOBUZPROXY_SOURCE (a local copy, a fork) is installed as given, unchecked: the owner chose it.
using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MandarinDacBridge.Qobuz;

internal static class QobuzSource
{
    // The v1.7.7 tag (for the record: the lock says how it was made).
    public const string Commit = "10dedc9d2ac0ff2edcad729e22876d2c7f158c8e";
    public const string Tree = "b5ff63e02c69fe12ea90d522082c266acee235cc0a24c5a035924f371812cde1";
    // An archive past these isn't QobuzProxy's (it is well under 1 MiB, packed or not).
    public const long MaxArchive = 16L << 20;
    private const long MaxUnpacked = 64L << 20;

    public static string Url(string version) => $"https://github.com/leolobato/qobuz-proxy/archive/refs/tags/v{version}.tar.gz";

    // The lock, as the program carries it.
    public static string Lock()
    {
        using var s = typeof(QobuzSource).Assembly.GetManifestResourceStream("Qobuz/QobuzProxy.lock")
                      ?? throw new InvalidOperationException("the program carries no QobuzProxy.lock");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // Throws unless the archive holds the tag's files (tree: Tree, but for the tests).
    public static void Check(string archive, string version, string tree = Tree)
    {
        if (TreeOf(archive) != tree)
            throw new InvalidDataException($"GitHub's QobuzProxy {version} isn't what it was when this bridge was made: not installed");
    }

    // The SHA-256 of a .tar.gz's files: over each one's path (without the archive's top folder), its length
    // and its own SHA-256, in path order. Null for anything QobuzProxy's archive wouldn't be: not a .tar.gz,
    // a link or a device in it, a file outside the top folder or twice in it, or more than MaxUnpacked.
    public static string? TreeOf(string archive)
    {
        var files = new SortedDictionary<string, (long Length, byte[] Sha)>(StringComparer.Ordinal);
        long total = 0;
        try
        {
            using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            while (tar.GetNextEntry() is { } e)
            {
                if (e.EntryType is TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes) continue;
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) return null;
                int slash = e.Name.IndexOf('/', StringComparison.Ordinal);
                if (slash <= 0 || slash == e.Name.Length - 1) return null;
                total += e.Length;
                if (total > MaxUnpacked) return null;
                var sha = e.DataStream != null ? SHA256.HashData(e.DataStream) : SHA256.HashData([]);
                if (!files.TryAdd(e.Name[(slash + 1)..], (e.Length, sha))) return null;
            }
        }
        catch (Exception x) when (x is InvalidDataException or FormatException or EndOfStreamException) { return null; }
        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (name, (size, sha)) in files)
        {
            tree.AppendData(Encoding.UTF8.GetBytes(name));
            tree.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(length, size);
            tree.AppendData(length);
            tree.AppendData(sha);
        }
        return Convert.ToHexStringLower(tree.GetHashAndReset());
    }
}
