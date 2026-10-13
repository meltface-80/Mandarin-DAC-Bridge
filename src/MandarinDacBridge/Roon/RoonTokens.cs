// RoonTokens.cs — which Roon Core gets the extension's token (roon-api.json, owner-only).
//
// The token keeps the bridge enabled in a Core; whoever holds it can register as the bridge and control
// every zone. A Core is found by asking the network (SOOD), and anything on the network can answer, faster
// than the real Core, and claim the real Core's id (which its registry hands to anyone). Before 1.1.22 the
// token for that id went to whoever answered first. Now each token is kept with the address of the Core it
// was given at, and sent only there: the first Core it is sent to after it was kept, for a token kept
// before (trust on first use). A Core that moves to another address is enabled again in Roon.
namespace MandarinDacBridge.Roon;

internal static class RoonTokens
{
    private static string HostKey(string coreId) => coreId + "|host";

    // The token to send to this Core at this address: "" when there is none, or when it was given at
    // another address. A token kept before addresses were is bound here now.
    public static string For(Dictionary<string, string> all, string coreId, string host, out bool elsewhere)
    {
        elsewhere = false;
        if (!all.TryGetValue(coreId, out var token) || token == "") return "";
        if (!all.TryGetValue(HostKey(coreId), out var bound) || bound == "") { all[HostKey(coreId)] = host; return token; }
        if (string.Equals(bound, host, StringComparison.OrdinalIgnoreCase)) return token;
        elsewhere = true;
        return "";
    }

    // A token the Core gave, kept with the address it gave it at.
    public static void Keep(Dictionary<string, string> all, string coreId, string token, string host)
    {
        all[coreId] = token;
        all[HostKey(coreId)] = host;
    }

    // Of the Cores that answered, one a token was given at comes first; otherwise they stay as they came.
    public static List<(string Host, int Port, string Name)> Prefer(List<(string Host, int Port, string Name)> found, Dictionary<string, string> all)
    {
        var bound = all.Where(kv => kv.Key.EndsWith("|host", StringComparison.Ordinal)).Select(kv => kv.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. found.OrderBy(f => bound.Contains(f.Host) ? 0 : 1)];
    }
}
