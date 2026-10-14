// UpnpControlTests.cs — what a UPnP controller sends that the renderer must refuse (Upnp/Control.cs). A
// part of EndToEndTests (dev/quality.md: an area moves out as a part of the same class until the shared
// fixture exists), using its helpers.
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests
{
    // Any device on the network can be a controller: what it asks to play is held to http and https. Before
    // 1.1.20 each of these went to `ffmpeg -i` as given (a file on this machine, ffmpeg's own readers of
    // files, another app's live sound).
    [Theory]
    [InlineData("file:///etc/hostname")]
    [InlineData("/etc/hostname")]
    [InlineData("concat:/etc/hostname|/etc/hostname")]
    [InlineData("subfile:,,start,0,end,0,,:/etc/hostname")]
    [InlineData("data:audio/wav;base64,UklGRg==")]
    [InlineData("live:spotify/dac-0123456789")]
    [InlineData("ftp://192.168.1.1/a.flac")]
    public async Task AStreamThatIsntHttp_IsRefused_AndDoesntTakeTheDac(string uri)
    {
        var id = await Start(Dev("clock"));
        var set = await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = uri, ["CurrentURIMetaData"] = "" }, Aud);
        Assert.Equal(500, set.Status);
        Assert.Contains("<errorCode>716</errorCode>", set.Text);
        var next = await Av(id, "SetNextAVTransportURI", new() { ["NextURI"] = uri, ["NextURIMetaData"] = "" }, Aud);
        Assert.Contains("<errorCode>716</errorCode>", next.Text);
        Assert.Equal("NO_MEDIA_PRESENT", await State(id));
        Assert.Null((await Dac()).GetProperty("control").GetProperty("owner").GetString());
    }

    // A blank stream is refused before it can take the DAC too (it took it, then failed, before 1.1.20); a
    // blank next track still means "no next track".
    [Fact]
    public async Task ABlankStream_DoesntTakeTheDac()
    {
        var id = await Start(Dev("clock"));
        Assert.Contains("<errorCode>714</errorCode>", (await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = "  ", ["CurrentURIMetaData"] = "" }, Aud)).Text);
        Assert.Null((await Dac()).GetProperty("control").GetProperty("owner").GetString());
        Assert.Equal(200, (await Av(id, "SetNextAVTransportURI", new() { ["NextURI"] = "", ["NextURIMetaData"] = "" }, Aud)).Status);
    }
}
