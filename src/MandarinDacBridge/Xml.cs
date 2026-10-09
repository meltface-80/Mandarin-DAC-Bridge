// Xml.cs — the little XML the bridge needs: escaping, the arguments of a SOAP
// request, and the fields of a DIDL-Lite item. No parser: UPnP's documents
// are flat enough for these few patterns, and controllers' XML is often not
// quite well-formed.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MandarinDacBridge;

internal sealed record Didl(string Title, string Artist, string Album, string Art, string Mime, double Duration, string ProtocolInfo)
{
    public static readonly Didl Empty = new("", "", "", "", "", 0, "");
}

internal static partial class Xml
{
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&apos;", _ => c.ToString() });
        }
        return sb.ToString();
    }

    public static string Unesc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = CData().Replace(s, "$1");
        return Entity().Replace(s, m =>
        {
            var e = m.Groups[1].Value.ToLowerInvariant();
            switch (e)
            {
                case "lt": return "<";
                case "gt": return ">";
                case "quot": return "\"";
                case "apos": return "'";
                case "amp": return "&";
            }
            var ok = e[1] == 'x'
                ? int.TryParse(e.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)
                : int.TryParse(e.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            return ok && n is > 0 and <= 0x10FFFF ? char.ConvertFromUtf32(n) : m.Value;
        });
    }

    // The direct children of an element's inner text: name → text.
    public static Dictionary<string, string> Children(string inner)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Child().Matches(inner))
            d[m.Groups[1].Value] = m.Groups[2].Success ? Unesc(m.Groups[2].Value) : "";
        return d;
    }

    // A SOAP request → the action and its arguments.
    public static (string Action, Dictionary<string, string> Args) ParseSoap(string body, string? soapAction)
    {
        var action = "";
        var h = SoapActionHeader().Match(soapAction ?? "");
        if (h.Success) action = h.Groups[1].Value;
        var bm = SoapBody().Match(body ?? "");
        var inner = bm.Success ? bm.Groups[1].Value : body ?? "";
        var am = SoapAction().Match(inner);
        if (am.Success)
        {
            if (action == "") action = am.Groups[2].Value;
            return (action, Children(am.Groups[3].Value));
        }
        var empty = SoapEmptyAction().Match(inner);
        if (empty.Success && action == "") action = empty.Groups[1].Value;
        return (action, new Dictionary<string, string>());
    }

    public static string Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\s{Regex.Escape(name)}\s*=\s*(""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? Unesc(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : "";
    }

    // DIDL-Lite → the resource's MIME and duration, and the title, artist and album for the page.
    public static Didl ParseDidl(string? meta, string? uri)
    {
        if (string.IsNullOrEmpty(meta)) return Didl.Empty;
        string Pick(Regex re) { var m = re.Match(meta); return m.Success ? Unesc(m.Groups[1].Value).Trim() : ""; }
        var title = Pick(DidlTitle());
        var artist = Pick(DidlArtist());
        if (artist == "") artist = Pick(DidlCreator());
        var album = Pick(DidlAlbum());
        var art = Pick(DidlArt());
        Match? chosen = null;
        foreach (Match m in DidlRes().Matches(meta))
        {
            chosen ??= m;
            if (!string.IsNullOrEmpty(uri) && Unesc(m.Groups[2].Value).Trim() == uri) { chosen = m; break; }
        }
        string pi = "", mime = "";
        double duration = 0;
        if (chosen != null)
        {
            pi = Attr(chosen.Groups[1].Value, "protocolInfo");
            var parts = pi.Split(':');
            mime = parts.Length > 2 ? parts[2].Trim() : "";
            duration = HmsToSeconds(Attr(chosen.Groups[1].Value, "duration"));
        }
        return new Didl(title, artist, album, art, mime, duration, pi);
    }

    // "1:02:03.500" → 3723.5; anything else → 0.
    public static double HmsToSeconds(string? t)
    {
        var m = Hms().Match(t ?? "");
        if (!m.Success) return 0;
        double h = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return h * 3600 + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
             + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    // 3723.5 → "1:02:03" (UPnP's H+:MM:SS).
    public static string SecondsToHms(double s)
    {
        var t = (long)Math.Max(0, Math.Floor(double.IsFinite(s) ? s : 0));
        return $"{t / 3600}:{t % 3600 / 60:00}:{t % 60:00}";
    }

    [GeneratedRegex(@"<!\[CDATA\[([\s\S]*?)\]\]>")] private static partial Regex CData();
    [GeneratedRegex(@"&(#x[0-9a-fA-F]+|#\d+|lt|gt|quot|apos|amp);", RegexOptions.IgnoreCase)] private static partial Regex Entity();
    [GeneratedRegex(@"<(?:[A-Za-z_][\w.-]*:)?([A-Za-z_][\w.-]*)(?:\s[^>]*?)?(?:/>|>([\s\S]*?)</(?:[A-Za-z_][\w.-]*:)?\1\s*>)")] private static partial Regex Child();
    [GeneratedRegex(@"#([\w-]+)""?\s*$")] private static partial Regex SoapActionHeader();
    [GeneratedRegex(@"<(?:[\w-]+:)?Body[^>]*>([\s\S]*)</(?:[\w-]+:)?Body\s*>", RegexOptions.IgnoreCase)] private static partial Regex SoapBody();
    [GeneratedRegex(@"<(?:([\w-]+):)?([A-Za-z_][\w-]*)(?:\s[^>]*)?>([\s\S]*)</(?:\1:)?\2\s*>")] private static partial Regex SoapAction();
    [GeneratedRegex(@"<(?:[\w-]+:)?([A-Za-z_][\w-]*)(?:\s[^>]*)?/>")] private static partial Regex SoapEmptyAction();
    [GeneratedRegex(@"<dc:title[^>]*>([\s\S]*?)</dc:title>", RegexOptions.IgnoreCase)] private static partial Regex DidlTitle();
    [GeneratedRegex(@"<upnp:artist[^>]*>([\s\S]*?)</upnp:artist>", RegexOptions.IgnoreCase)] private static partial Regex DidlArtist();
    [GeneratedRegex(@"<dc:creator[^>]*>([\s\S]*?)</dc:creator>", RegexOptions.IgnoreCase)] private static partial Regex DidlCreator();
    [GeneratedRegex(@"<upnp:album[^>]*>([\s\S]*?)</upnp:album>", RegexOptions.IgnoreCase)] private static partial Regex DidlAlbum();
    [GeneratedRegex(@"<upnp:albumArtURI[^>]*>([\s\S]*?)</upnp:albumArtURI>", RegexOptions.IgnoreCase)] private static partial Regex DidlArt();
    [GeneratedRegex(@"<res\b([^>]*)>([\s\S]*?)</res>", RegexOptions.IgnoreCase)] private static partial Regex DidlRes();
    [GeneratedRegex(@"^\s*(?:(\d+):)?(\d+):(\d+(?:\.\d+)?)\s*$")] private static partial Regex Hms();
}
