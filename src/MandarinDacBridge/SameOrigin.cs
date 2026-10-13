// SameOrigin.cs — from a browser, only the bridge's own page may change anything.
//
// A request that changes something (a POST to the API, a SOAP action, a subscription) is refused when
// the browser says it comes from another site. Otherwise any page of any site, opened in a browser
// anywhere on the network, could switch connections on, start downloads or turn a DAC up. Browsers say
// where a request comes from: Sec-Fetch-Site, or on older ones Origin. Programs that aren't browsers
// (UPnP controllers, a Squeezebox server, curl) send neither and are let through: they aren't what this
// closes. Who on the network may use the page at all is for the password (dev/quality.md, Security).
//
// A browser also says which name it reached the bridge by (Host). Another site can point a name of its
// own at the bridge's address (DNS rebinding): its page and the bridge then look like one site to the
// browser, and Sec-Fetch-Site says same-origin. So a change from a browser goes ahead only when it was
// addressed to an address, to localhost, or to one of this machine's names: its hostname (with or
// without .local), BRIDGE_IP, and the names in BRIDGE_HOSTNAMES (for a name of your own in your DNS).
using System.Net;

namespace MandarinDacBridge;

internal static class SameOrigin
{
    // Null when the request may go ahead; otherwise why not. host is the request's Host header; names are
    // this machine's (Config.Names).
    public static string? Refusal(string method, string fetchSite, string origin, string host, IReadOnlyCollection<string> names)
    {
        if (method is "GET" or "HEAD" or "OPTIONS") return null;
        bool browser = fetchSite != "" || origin != "";
        if (browser && !Ours(NameIn(host), names))
            return $"addressed to {NameIn(host)}, which isn't this machine's name or address (BRIDGE_HOSTNAMES adds a name)";
        if (fetchSite != "") return fetchSite is "same-origin" or "none" ? null : $"sent from another site ({fetchSite})";
        if (origin == "") return null;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return $"sent from an unknown origin ({origin})";
        var from = o.IsDefaultPort ? o.Host : $"{o.Host}:{o.Port}";
        return string.Equals(from, host, StringComparison.OrdinalIgnoreCase) ? null : $"sent from {origin}";
    }

    // The name in a Host header, without its port: "box:55500" is box, "[::1]:55500" is ::1.
    internal static string NameIn(string host)
    {
        if (host.StartsWith('[')) return host.IndexOf(']', StringComparison.Ordinal) is > 0 and var end ? host[1..end] : host;
        return host.Count(c => c == ':') == 1 ? host[..host.IndexOf(':', StringComparison.Ordinal)] : host;
    }

    private static bool Ours(string name, IReadOnlyCollection<string> names)
    {
        if (name == "" || IPAddress.TryParse(name, out _) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var bare = name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
        return names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase) || n.Equals(bare, StringComparison.OrdinalIgnoreCase));
    }
}
