// SpotifyTests.cs — Spotify Connect through Spotify Soloist, as the owner switches it on and changes it.
// A part of EndToEndTests (dev/quality.md: an area moves out as a part of the same class until the
// shared fixture exists), using its helpers. No Soloist is needed here: what is tested is the bridge's
// own wiring.
using System.Text;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests
{
    // Spotify's sound reaches the renderer through a name ("live:spotify/<dac>") its player registers.
    // A new key, a Soloist download or an expired build restarts the player: the new one must still be
    // reachable by that name. (1.1.9: the old player, closed after the new one had registered, took the
    // name away, and every play then failed until Spotify was switched off and on.)
    [Fact]
    public async Task Spotify_RestartedByANewKey_StillReachesItsSound()
    {
        var id = await Start(Dev("clock"), soloist: Path.Combine(tmp, "no-soloist-here"));
        await PostServices("{\"spotify\":true,\"soloistKey\":\"key-one\"}");
        Assert.Equal("Spotify isn't playing", OpenSpotifySound(id));

        await PostServices("{\"soloistKey\":\"key-two\"}");   // restarts the player
        Assert.Equal("Spotify isn't playing", OpenSpotifySound(id));

        await PostServices("{\"spotify\":false}");
        Assert.Equal($"live:spotify/{id} isn't playing", OpenSpotifySound(id));
    }

    private async Task PostServices(string json)
    {
        using var res = await Http.PostAsync($"http://127.0.0.1:{port}/api/services", new StringContent(json, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
    }

    // What opening Spotify's sound says while nothing plays: its player's own answer, or that no player
    // has the name at all.
    private static string OpenSpotifySound(string id) =>
        Assert.Throws<InvalidOperationException>(() =>
            Sources.Open($"live:spotify/{id}", "", 0, new DacTraits([44100], 2, "pcm"), "ffmpeg", _ => { }, CancellationToken.None)).Message;
}
