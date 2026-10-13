// PlexServers.cs — which Plex servers may be sent the owner's sign-in, and how they are asked.
//
// Caldera's Plex Companion timeline names the server a track comes from, and that name comes from
// whoever told Caldera to play: any device on the network can. The owner's plex.tv sign-in is an
// account-wide token, so it goes only to the owner's own servers, by plex.tv's own list of them (its
// resources: each server's addresses, with their scheme). A server the timeline names gets the token the
// timeline came with; failing that, the account token if it is one of the owner's; otherwise none, and
// what it plays isn't looked up.
//
// What is asked of a server is a path on that same server (the timeline's key, a cover's thumb), checked
// on the URL actually requested, and asked without following redirects: a redirect would carry the
// X-Plex-Token header to whatever host it names.
using System.Text.Json;

namespace MandarinDacBridge.Caldera;

internal static class PlexServers
{
    internal static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    // A server as "scheme://address:port" (lower case): "http://192.168.1.10:32400" stays so.
    internal static string Key(string server) => Uri.TryCreate(server, UriKind.Absolute, out var u) ? Key(u) : "";

    private static string Key(Uri u) =>
        u.Scheme is not ("http" or "https") ? ""
        : u.HostNameType == UriHostNameType.IPv6 ? $"{u.Scheme}://[{u.IdnHost.Trim('[', ']').ToLowerInvariant()}]:{u.Port}"
        : $"{u.Scheme}://{u.IdnHost.ToLowerInvariant()}:{u.Port}";

    // The URL of a path on a server, or null when it isn't a plain path on that very server (a key like
    // "@other.host/x" would otherwise move the request to another host).
    public static Uri? On(string server, string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\')) return null;
        var key = Key(server);
        return key != "" && Uri.TryCreate(server + path, UriKind.Absolute, out var u) && u.UserInfo == "" && Key(u) == key ? u : null;
    }

    // Every address of every server the account owns, from plex.tv's resources (a JSON array).
    internal static HashSet<string> Owned(JsonElement resources)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (resources.ValueKind != JsonValueKind.Array) return keys;
        foreach (var r in resources.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            bool server = r.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.String && (p.GetString() ?? "").Split(',').Contains("server");
            bool owned = r.TryGetProperty("owned", out var o) && o.ValueKind == JsonValueKind.True;
            if (!server || !owned || !r.TryGetProperty("connections", out var cs) || cs.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                if (Str(c, "uri") is { Length: > 0 } uri && Key(uri) is { Length: > 0 } k) keys.Add(k);
                if (Str(c, "address") is { Length: > 0 } host && c.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var n)
                    && Key($"{(Str(c, "protocol") is { Length: > 0 } pr ? pr : "http")}://{(host.Contains(':') ? $"[{host}]" : host)}:{n}") is { Length: > 0 } ak)
                    keys.Add(ak);
            }
        }
        return keys;
    }

    private static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // The account's list, kept for the account (a new sign-in starts afresh), asked again when it is an hour
    // old or doesn't have the server asked about; after plex.tv couldn't be asked, not again for 30 seconds.
    private static readonly object gate = new();
    private static string listFor = "";
    private static HashSet<string> list = [];
    private static DateTime listAt = DateTime.MinValue, failedAt = DateTime.MinValue;

    // Whether a server is one of the account's: true, false, or null when plex.tv can't say just now.
    public static async Task<bool?> IsOwned(string server, string token, string clientId, CancellationToken ct)
    {
        var key = Key(server);
        if (key == "" || token == "") return false;
        HashSet<string> known;
        DateTime at, failed;
        lock (gate)
        {
            if (listFor != token) { listFor = token; list = []; listAt = failedAt = DateTime.MinValue; }
            (known, at, failed) = (list, listAt, failedAt);
        }
        var now = DateTime.UtcNow;
        bool fresh = now - at < TimeSpan.FromHours(1);
        if (fresh && known.Contains(key)) return true;
        if (now - failed < TimeSpan.FromSeconds(30)) return null;
        var asked = await Fetch(token, clientId, ct);
        lock (gate)
        {
            if (listFor != token) return null;
            if (asked == null) { failedAt = now; return null; }
            (list, listAt) = (asked, now);
        }
        return asked.Contains(key);
    }

    // The owner's servers, asked of plex.tv with the account token; null when plex.tv can't be asked.
    private static async Task<HashSet<string>?> Fetch(string token, string clientId, CancellationToken ct)
    {
        try
        {
            using var req = PlexLink.Request(HttpMethod.Get, $"{PlexLink.PlexTv}/api/v2/resources?includeHttps=1", clientId);
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return Owned(doc.RootElement);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Offline, plex.tv slow or changed: null, which the caller reports and asks again about later;
            // until plex.tv answers, a server not already on a list of the last hour gets no token.
            return null;
        }
    }
}
