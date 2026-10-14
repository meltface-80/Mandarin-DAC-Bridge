// QobuzTests.cs — Qobuz Connect through QobuzProxy (Qobuz/): the config the bridge writes for it, each
// speaker's identity, and QobuzProxy installed only as it was pinned (QobuzSource.cs).
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MandarinDacBridge.Qobuz;
using Xunit;

namespace MandarinDacBridge.Tests;

public class QobuzTests
{
    [Fact]
    public void Config_HasASpeakerPerDac_WithAStableUuid()
    {
        var list = new List<Qobuz.QobuzSpeaker>
        {
            new("dac-1", "usb:1", "D90 \"Pro\" (Bridge)", "http://127.0.0.1:55500/upnp/dac-1/description.xml", "127.0.0.1", 55500),
            new("dac-2", "usb:2", "RME (Bridge)", "http://127.0.0.1:55500/upnp/dac-2/description.xml", "127.0.0.1", 55500)
        };
        var yaml = Qobuz.QobuzConnect.Config(list);
        Assert.Contains("name: \"D90 \\\"Pro\\\" (Bridge)\"", yaml);
        Assert.Contains("proxy_port: 7121", yaml);
        Assert.Contains("http_port: 8691", yaml);
        Assert.Equal(Qobuz.QobuzConnect.Uuid("usb:1"), Qobuz.QobuzConnect.Uuid("usb:1"));
        Assert.NotEqual(Qobuz.QobuzConnect.Uuid("usb:1"), Qobuz.QobuzConnect.Uuid("usb:2"));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", Qobuz.QobuzConnect.Uuid("usb:1"));
    }

    [Fact]
    public void ProtocolInfo_SaysTheDacsBest_ForFlac()
    {
        var pi = Upnp.Control.ProtocolInfo([44100, 96000, 192000], [16, 24, 32]);
        Assert.StartsWith("http-get:*:audio/flac:sampleRate=192000;bitsPerSample=32,", pi);
        Assert.Contains("http-get:*:audio/L24;rate=192000;channels=2:*", pi);
    }
}

// QobuzProxy runs as the bridge. Before 1.1.27 pip installed whatever GitHub's archive held, and the newest
// of everything it needs, unchecked.
public sealed class QobuzSourceTests : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("mdb-qp-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch (IOException) { /* left for the system's temp cleaning */ }
    }

    // Zeros, as many as asked, without holding them.
    private sealed class Zeros(long length) : Stream
    {
        private long at;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => at; set => at = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - at);
            Array.Clear(buffer, offset, n);
            at += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => at = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.End ? length + offset : at + offset;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static readonly Dictionary<string, string> Files = new()
    {
        ["pyproject.toml"] = "[project]\nname = \"qobuz-proxy\"\n",
        ["qobuz_proxy/__init__.py"] = "",
        ["qobuz_proxy/main.py"] = "print('hello')\n",
    };

    // An archive of these files under a top folder, as GitHub (or anyone) might pack it.
    private string Pack(Dictionary<string, string> files, string top = "qobuz-proxy-1.7.7", CompressionLevel level = CompressionLevel.Optimal,
                        bool globalHeader = false, Action<TarWriter>? more = null)
    {
        var path = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".tar.gz");
        using (var gz = new GZipStream(File.Create(path), level))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            if (globalHeader) tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "10dedc9d2ac0ff2edcad729e22876d2c7f158c8e" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/"));
            foreach (var (name, text) in files.Reverse())
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{top}/{name}") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            more?.Invoke(tar);
        }
        return path;
    }

    // The files are what is pinned, not how they are packed: the order, the compression, the top folder's
    // name and git's header don't change it.
    [Fact]
    public void TheSameFiles_HowEverPacked_AreTheSameTree()
    {
        var tree = QobuzSource.TreeOf(Pack(Files));
        Assert.Matches("^[0-9a-f]{64}$", tree);
        Assert.Equal(tree, QobuzSource.TreeOf(Pack(Files, "other-1.7.7", CompressionLevel.Fastest, globalHeader: true)));
    }

    [Fact]
    public void AnythingElse_IsAnotherTree_OrNone()
    {
        var tree = QobuzSource.TreeOf(Pack(Files));
        Assert.NotEqual(tree, QobuzSource.TreeOf(Pack(new(Files) { ["qobuz_proxy/main.py"] = "print('hellO')\n" })));   // a byte changed
        Assert.NotEqual(tree, QobuzSource.TreeOf(Pack(new(Files) { ["qobuz_proxy/extra.py"] = "" })));                     // a file added
        var renamed = new Dictionary<string, string>(Files);
        renamed.Remove("qobuz_proxy/main.py");
        renamed["qobuz_proxy/mian.py"] = Files["qobuz_proxy/main.py"];
        Assert.NotEqual(tree, QobuzSource.TreeOf(Pack(renamed)));                                                              // a file renamed
        // Nothing QobuzProxy's archive would hold: a link, a file outside the top folder, one twice.
        Assert.Null(QobuzSource.TreeOf(Pack(Files, more: t => t.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "qobuz-proxy-1.7.7/l") { LinkName = "/etc/passwd" }))));
        Assert.Null(QobuzSource.TreeOf(Pack(Files, more: t => t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "setup.py") { DataStream = new MemoryStream([1]) }))));
        Assert.Null(QobuzSource.TreeOf(Pack(Files, more: t => t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "qobuz-proxy-1.7.7/pyproject.toml") { DataStream = new MemoryStream([1]) }))));
    }

    [Fact]
    public void AnArchiveThatUnpacksTooFar_IsNone() =>
        Assert.Null(QobuzSource.TreeOf(Pack(Files, level: CompressionLevel.Fastest,
            more: t => t.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "qobuz-proxy-1.7.7/big") { DataStream = new Zeros(65L << 20) }))));

    // A page, or a download cut short, where the archive should be: refused like any other, not a crash.
    [Fact]
    public void WhatIsntATarGz_IsNone()
    {
        var page = Path.Combine(temp, "page.tar.gz");
        File.WriteAllText(page, "<html>Rate limited</html>");
        Assert.Null(QobuzSource.TreeOf(page));
        var whole = File.ReadAllBytes(Pack(Files));
        var cut = Path.Combine(temp, "cut.tar.gz");
        File.WriteAllBytes(cut, whole[..(whole.Length / 2)]);
        Assert.Null(QobuzSource.TreeOf(cut));
    }

    // pip's refusals, said so the owner can act on them.
    [Theory]
    [InlineData("ERROR: THESE PACKAGES DO NOT MATCH THE HASHES FROM THE REQUIREMENTS FILE.\n    aiohttp==3.14.1 from ...:\n        Expected sha256 aa\n             Got        bb", "didn't match the hash pinned for it")]
    [InlineData("ERROR: Could not find a version that satisfies the requirement aiohttp==3.14.1\nERROR: No matching distribution found for aiohttp==3.14.1", "no ready-made build for Python 3.15")]
    [InlineData("ERROR: something else\n", "pip: ERROR: something else")]
    public void PipsRefusals_AreSaidPlainly(string output, string said) => Assert.Contains(said, Qobuz.QobuzConnect.PipSays(output, "3.15"));

    [Fact]
    public void AnArchiveThatIsntThePinnedOne_IsRefused()
    {
        var archive = Pack(Files);
        var e = Assert.Throws<InvalidDataException>(() => QobuzSource.Check(archive, "1.7.7"));
        Assert.Contains("isn't what it was when this bridge was made", e.Message);
        QobuzSource.Check(archive, "1.7.7", QobuzSource.TreeOf(archive)!);   // the one pinned: let through
    }

    // Everything pip installs from the lock is pinned to a version and to its files' hashes; what
    // QobuzProxy says it needs, and setuptools to build it, are all there.
    [Fact]
    public void TheLock_PinsEverythingWithHashes()
    {
        var lines = QobuzSource.Lock().ReplaceLineEndings("\n").Split('\n');
        var packages = lines.Where(l => Regex.IsMatch(l, "^[A-Za-z]")).ToList();
        Assert.All(packages, p => Assert.Matches(@"^[a-z0-9._-]+==[0-9][^ ]* (; [^\\]+ )?\\$", p));
        foreach (var block in Regex.Split(string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith('#'))), @"\n(?=[a-z])"))
            if (Regex.IsMatch(block, "^[a-z]")) Assert.Matches(@"--hash=sha256:[0-9a-f]{64}", block);
        var names = packages.Select(p => p[..p.IndexOf("==", StringComparison.Ordinal)]).ToHashSet();
        foreach (var needed in new[] { "websockets", "protobuf", "aiohttp", "zeroconf", "async-upnp-client", "pyyaml", "pyjwt", "setuptools" })
            Assert.Contains(needed, names);
        Assert.Equal(22, names.Count);
        Assert.DoesNotContain(lines, l => l.Contains("qobuz-proxy==", StringComparison.Ordinal));   // QobuzProxy itself comes checked, from its archive
    }
}
