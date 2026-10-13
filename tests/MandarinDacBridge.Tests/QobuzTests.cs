// QobuzTests.cs — Qobuz Connect through QobuzProxy (Qobuz/): the config the bridge writes for it, each
// speaker's identity.
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
