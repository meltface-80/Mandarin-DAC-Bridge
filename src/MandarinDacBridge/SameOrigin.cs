// SameOrigin.cs — from a browser, only the bridge's own page may change anything.
//
// A request that changes something (a POST to the API, a SOAP action, a subscription) is refused when
// the browser says it comes from another site. Otherwise any page of any site, opened in a browser
// anywhere on the network, could switch connections on, start downloads or turn a DAC up. Browsers say
// where a request comes from: Sec-Fetch-Site, or on older ones Origin. Programs that aren't browsers
// (UPnP controllers, a Squeezebox server, curl) send neither and are let through: they aren't what this
// closes. Who on the network may use the page at all is for the password (dev/quality.md, Security).
namespace MandarinDacBridge;

internal static class SameOrigin
{
    // Null when the request may go ahead; otherwise why not. host is the request's Host header.
    public static string? Refusal(string method, string fetchSite, string origin, string host)
    {
        if (method is "GET" or "HEAD" or "OPTIONS") return null;
        if (fetchSite != "") return fetchSite is "same-origin" or "none" ? null : $"sent from another site ({fetchSite})";
        if (origin == "") return null;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return $"sent from an unknown origin ({origin})";
        var from = o.IsDefaultPort ? o.Host : $"{o.Host}:{o.Port}";
        return string.Equals(from, host, StringComparison.OrdinalIgnoreCase) ? null : $"sent from {origin}";
    }
}
