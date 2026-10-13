// ManagerPlayers.cs — each DAC's players besides UPnP, as switched on the page: its Squeezebox player,
// its Spotify speaker and its Plex player (Caldera, on its own port), made, replaced and closed with
// the DAC. A part of Manager (Manager.cs), sharing its state and locks.
using MandarinDacBridge.Slim;
using MandarinDacBridge.Spotify;

namespace MandarinDacBridge;

internal sealed partial class Manager
{
    // Each DAC's Caldera answers Plex on its own port: 32500, 32501, …
    private int CalderaPort(string id)
    {
        lock (calderaPorts)
        {
            if (calderaPorts.TryGetValue(id, out var p)) return p;
            p = 32500;
            while (calderaPorts.ContainsValue(p)) p++;
            return calderaPorts[id] = p;
        }
    }

    // Starts or stops a bridge's Squeezebox player, Spotify speaker and Plex player, as switched on the page.
    // A player being replaced is closed before its successor is made: two at once would share its name for
    // the renderer ("live:spotify/<dac>", which closing the old one removes), its folders and its ports.
    private void Connect(Bridge b, bool restartSpotify = false, bool restartCaldera = false)
    {
        // One at a time: another connect between the closing and the making would make a successor
        // while the old player is still closing.
        lock (connectGate)
        {
            SlimPlayer? dropSlim = null;
            SoloistPlayer? dropSpotify = null;
            Caldera.CalderaPlayer? dropCaldera = null;
            lock (servicesGate)
            {
                if (Get(b.Id) != b) return;
                if (!SqueezeliteOn && b.Slim != null) { dropSlim = b.Slim; b.Slim = null; }
                if ((!SpotifyOn || restartSpotify) && b.Spotify != null) { dropSpotify = b.Spotify; b.Spotify = null; }
                if ((!CalderaOn || restartCaldera) && b.Caldera != null) { dropCaldera = b.Caldera; b.Caldera = null; }
            }
            dropSlim?.Dispose();
            dropSpotify?.Dispose();
            dropCaldera?.Dispose();
            bool open;
            lock (servicesGate)
            {
                open = Get(b.Id) == b;
                if (open && SqueezeliteOn && b.Slim == null) b.Slim = new SlimPlayer(b, config.Hostname, () => LmsServer, config.LmsJsonPort);
                if (open && SpotifyOn && b.Spotify == null)
                {
                    b.Spotify = new SoloistPlayer(b, config.DataDir, config.Port, Soloist);
                    b.Spotify.Expired += () => { if (config.FindSoloist().StartsWith(config.SoloistDir)) _ = DownloadSoloist(); };
                }
                if (open && CalderaOn && b.Caldera == null)
                {
                    b.Caldera = new Caldera.CalderaPlayer(b, config.DataDir, CalderaPort(b.Id), CalderaSetup);
                    b.Caldera.TokenRejected += () => { if (PlexToken != "") { Log.Write("Plex: the sign-in was refused; sign in again"); Settings.Apply(new ServiceSettings { PlexToken = "" }); } };
                }
                // Caldera and Roon Bridge play to the DAC themselves: it is shared whenever nothing plays through the bridge.
                if (open) b.ForceShare = b.Caldera != null || RoonOn;
            }
            if (dropCaldera != null && b.Caldera == null) lock (calderaPorts) calderaPorts.Remove(b.Id);
            if (!open) return;
            b.ApplyShare();
            b.Notify();
        }
    }

    private void CloseBridge(string id)
    {
        Bridge? b;
        lock (gate)
        {
            if (!bridges.Remove(id, out b)) return;
        }
        Removed?.Invoke(b);
        b.Dispose();
        qobuz?.Changed();
    }
}
