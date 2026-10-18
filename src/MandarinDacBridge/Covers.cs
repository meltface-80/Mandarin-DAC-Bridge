// Covers.cs — a cover the page shows, fetched here from the server that has it (/api/dacs/<id>/art): a
// Plex server named in Caldera's timeline (with the token, which stays here), or a Roon Core. (The Music
// app's covers are files on this machine, read as they are.)
//
// Those servers aren't the bridge's, and whatever they send is passed on to anyone who asks for the DAC's
// cover. Before 1.1.23 the fetch followed redirects anywhere and took a reply with no type as a JPEG, so a
// server could point the bridge at 127.0.0.1 or another machine on the network and have the answer
// relayed. Now a cover is what that same server sends (a redirect is followed only to another path on
// it), said to be a picture, of a picture's size, within 15 seconds; anything else is no cover.
using System.Net;

namespace MandarinDacBridge;

internal static class Covers
{
    public const int MaxBytes = 16 << 20;
    private const int MaxRedirects = 3;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseDrainSize = 0
    }) { Timeout = Timeout.InfiniteTimeSpan };

    static Covers() => Http.DefaultRequestHeaders.UserAgent.ParseAdd(Sources.UserAgent);

    // Only a picture is passed on (never SVG, which can carry script), and only one said to be one.
    internal static string? Type(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" or "image/pjpeg" => "image/jpeg",
        var t and ("image/png" or "image/webp" or "image/gif") => t,
        _ => null
    };

    // The cover at this URL, or null when there is none to pass on.
    // Past `within` (15 s), from asking to the last byte, it is no cover either: OperationCanceledException.
    public static async Task<(string Type, byte[] Bytes)?> Fetch(string url, CancellationToken ct, TimeSpan? within = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(within ?? TimeSpan.FromSeconds(15));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var at) || at.Scheme is not ("http" or "https")) return null;
        for (int hops = 0; ; hops++)
        {
            using var res = await Http.GetAsync(at, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (res.StatusCode is >= HttpStatusCode.MovedPermanently and < HttpStatusCode.BadRequest)
            {
                // Another path on the same server (the URL may carry Plex's token): never another server.
                if (hops == MaxRedirects || res.Headers.Location is not { } to) return null;
                var next = new Uri(at, to);
                if (!SameServer(at, next)) return null;
                at = next;
                continue;
            }
            if (res.StatusCode != HttpStatusCode.OK || Type(res.Content.Headers.ContentType?.MediaType) is not { } type) return null;
            return await Read(res, limit.Token) is { } bytes ? (type, bytes) : null;
        }
    }

    private static bool SameServer(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

    // The body, or null past MaxBytes (said or sent). Of the size said, it is read straight into its array.
    private static async Task<byte[]?> Read(HttpResponseMessage res, CancellationToken ct)
    {
        var said = res.Content.Headers.ContentLength;
        if (said > MaxBytes) return null;
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        if (said is { } n)
        {
            var exact = new byte[n];
            await body.ReadExactlyAsync(exact, ct);
            return await body.ReadAsync(new byte[1], ct) == 0 ? exact : null;
        }
        var buf = new MemoryStream();
        var chunk = new byte[81920];
        int got;
        while ((got = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buf.Length + got > MaxBytes) return null;
            buf.Write(chunk, 0, got);
        }
        return buf.ToArray();
    }
}
