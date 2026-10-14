// StreamUri.cs — what the bridge may be asked to play: a stream on the network (http or https), never a
// file on this machine, another source's live sound, or one of ffmpeg's other inputs.
//
// A UPnP controller's CurrentURI and NextURI went to `ffmpeg -i` as given, and any device on the network
// can be a controller. "file:///…", "concat:…", "subfile:…" or "data:…" had ffmpeg read files on this
// machine, and "live:…" attached a controller to another app's sound (Spotify's, the Mac apps'). Before
// 1.1.20 nothing checked. Now every track meets one rule where it meets ffmpeg (Sources.Open), checked
// early for UPnP too (Upnp/Control.cs, before the arbiter); and ffmpeg is told which protocols it may use,
// so a playlist it fetches (HLS) can't send it to a file either.
namespace MandarinDacBridge;

internal static class StreamUri
{
    // HTTP(S), what it rides on (TCP, TLS, a proxy), HLS's AES and its keys written inline (data:, which
    // reads nothing on this machine).
    public const string FfmpegProtocols = "http,https,tcp,tls,crypto,httpproxy,data";

    // The stream as ffmpeg should be given it (its scheme in lower case: ffmpeg's protocol names are), or
    // null when it isn't an http or https URL with a host. Everything after the scheme is kept as sent: a
    // signed URL (a streaming service's) must reach its server unchanged.
    public static string? Stream(string uri)
    {
        uri = uri.Trim();
        int sep = uri.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return null;
        var scheme = uri[..sep].ToLowerInvariant();
        if (scheme is not ("http" or "https") || uri.Contains('\\')) return null;
        var stream = scheme + uri[sep..];
        return Uri.TryCreate(stream, UriKind.Absolute, out var u) && u.Host != "" ? stream : null;
    }
}
