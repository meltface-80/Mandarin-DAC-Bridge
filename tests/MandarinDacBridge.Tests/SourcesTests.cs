// SourcesTests.cs — what is handed to ffmpeg (Sources.cs, StreamUri.cs): a controller's stream is held to
// the network, and ffmpeg reading it to the network's protocols.
using Xunit;

namespace MandarinDacBridge.Tests;

public class SourcesTests
{
    [Theory]
    [InlineData("http://192.168.1.10:9790/a.flac", "http://192.168.1.10:9790/a.flac")]
    [InlineData("  http://h/a.flac  ", "http://h/a.flac")]
    [InlineData("HTTPS://music.example/a%2Fb.flac?sig=AbC%3D", "https://music.example/a%2Fb.flac?sig=AbC%3D")]   // only the scheme changes
    [InlineData("file:///etc/hostname", null)]
    [InlineData("/etc/hostname", null)]
    [InlineData("concat:/etc/hostname", null)]
    [InlineData("subfile:,,start,0,end,0,,:/etc/hostname", null)]
    [InlineData("data:audio/wav;base64,UklGRg==", null)]
    [InlineData("live:mac", null)]
    [InlineData("ftp://h/a.flac", null)]
    [InlineData("http:///no-host", null)]
    [InlineData("https:/\\h/a", null)]
    [InlineData("", null)]
    public void OnlyAStreamOnTheNetworkIsPlayed_AsFfmpegReadsIt(string uri, string? stream) =>
        Assert.Equal(stream, StreamUri.Stream(uri));

    // The one place every way in meets ffmpeg refuses the rest too (a Squeezebox server, a future source).
    [Theory]
    [InlineData("file:///etc/hostname")]
    [InlineData("/etc/hostname")]
    [InlineData("concat:/etc/hostname|/etc/hostname")]
    public void OpeningATrack_RefusesWhatIsntAStream(string uri)
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            Sources.Open(uri, "audio/flac", 0, new DacTraits([44100], 2, "pcm"), "ffmpeg", _ => { }, CancellationToken.None));
        Assert.Contains("only http and https", e.Message, StringComparison.Ordinal);
    }

    // ffmpeg may use only the network's protocols, exactly these: a playlist (HLS) can't send it to a file,
    // and https (TLS), HLS's AES and inline keys still work.
    [Theory]
    [InlineData("http://192.168.1.10/a.flac")]
    [InlineData("https://music.example/a.m3u8")]
    public void Ffmpeg_IsHeldToTheNetworksProtocols(string uri)
    {
        var args = FfmpegSource.Args(uri, 0, null, []);
        int w = args.IndexOf("-protocol_whitelist");
        Assert.InRange(w, 0, args.IndexOf("-i") - 1);
        Assert.Equal(["crypto", "data", "http", "httpproxy", "https", "tcp", "tls"], args[w + 1].Split(',').Order());
    }
}
