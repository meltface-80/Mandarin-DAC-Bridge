// SameOriginTests.cs — from a browser, only the bridge's own page may change anything (SameOrigin.cs).
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MandarinDacBridge.Tests;

public class SameOriginTests
{
    [Theory]
    // What browsers say (Sec-Fetch-Site) decides when they say it.
    [InlineData("POST", "cross-site", "http://evil.example", "192.168.1.5:55500", false)]
    [InlineData("POST", "same-site", "http://192.168.1.5:8080", "192.168.1.5:55500", false)]
    [InlineData("POST", "same-origin", "http://192.168.1.5:55500", "192.168.1.5:55500", true)]
    [InlineData("POST", "none", "", "192.168.1.5:55500", true)]
    // Older browsers say only Origin: it must be this host and port.
    [InlineData("POST", "", "http://evil.example", "192.168.1.5:55500", false)]
    [InlineData("POST", "", "http://192.168.1.5:55500", "192.168.1.5:55500", true)]
    [InlineData("POST", "", "http://DAC-BRIDGE.local:55500", "dac-bridge.local:55500", true)]
    [InlineData("POST", "", "http://[::1]:55500", "[::1]:55500", true)]
    [InlineData("POST", "", "http://box", "box", true)]
    [InlineData("POST", "", "http://box:8080", "box", false)]
    [InlineData("POST", "", "null", "box:55500", false)]
    // Not a browser (a UPnP controller, a server, curl): nothing to go on, and not what this closes.
    [InlineData("POST", "", "", "192.168.1.5:55500", true)]
    [InlineData("SUBSCRIBE", "cross-site", "", "192.168.1.5:55500", false)]
    // Reading changes nothing.
    [InlineData("GET", "cross-site", "http://evil.example", "192.168.1.5:55500", true)]
    [InlineData("HEAD", "cross-site", "http://evil.example", "192.168.1.5:55500", true)]
    public void OnlyTheBridgesOwnPageMayChangeThings(string method, string fetchSite, string origin, string host, bool allowed) =>
        Assert.Equal(allowed, SameOrigin.Refusal(method, fetchSite, origin, host, Names) == null);

    private static readonly string[] Names = ["dac-bridge", "box", "", "music.home.arpa"];

    [Theory]
    // Another site's name pointed at the bridge (DNS rebinding): the browser calls it same-origin. Refused.
    [InlineData("same-origin", "", "evil.example:55500", false)]
    [InlineData("", "http://evil.example:55500", "evil.example:55500", false)]
    [InlineData("same-origin", "", "dac-bridge.evil.example:55500", false)]
    // This machine's names, addresses and localhost.
    [InlineData("same-origin", "", "dac-bridge:55500", true)]
    [InlineData("same-origin", "", "DAC-Bridge.local:55500", true)]
    [InlineData("same-origin", "", "192.168.1.5:55500", true)]
    [InlineData("same-origin", "", "[fe80::1]:55500", true)]
    [InlineData("same-origin", "", "localhost:55500", true)]
    [InlineData("same-origin", "", "music.home.arpa", true)]          // BRIDGE_HOSTNAMES
    // Not a browser: the name isn't what keeps it out (UPnP controllers use whatever they were given).
    [InlineData("", "", "evil.example:55500", true)]
    public void AChangeFromABrowser_MustBeAddressedToThisMachine(string fetchSite, string origin, string host, bool allowed) =>
        Assert.Equal(allowed, SameOrigin.Refusal("POST", fetchSite, origin, host, Names) == null);

    [Theory]
    [InlineData("box:55500", "box")]
    [InlineData("box", "box")]
    [InlineData("[::1]:55500", "::1")]
    [InlineData("::1", "::1")]
    [InlineData("", "")]
    public void TheNameInAHostHeader_IsWithoutItsPort(string host, string name) => Assert.Equal(name, SameOrigin.NameIn(host));
}

public sealed partial class EndToEndTests
{
    private async Task<HttpStatusCode> PostAs(string path, string body, string? fetchSite, string? origin, string? host = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        if (host != null) req.Headers.Host = host;
        if (fetchSite != null) req.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (origin != null) req.Headers.Add("Origin", origin);
        using var res = await Http.SendAsync(req);
        return res.StatusCode;
    }

    [Fact]
    public async Task ChangesSentFromAnotherSite_AreRefused_ThePagesOwnAndOtherProgramsGoAhead()
    {
        var id = await Start(Dev("clock", volume: 40));
        var self = $"http://127.0.0.1:{port}";

        // Another site's page tries to switch Squeezebox on and turn the DAC up: refused, nothing changes.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAs("/api/services", "{\"squeezelite\":true}", "cross-site", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAs($"/api/dacs/{id}/settings", "{\"level\":100}", null, "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAs("/api/services/soloist", "", "cross-site", "http://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAs($"/upnp/{id}/RenderingControl/control", "", "cross-site", "http://evil.example"));
        var services = JsonDocument.Parse(await Http.GetStringAsync($"{self}/api/services")).RootElement;
        Assert.False(services.GetProperty("squeezelite").GetBoolean());
        Assert.Equal("40", await Volume(id));

        // The bridge's own page, and a program that isn't a browser, go ahead.
        Assert.Equal(HttpStatusCode.OK, await PostAs($"/api/dacs/{id}/settings", "{\"level\":30}", "same-origin", self));
        Assert.Equal("30", await Volume(id));
        Assert.Equal(HttpStatusCode.OK, await PostAs($"/api/dacs/{id}/settings", "{\"level\":35}", null, null));
        Assert.Equal("35", await Volume(id));

        // Another site's name pointed at the bridge (DNS rebinding): the browser says same-origin. Refused.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAs($"/api/dacs/{id}/settings", "{\"level\":100}", "same-origin", $"http://evil.example:{port}", $"evil.example:{port}"));
        Assert.Equal("35", await Volume(id));
        Assert.Equal(HttpStatusCode.OK, await PostAs($"/api/dacs/{id}/settings", "{\"level\":36}", "same-origin", $"http://localhost:{port}", $"localhost:{port}"));
        Assert.Equal("36", await Volume(id));
    }
}
