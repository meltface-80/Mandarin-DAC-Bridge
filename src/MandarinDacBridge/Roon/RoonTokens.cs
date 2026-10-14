// RoonTokens.cs — which Roon Core gets the extension's token (roon-api.json, owner-only).
//
// The token keeps the bridge enabled in a Core; whoever holds it can register as the bridge and control
// every zone. A Core is found by asking the network (SOOD), and anything on the network can answer, faster
// than the real Core, and claim the real Core's id (which its registry hands to anyone). Before 1.1.22 the
// token for that id went to whoever answered first. Now each token is kept with the address (host:port) of
// the Core it was given at, and sent only there; for a token kept with no address (before 1.1.22, or since
// the owner switched Roon now playing off), the first Core it is sent to (trust on first use).
//
// Once the bridge is enabled in a Core, it talks to no other: a Core it would send no token to is left, and
// the enabled one looked for again. Were it registered with, it could say it had enabled the bridge and have
// its "token" kept with its own address, and nothing on the network can tell the two apart. So a Core that
// has moved is taken up again only by the owner: switching Roon now playing off forgets the addresses.
//
// roon-api.json is one flat map: "<core id>" → token, "<core id>|host" → address. A core id is Roon's
// GUID; one with the "|" in it would name another id's address, so it is no core id.
using System.Globalization;

namespace MandarinDacBridge.Roon;

internal static class RoonTokens
{
    private const string At = "|host";

    public static bool Valid(string coreId) => coreId != "" && !coreId.Contains('|', StringComparison.Ordinal);

    public static string Address(string host, int port) => host + ":" + port.ToString(CultureInfo.InvariantCulture);

    // Token: what to send ("" for none). Elsewhere: none goes here, and the bridge is enabled in a Core at
    // another address: this one is to be left. Bound: the token had no address and has just been bound here
    // in `all` (to be written before it is sent, or the next Core to claim the id is trusted on first use
    // again).
    public readonly record struct Grant(string Token, bool Elsewhere, bool Bound);

    public static Grant For(Dictionary<string, string> all, string coreId, string address)
    {
        if (!Valid(coreId) || !all.TryGetValue(coreId, out var token) || token == "") return new Grant("", Addresses(all).Count > 0, false);
        if (!all.TryGetValue(coreId + At, out var at) || at == "")
        {
            all[coreId + At] = address;
            return new Grant(token, false, true);
        }
        return string.Equals(at, address, StringComparison.OrdinalIgnoreCase) ? new Grant(token, false, false) : new Grant("", true, false);
    }

    // The owner's way to take up a Core that has moved: the tokens stay, their addresses go.
    public static bool Forget(Dictionary<string, string> all)
    {
        var keys = all.Keys.Where(k => k.EndsWith(At, StringComparison.Ordinal)).ToList();
        foreach (var k in keys) all.Remove(k);
        return keys.Count > 0;
    }

    // A token the Core gave, kept with the address it gave it at. False (nothing kept) for an id that isn't one.
    public static bool Keep(Dictionary<string, string> all, string coreId, string token, string address)
    {
        if (!Valid(coreId) || token == "") return false;
        all[coreId] = token;
        all[coreId + At] = address;
        return true;
    }

    // Of the Cores that answered, one a token was given at comes first; otherwise they stay as they came.
    public static List<(string Host, int Port, string Name)> Prefer(List<(string Host, int Port, string Name)> found, Dictionary<string, string> all)
    {
        var bound = Addresses(all);
        return [.. found.OrderBy(f => bound.Contains(Address(f.Host, f.Port)) ? 0 : 1)];
    }

    // The addresses tokens are kept with: an id's own, under a key that names one.
    private static HashSet<string> Addresses(Dictionary<string, string> all) =>
        all.Where(kv => kv.Key.EndsWith(At, StringComparison.Ordinal) && Valid(kv.Key[..^At.Length]) && kv.Value != ""
                        && all.TryGetValue(kv.Key[..^At.Length], out var t) && t != "")
            .Select(kv => kv.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
