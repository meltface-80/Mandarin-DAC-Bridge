// ManagerView.cs — what the page and the now-playing screen are shown: each DAC with what plays on it
// (from the bridge, or from Roon's zone for it), the connections and their state, and the covers.
// A part of Manager (Manager.cs), sharing its state and locks.
using MandarinDacBridge.Spotify;

namespace MandarinDacBridge;

internal sealed partial class Manager
{
    // The Roon zone that plays to this DAC: the one chosen on its page, else one named like it
    // (its Squeezebox name "<DAC> (Bridge)", its own name, or Roon Bridge's name for it).
    private Roon.RoonZone? ZoneFor(Bridge b, List<Roon.RoonZone> zones)
    {
        if (zones.Count == 0) return null;
        var chosen = Settings.For(b.Id).RoonZone;
        if (chosen != "") return zones.FirstOrDefault(z => z.Name == chosen || z.Outputs.Contains(chosen));
        static string N(string s) => s.Trim().TrimEnd('.').ToLowerInvariant();
        var names = new[] { b.FriendlyName(), Settings.For(b.Id).Name, b.Dev.Name }.Where(x => x.Trim() != "").Select(N).ToList();
        bool Is(Roon.RoonZone z) => z.Outputs.Append(z.Name).Select(N).Any(names.Contains);
        bool Like(Roon.RoonZone z) => z.Outputs.Append(z.Name).Select(N).Any(o => o.Length >= 6 && names.Any(n => n.Contains(o) || o.Contains(n)));
        return zones.FirstOrDefault(Is) ?? zones.FirstOrDefault(Like);
    }

    private static NowPlaying RoonNow(Roon.RoonZone z, string dacId)
    {
        double pos = (z.Seek ?? 0) + (z.State == "playing" ? Math.Max(0, Arbiter.Now - z.At) / 1000.0 : 0);
        if (z.Length > 0) pos = Math.Min(pos, z.Length);
        var transport = z.State switch { "playing" => "PLAYING", "paused" => "PAUSED_PLAYBACK", _ => "TRANSITIONING" };
        var art = z.ImageKey != "" ? $"/api/dacs/{dacId}/art?k={Uri.EscapeDataString(z.ImageKey)}" : "";
        return new NowPlaying(transport, "OK", "", z.Title, z.Artist, z.Album, (long)Math.Round(pos), (long)Math.Round(z.Length), "", "", false, art);
    }

    // The cover for what plays on this DAC, when the page can't fetch it itself: a URL (Caldera's, with the
    // Plex token) or a file (the Music app's).
    public string? Art(string id)
    {
        if (Get(id)?.Caldera?.ArtUrl is { Length: > 0 } u) return u;
        if (Get(id) is { } m && mac?.CoverFor(m) is { } f) return f;
        var api = roonApi;   // read once: switching Roon now playing off meanwhile leaves it null
        if (api != null && Get(id) is { } b && ZoneFor(b, api.Zones()) is { ImageKey.Length: > 0 } z) return api.ImageUrl(z.ImageKey);
        return null;
    }

    public ServicesView Services()
    {
        var soloist = config.FindSoloist();
        return new ServicesView
        {
            Squeezelite = SqueezeliteOn, LmsServer = LmsServer, Spotify = SpotifyOn,
            SoloistKey = SoloistKey != "", Soloist = soloist, SoloistVersion = SoloistDownload.Version(soloist),
            SoloistExpires = SoloistDownload.Expires(SoloistDownload.Version(soloist))?.ToString("yyyy-MM-dd") ?? "",
            SoloistDownload = soloistDownload, PulseAudio = Config.FindPulseAudio() != "",
            SpotifyPossible = OperatingSystem.IsLinux() && SoloistDownload.Arch != null,
            Caldera = CalderaOn, CalderaPossible = OperatingSystem.IsLinux() && Caldera.CalderaDownload.Arch != null,
            CalderaVersion = Caldera.CalderaDownload.Version(config.DataDir), CalderaDownload = calderaDownload,
            PlexSignedIn = PlexToken != "", PlexAccount = PlexToken != "" ? Settings.Services().PlexAccount ?? "" : "",
            PlexCode = plexLink?.Code ?? "", PlexMessage = plexLink?.Message ?? "",
            Qobuz = QobuzOn, QobuzInstalled = qobuz?.Installed ?? false, QobuzPinned = qobuz?.Pinned ?? false, QobuzInstall = qobuz?.Install ?? "",
            QobuzStatus = qobuz?.Status ?? "", QobuzSignedIn = qobuz?.SignedIn ?? false, QobuzWebPort = Qobuz.QobuzConnect.WebPort,
            MacPossible = OperatingSystem.IsMacOS(), MacApps = MacOn,
            MacDriver = OperatingSystem.IsMacOS() && Native.CoreAudio.FindDevice(Mac.MacSource.LoopbackUid) != Native.CoreAudio.Unknown, MacDac = MacTarget()?.Id ?? "", MacStatus = mac?.Status ?? "",
            RoonBridge = RoonBridge.Running(),
            Roon = RoonOn, RoonPossible = Roon.RoonDownload.Package != null, RoonVersion = Roon.RoonDownload.Version(config.DataDir),
            RoonDownload = roonDownload, RoonStatus = roon?.Status ?? "",
            RoonApi = RoonApiOn, RoonApiStatus = roonApi?.Status ?? "", RoonZones = roonApi?.Zones().Select(z => z.Name).ToArray() ?? []
        };
    }

    // Everything the page shows.
    public BridgeView View()
    {
        var list = new List<DacView>();
        var zones = roonApi?.Zones() ?? [];
        List<DacDevice> devs;
        lock (gate) devs = devices.Values.ToList();
        foreach (var dev in devs)
        {
            var s = Settings.For(dev.Id);
            var b = Get(dev.Id);
            var holder = dev.HolderPid > 0 ? (dev.HolderName != "" ? dev.HolderName : $"process {dev.HolderPid}") : "";
            var v = new DacView
            {
                Id = dev.Id, Name = s.Name != "" ? s.Name : dev.Name, DeviceName = dev.Name, Manufacturer = dev.Manufacturer, Model = dev.Model,
                Transport = dev.Transport, Usb = dev.Usb, Rates = dev.Rates, Bits = dev.Bits, Channels = dev.Channels, Formats = dev.Formats,
                DsdNative = dev.DsdNative, DopRates = dev.DopRates, CurrentRate = dev.CurrentRate, Volume = dev.Volume,
                Enabled = s.Enabled, Dsd = s.Dsd, Platform = config.Platform, Holder = holder, Share = s.Share, VolumeMode = s.Volume, RoonZoneChosen = s.RoonZone
            };
            if (b != null)
            {
                v.UpnpName = b.FriendlyName();
                v.DsdMode = b.DsdMode();
                v.Exclusive = b.Sink.Exclusive;
                v.Waiting = b.Sink.Exclusive ? "" : b.Sink.Message;
                v.Squeezebox = b.Slim?.Status;
                v.SqueezeboxId = b.Slim?.Mac;
                v.Spotify = b.Spotify?.Status;
                v.Caldera = b.Caldera?.Status;
                v.Holder = b.Sink.Exclusive ? "" : holder;
                v.VolumeAvailable = b.Volume.Available;
                v.Level = b.Level;
                v.LevelDb = b.Level != null ? b.LevelDb : null;
                v.Player = b.Renderer.Now();
                var active = b.Renderer.IsActive;
                // Caldera plays to the DAC by itself: what it plays is shown, and who.
                var caldera = !active && b.Caldera?.Holding == true ? b.Caldera.Now : null;
                if (caldera != null) v.Player = caldera;
                // Roon: what its zone for this DAC plays. Through Roon Bridge (not through the bridge) it's all
                // there is to show; through the Squeezebox player, it fills in what Roon's Squeezebox support leaves out.
                string? roonOwner = null;
                var rz = ZoneFor(b, zones);
                v.RoonZone = rz?.Name ?? "";
                if (rz is { InUse: true } && caldera == null)
                {
                    var np = RoonNow(rz, b.Id);
                    if (!active) { v.Player = np; roonOwner = "Roon"; }
                    else if (v.Player is { } cur && cur.Title == "" && b.Arbiter.Owner?.Name == "Squeezebox server")
                    {
                        v.Player = cur with { Title = np.Title, Artist = np.Artist, Album = np.Album, Art = np.Art, Duration = cur.Duration > 0 ? cur.Duration : np.Duration };
                        roonOwner = "Roon (Squeezebox)";
                    }
                }
                var blocked = b.Arbiter.LastBlocked;
                v.Control = new ControlView
                {
                    Owner = caldera != null ? "Plex (Caldera)" : roonOwner ?? b.Arbiter.Owner?.Name,
                    Locked = b.Arbiter.Holding(active),
                    Blocked = blocked != null && Arbiter.Now - blocked.At < 5 * 60 * 1000 ? blocked : null
                };
            }
            list.Add(v);
        }
        string err;
        lock (gate) err = error;
        return new BridgeView
        {
            Dacs = list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Error = err, Version = Config.Version, Host = config.Hostname, Platform = config.Platform,
            Others = Others(), Services = Services()
        };
    }

    private List<string> Others() { lock (gate) return skipped.ToList(); }
}
