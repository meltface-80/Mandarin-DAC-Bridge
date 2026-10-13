// Manager.cs — the DACs found, and one bridge for each: a sink holding the
// DAC, a renderer playing to it, an arbiter guarding it, and its UPnP face.
//
// Looked for every few seconds, so a DAC plugged in later appears by itself
// (and on the network a moment after), and one unplugged goes. A DAC can be
// switched off on its page: the bridge lets go of it and stops offering it.
//
// Besides UPnP, each bridge can be a Squeezebox player (Slim/) and a Spotify
// Connect speaker through Spotify Soloist (Spotify/), switched on for all DACs
// on the page. A DAC
// set to "share when idle" is let go whenever nothing plays, so Roon Bridge
// (or any player on this machine) can use it in between.
using MandarinDacBridge.Slim;
using MandarinDacBridge.Spotify;

namespace MandarinDacBridge;

internal sealed partial class Manager : IDisposable
{
    private readonly Config config;
    private readonly object gate = new();
    private readonly object connectGate = new();   // Connect, one at a time (taken before servicesGate)
    private readonly Dictionary<string, DacDevice> devices = new();
    private readonly Dictionary<string, Bridge> bridges = new();
    private Timer? timer;
    private int scanning;
    private string error = "";
    private List<string> skipped = [];
    private readonly object servicesGate = new();
    private string soloistDownload = "";
    private int downloading;
    private string calderaDownload = "";
    private int calderaDownloading;
    private readonly Dictionary<string, int> calderaPorts = new();
    private Caldera.PlexLink? plexLink;
    private Qobuz.QobuzConnect? qobuz;
    private Mac.MacSource? mac;
    private Roon.RoonRunner? roon;
    private Roon.RoonApi? roonApi;
    private string roonDownload = "";
    private int roonDownloading;

    public Settings Settings { get; }

    public Manager(Config config)
    {
        this.config = config;
        Settings = new(config.DataDir);
    }
    public event Action<Bridge>? Added;
    public event Action<Bridge>? Removed;
    public event Action<Bridge>? Renamed;
    public event Action<Bridge>? BridgeChanged;

    public Bridge? Get(string id) { lock (gate) return bridges.GetValueOrDefault(id); }
    public List<Bridge> Bridges() { lock (gate) return bridges.Values.ToList(); }
    public bool Has(string id) { lock (gate) return devices.ContainsKey(id); }
    public int Count { get { lock (gate) return devices.Count; } }

    public void Start()
    {
        if (PlexToken != "" && (Settings.Services().PlexAccount ?? "") == "") _ = LookUpPlexAccount(PlexToken);
        ConnectQobuz();
        ConnectMac();
        ConnectRoon();
        ConnectRoonApi();
        Scan();
        timer = new Timer(_ => Scan(), null, config.ScanEvery, config.ScanEvery);
    }

    public void Scan()
    {
        if (Interlocked.Exchange(ref scanning, 1) == 1) return;
        try
        {
            var (found, err, skippedNow) = Devices.ListWithSkipped(config);
            var open = new List<DacDevice>();
            var close = new List<string>();
            lock (gate)
            {
                error = err;
                foreach (var x in skippedNow.Except(skipped)) Log.Write("seen, not bridged: " + x);
                skipped = skippedNow;
                var seen = new HashSet<string>();
                foreach (var dev in found)
                {
                    seen.Add(dev.Id);
                    bool known = devices.ContainsKey(dev.Id);
                    devices[dev.Id] = dev;
                    if (bridges.TryGetValue(dev.Id, out var b)) b.Dev = dev;
                    if (!known) Log.Write($"found {dev.Name} ({dev.Transport}{(dev.Usb != null ? " " + dev.Usb : "")})");
                    bool want = Settings.For(dev.Id).Enabled;
                    if (want && b == null) open.Add(dev);
                    if (!want && b != null) close.Add(dev.Id);
                }
                foreach (var id in devices.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    Log.Write($"{devices[id].Name} is gone");
                    devices.Remove(id);
                    close.Add(id);
                }
            }
            foreach (var id in close) CloseBridge(id);
            foreach (var dev in open) Open(dev);
        }
        catch (Exception e) { lock (gate) error = e.Message; }
        finally { Volatile.Write(ref scanning, 0); }
    }

    private void Open(DacDevice dev)
    {
        var b = new Bridge(dev, config, Settings);
        lock (gate)
        {
            if (bridges.ContainsKey(dev.Id)) { b.Dispose(); return; }
            bridges[dev.Id] = b;
        }
        b.Changed += () => BridgeChanged?.Invoke(b);
        b.Gone += () =>
        {
            lock (gate) devices.Remove(dev.Id);
            CloseBridge(dev.Id);
        };
        b.Start();
        Added?.Invoke(b);
        Connect(b);
        qobuz?.Changed();
    }

    // ------------------------------------------------------------ Squeezebox and Spotify

    public bool SqueezeliteOn => Settings.Services().Squeezelite ?? config.Squeezelite;
    public string LmsServer => Settings.Services().LmsServer ?? config.LmsServer;
    public bool SpotifyOn => Settings.Services().Spotify ?? config.Spotify;
    private string SoloistKey => Settings.Services().SoloistKey is { Length: > 0 } k ? k : config.SoloistKey;

    private SoloistSetup Soloist() => new(config.FindSoloist(), Config.FindPulseAudio(), SoloistKey);

    public bool CalderaOn => Settings.Services().Caldera ?? config.Caldera;
    public bool QobuzOn => Settings.Services().Qobuz ?? config.Qobuz;
    public bool MacOn => OperatingSystem.IsMacOS() && (Settings.Services().MacApps ?? config.MacApps);
    public bool RoonOn => Settings.Services().Roon ?? config.Roon;
    public bool RoonApiOn => Settings.Services().RoonApi ?? config.RoonApi;

    // Roon's extension API, while switched on: what each Roon zone plays.
    private void ConnectRoonApi()
    {
        Roon.RoonApi? drop = null;
        lock (servicesGate)
        {
            if (RoonApiOn && roonApi == null)
            {
                roonApi = new Roon.RoonApi(config.DataDir, config.RoonCore);
                roonApi.Changed += () => { foreach (var b in Bridges()) b.Notify(); };
            }
            else if (!RoonApiOn && roonApi != null) { drop = roonApi; roonApi = null; }
        }
        drop?.Dispose();
    }

    // Roon Bridge beside the bridge, while switched on (Roon/RoonBridgeHost.cs).
    private void ConnectRoon()
    {
        Roon.RoonRunner? drop = null;
        lock (servicesGate)
        {
            if (RoonOn && roon == null) roon = new Roon.RoonRunner(config.DataDir);
            else if (!RoonOn && roon != null) { drop = roon; roon = null; }
        }
        drop?.Dispose();
    }

    // Fetches Roon Bridge from Roon Labs (the page's Download), as RoPieee does.
    public async Task DownloadRoon()
    {
        if (Interlocked.Exchange(ref roonDownloading, 1) == 1) return;
        try
        {
            var v = await Roon.RoonDownload.Fetch(config.DataDir, m => roonDownload = m, CancellationToken.None);
            roonDownload = "";
            Log.Write($"Roon Bridge {v} from Roon Labs");
        }
        catch (Exception e)
        {
            roonDownload = "download failed: " + e.Message;
            Log.Write("Roon Bridge: " + roonDownload);
        }
        finally { Volatile.Write(ref roonDownloading, 0); }
    }

    // The Music and Spotify apps through the DAC Bridge output, while switched on (macOS).
    private void ConnectMac()
    {
        Mac.MacSource? drop = null;
        lock (servicesGate)
        {
            if (MacOn && mac == null) mac = new Mac.MacSource(config.DataDir, MacTarget);
            else if (!MacOn && mac != null) { drop = mac; mac = null; }
        }
        drop?.Dispose();
    }

    // The DAC chosen for the Mac's apps, else the first.
    private Bridge? MacTarget() =>
        Settings.Services().MacDac is { Length: > 0 } id && Get(id) is { } b ? b : Bridges().OrderBy(x => x.FriendlyName(), StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    // One QobuzProxy for all the DACs, while Qobuz is switched on.
    private void ConnectQobuz()
    {
        Qobuz.QobuzConnect? drop = null;
        lock (servicesGate)
        {
            if (QobuzOn && qobuz == null)
            {
                qobuz = new Qobuz.QobuzConnect(config.DataDir, QobuzSpeakers);
                for (int i = 0; i < 64; i++) Arbiter.StreamPorts[Qobuz.QobuzConnect.FirstProxyPort + i] = "Qobuz";
            }
            else if (!QobuzOn && qobuz != null) { drop = qobuz; qobuz = null; Arbiter.StreamPorts.Clear(); }
        }
        drop?.Dispose();
    }

    private List<Qobuz.QobuzSpeaker> QobuzSpeakers() =>
        Bridges().OrderBy(b => b.Dev.Key, StringComparer.Ordinal)
            .Select(b => new Qobuz.QobuzSpeaker(b.Id, b.Dev.Key, b.FriendlyName(),
                $"http://127.0.0.1:{config.Port}/upnp/{b.Id}/description.xml", "127.0.0.1", config.Port, FixedVolume: !b.VolumeOn))
            .ToList();

    public async Task InstallQobuz()
    {
        Qobuz.QobuzConnect? q;
        lock (servicesGate) q = qobuz;
        if (q == null) return;
        await q.InstallProxy();
    }
    private string PlexToken => Settings.Services().PlexToken ?? "";
    private Caldera.CalderaSetup CalderaSetup() =>
        new(Caldera.CalderaDownload.Program(config.DataDir), Caldera.CalderaDownload.Home(config.DataDir), PlexToken, PlexClientId);

    public ServicesView SetServices(ServiceSettings patch)
    {
        bool lmsChanged = patch.LmsServer != null && patch.LmsServer.Trim() != LmsServer;
        bool keyChanged = patch.SoloistKey != null && patch.SoloistKey.Trim() != SoloistKey;
        bool plexChanged = patch.PlexToken != null && patch.PlexToken.Trim() != PlexToken;
        // A new Plex sign-in: whose it is is looked up again (shown on the page, so a wrong account is plain to see).
        if (plexChanged) patch.PlexAccount = "";
        Settings.Apply(patch);
        if (plexChanged && PlexToken != "") _ = LookUpPlexAccount(PlexToken);
        foreach (var b in Bridges())
        {
            // A new server: the players start again, to find it.
            if (lmsChanged && b.Slim != null)
            {
                SlimPlayer? old;
                lock (servicesGate) { old = b.Slim; b.Slim = null; }
                old?.Dispose();
            }
            Connect(b, restartSpotify: keyChanged, restartCaldera: plexChanged);
        }
        ConnectQobuz();
        ConnectMac();
        ConnectRoon();
        ConnectRoonApi();
        return Services();
    }

    // Fetches Caldera Headless from Caldera (the page's Download), then starts the players again.
    public async Task DownloadCaldera()
    {
        if (Interlocked.Exchange(ref calderaDownloading, 1) == 1) return;
        try
        {
            calderaDownload = "downloading from Caldera…";
            var v = await Caldera.CalderaDownload.Fetch(config.DataDir, CancellationToken.None);
            calderaDownload = "";
            Log.Write($"Caldera Headless {v} downloaded");
            foreach (var b in Bridges()) if (b.Caldera != null) Connect(b, restartCaldera: true);
        }
        catch (Exception e)
        {
            calderaDownload = "download failed: " + e.Message;
            Log.Write("Caldera Headless: " + calderaDownload);
        }
        finally { Volatile.Write(ref calderaDownloading, 0); }
    }

    private string PlexClientId => "mandarin-dac-bridge-" + Devices.IdOf(config.Hostname);

    private async Task LookUpPlexAccount(string token)
    {
        var who = await Caldera.PlexLink.Account(token, PlexClientId);
        if (who == "" || PlexToken != token) return;
        Settings.Apply(new ServiceSettings { PlexAccount = who });
        Log.Write($"Plex: signed in as {who}");
        foreach (var b in Bridges()) b.Notify();
    }

    // Signing out of Plex: the token is forgotten (here and in Caldera's settings), the Caldera players wait for
    // a new sign-in.
    public void SignOutPlex()
    {
        plexLink?.Cancel();
        SetServices(new ServiceSettings { PlexToken = "" });
        try
        {
            var players = Path.Combine(Caldera.CalderaDownload.Home(config.DataDir), "players");
            if (Directory.Exists(players)) Directory.Delete(players, true);
        }
        catch (Exception e) { Log.Write("Plex: couldn't clear Caldera's settings: " + e.Message); }
        Log.Write("Plex: signed out");
    }

    // Signing in to Plex: a code for plex.tv/link; the token, once there, goes to the Caldera players.
    public async Task LinkPlex()
    {
        plexLink ??= new Caldera.PlexLink(PlexClientId, token =>
        {
            Log.Write("Plex: signed in");
            SetServices(new ServiceSettings { PlexToken = token });
        });
        await plexLink.Start();
    }

    // Fetches Spotify Soloist from Spotify (the page's Download, or an expired build), then starts the speakers again.
    public async Task DownloadSoloist()
    {
        if (Interlocked.Exchange(ref downloading, 1) == 1) return;
        try
        {
            soloistDownload = "downloading from Spotify…";
            var path = await SoloistDownload.Fetch(config.SoloistDir, CancellationToken.None);
            soloistDownload = "";
            Log.Write($"Spotify Soloist downloaded: {SoloistDownload.Version(path)}");
            foreach (var b in Bridges()) if (b.Spotify != null) Connect(b, restartSpotify: true);
        }
        catch (Exception e)
        {
            soloistDownload = "download failed: " + e.Message;
            Log.Write("Spotify Soloist: " + soloistDownload);
        }
        finally { Volatile.Write(ref downloading, 0); }
    }

    public DacSettings SetSettings(string id, SettingsPatch patch)
    {
        var before = Settings.For(id).Name;
        var s = Settings.Apply(id, patch);
        DacDevice? dev;
        Bridge? b;
        lock (gate) { dev = devices.GetValueOrDefault(id); b = bridges.GetValueOrDefault(id); }
        if (dev != null && patch.Enabled is { } on)
        {
            if (on && b == null) Open(dev);
            if (!on) CloseBridge(id);
        }
        if (patch.Name != null && s.Name != before && b != null && s.Enabled)
        {
            Renamed?.Invoke(b);
            qobuz?.Changed();
            b.Slim?.Renamed();
            // Spotify and Plex take the name only as they start: restarted, which stops what they play.
            if (b.Spotify != null || b.Caldera != null) Connect(b, restartSpotify: b.Spotify != null, restartCaldera: b.Caldera != null);
        }
        if (patch.Share is { } share && b != null) b.SetShare(share);
        if (patch.Volume != null && b != null)
        {
            b.Log(b.VolumeOn ? "volume: the apps set the DAC's own volume" : "volume: left where it is (fixed)");
            qobuz?.Changed();
            b.Notify();
        }
        if (patch.Level is { } lv && b != null) b.SetLevel(lv);
        return s;
    }

    public void Release(string id)
    {
        var b = Get(id);
        if (b == null) return;
        b.Arbiter.Release();
        b.Notify();
    }

    public void Dispose()
    {
        timer?.Dispose();
        qobuz?.Dispose();
        mac?.Dispose();
        roon?.Dispose();
        roonApi?.Dispose();
        foreach (var b in Bridges()) b.Dispose();
    }
}
