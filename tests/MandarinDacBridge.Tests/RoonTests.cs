// RoonTests.cs — the extension's token goes only to the Core it was given at (RoonTokens.cs): anything on
// the network can answer discovery and claim the real Core's id. The end-to-end case is a part of
// EndToEndTests (dev/quality.md), using its pretend Core.
using MandarinDacBridge.Roon;
using Xunit;

namespace MandarinDacBridge.Tests;

public class RoonTokenTests
{
    private static Dictionary<string, string> Kept(string host) =>
        new(StringComparer.Ordinal) { ["core-1"] = "tok-1", ["core-1|host"] = host };

    [Fact]
    public void TheToken_GoesToTheCoreAtTheAddressItWasGivenAt()
    {
        Assert.Equal("tok-1", RoonTokens.For(Kept("192.168.1.20"), "core-1", "192.168.1.20", out var elsewhere));
        Assert.False(elsewhere);
    }

    [Fact]
    public void TheToken_IsntSent_ToACoreAnsweringFromAnotherAddress()
    {
        var all = Kept("192.168.1.20");
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.66", out var elsewhere));
        Assert.True(elsewhere);
        Assert.Equal("192.168.1.20", all["core-1|host"]);    // the impostor isn't remembered either
    }

    [Fact]
    public void NoToken_NothingIsSent_AndNothingIsBound()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.20", out var elsewhere));
        Assert.False(elsewhere);
        Assert.Empty(all);
    }

    [Fact]
    public void ATokenKeptBeforeAddressesWere_IsBoundToTheFirstCoreItGoesTo()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal) { ["core-1"] = "tok-1" };
        Assert.Equal("tok-1", RoonTokens.For(all, "core-1", "192.168.1.20", out _));
        Assert.Equal("192.168.1.20", all["core-1|host"]);
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.66", out var elsewhere));
        Assert.True(elsewhere);
    }

    [Fact]
    public void ATokenTheCoreGives_IsKeptWithItsAddress()
    {
        var all = Kept("192.168.1.20");
        RoonTokens.Keep(all, "core-1", "tok-2", "192.168.1.30");
        Assert.Equal("tok-2", RoonTokens.For(all, "core-1", "192.168.1.30", out _));
        Assert.Equal("", RoonTokens.For(all, "core-1", "192.168.1.20", out _));
    }

    [Fact]
    public void OfTheCoresThatAnswer_TheOneATokenWasGivenAt_ComesFirst()
    {
        List<(string, int, string)> found = [("192.168.1.66", 9330, "first"), ("192.168.1.20", 9330, "kept"), ("192.168.1.70", 9330, "other")];
        Assert.Equal(["kept", "first", "other"], RoonTokens.Prefer(found, Kept("192.168.1.20")).Select(c => c.Name));
        Assert.Equal(["first", "kept", "other"], RoonTokens.Prefer(found, new(StringComparer.Ordinal)).Select(c => c.Name));
    }
}

public sealed partial class EndToEndTests
{
    // Before 1.1.22 a Core claiming the id the token was given for got it, wherever it answered from.
    [Fact]
    public async Task Roon_TheToken_IsntSent_ToACoreAtAnotherAddress()
    {
        using var core = new FakeRoonCore();
        await Start(Dev("clock"), roonCore: $"127.0.0.1:{core.Port}");
        var tokens = Path.Combine(hosts[^1].Manager.Settings.Dir, "roon-api.json");
        OwnerOnlyFile.WriteStrings(tokens, new(StringComparer.Ordinal) { ["core-1"] = "tok-0", ["core-1|host"] = "192.168.1.20" });
        await Post("/api/services", "{\"roonApi\":true}");
        await Until(async () => (await Dacs()).RootElement.GetProperty("services").GetProperty("roonApiStatus").GetString() == "connected to Test Core");

        var register = Assert.Single(core.Got, g => g.StartsWith("MOO/1 REQUEST com.roonlabs.registry:1/register", StringComparison.Ordinal));
        Assert.DoesNotContain("tok-0", register, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\"", register, StringComparison.Ordinal);
        // Enabled again at this address: the token it gave is kept with it.
        var kept = OwnerOnlyFile.ReadStrings(tokens);
        Assert.Equal(("tok-1", "127.0.0.1"), (kept["core-1"], kept["core-1|host"]));
    }
}
