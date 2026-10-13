// Track.cs — what the renderer plays and says: a track as a controller gave it (its URI, its DIDL-Lite,
// what was learnt on the way), what a Squeezebox server or Spotify says about it (LiveMeta), where each
// track begins in the DAC's stream (Mark), and what the page is shown (NowPlaying). Used by Renderer.cs.
namespace MandarinDacBridge;

internal sealed class UpnpException(int code, string description) : Exception(description)
{
    public int Code { get; } = code;
}

internal sealed class Track(string uri, string meta)
{
    public string Uri { get; } = uri;
    public string Meta { get; } = meta;
    public Didl Didl { get; } = Xml.ParseDidl(meta, uri);
    public string Mime { get; set; } = "";
    public double Duration { get; set; }
    public TrackInfo? Info { get; set; }
    public bool Taken { get; set; }
    public LiveMeta? Live { get; set; }
}

// What a Squeezebox server or Spotify says is playing. Position (when given)
// was so at At (ms) and runs on from there while the track plays.
internal sealed record LiveMeta(string Title, string Artist, string Album, string Art, double Duration, double? Position = null, long At = 0);

internal sealed record Mark(long Frame, Track Track, double Offset, TrackInfo? Info);

internal sealed record NowPlaying(string Transport, string Status, string Error, string Title, string Artist, string Album,
    long Position, long Duration, string Format, string Physical, bool Muted, string Art = "");
