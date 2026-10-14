// IdentityTests.cs — what must never change (CLAUDE.md, "Identities never change"): how a DAC's id, its
// UPnP UDN and its Squeezebox MAC are derived, Roon's extension id, the settings.json field names, the
// port and the program's, container's and volume's names. Each is pinned to its value as shipped: a change
// renames every DAC (and loses its settings), makes every controller forget it, or leaves an install
// pointing at nothing. A failure here is never fixed by updating the value.
using System.Text.Json;
using Xunit;

namespace MandarinDacBridge.Tests;

public class IdentityTests
{
    [Theory]
    [InlineData("usb:0d8c:0014:1-1.2", "dac-874f7b3867")]
    [InlineData("alsa:hw:CARD=DAC,DEV=0", "dac-0bc5e8c1c6")]
    public void ADacsId_IsDerivedAsShipped(string key, string id) => Assert.Equal(id, Devices.IdOf(key));

    [Theory]
    [InlineData("music-room", "usb:0d8c:0014:1-1.2", "uuid:573e39c8-6677-529a-9f51-6e9216cf301f")]
    [InlineData("music-room", "alsa:hw:CARD=DAC,DEV=0", "uuid:bf0afe71-4049-56c1-a140-0a2ec7eb14f6")]
    public void ADacsUdn_IsDerivedAsShipped(string host, string key, string udn) => Assert.Equal(udn, Upnp.Description.UdnFor(host, key));

    [Theory]
    [InlineData("music-room", "usb:0d8c:0014:1-1.2", "6e:19:45:98:f5:c3")]
    [InlineData("music-room", "alsa:hw:CARD=DAC,DEV=0", "ba:a8:ea:dc:14:cd")]
    public void ADacsSqueezeboxMac_IsDerivedAsShipped(string host, string key, string mac) =>
        Assert.Equal(mac, string.Join(':', Slim.SlimPlayer.MacFor(host, key).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))));

    [Fact]
    public void RoonsExtensionId_IsAsShipped() => Assert.Equal("com.musicd.mandarin-dac-bridge", Roon.RoonApi.ExtensionId);

    [Fact]
    public void ThePort_IsAsShipped() => Assert.Equal(55500, new Config().Port);

    // settings.json as written: every field's name. A field may be added; none renamed or dropped.
    [Fact]
    public void TheSettingsFilesFieldNames_AreAsShipped()
    {
        var file = new SettingsFile
        {
            Dacs = { ["dac-874f7b3867"] = new DacSettings() },
            Services = new ServiceSettings
            {
                Squeezelite = true, LmsServer = "", Spotify = true, SoloistKey = "", Caldera = true, Qobuz = true, PlexToken = "",
                PlexAccount = "", MacApps = true, MacDac = "", Roon = true, RoonApi = true
            }
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(file, BridgeJson.Default.SettingsFile));
        var root = doc.RootElement;
        Assert.Superset(new HashSet<string> { "dacs", "services" }, Names(root));
        Assert.Superset(new HashSet<string> { "enabled", "dsd", "name", "share", "volume", "roonZone" }, Names(root.GetProperty("dacs").GetProperty("dac-874f7b3867")));
        Assert.Superset(new HashSet<string>
        {
            "squeezelite", "lmsServer", "spotify", "soloistKey", "caldera", "qobuz", "plexToken", "plexAccount", "macApps", "macDac", "roon", "roonApi"
        }, Names(root.GetProperty("services")));
    }

    private static HashSet<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToHashSet();

    // The names an install refers to: the program's, the container's, the data volume's.
    [Fact]
    public void TheProgramContainerAndVolumeNames_AreAsShipped()
    {
        var compose = File.ReadAllText(Path.Combine(ComposeTests.RepoRoot(), "docker-compose.yml"));
        Assert.Contains("container_name: mandarin-dac-bridge", compose);
        Assert.Contains("- dac-bridge-data:/data", compose);
        Assert.Contains("\n  dac-bridge-data:", compose);
        var csproj = File.ReadAllText(Path.Combine(ComposeTests.RepoRoot(), "src", "MandarinDacBridge", "MandarinDacBridge.csproj"));
        Assert.Contains("<AssemblyName>mandarin-dac-bridge</AssemblyName>", csproj);
    }
}
