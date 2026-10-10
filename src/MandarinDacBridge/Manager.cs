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
using System.Diagnostics;
using System.Text.Json;
using MandarinDacBridge.Audio;
using MandarinDacBridge.Slim;
using MandarinDacBridge.Spotify;
using MandarinDacBridge.Upnp;

namespace MandarinDacBridge;

internal sealed class DacSettings
{
    public bool Enabled { get; set; } = true;
    public string Dsd { get; set; } = "auto";
    public string Name { get; set; } = "";
    // Let go of the DAC while nothing plays, for Roon Bridge or another player on this machine.
    public bool Share { get; set; }
    // "dac": the apps (and the page) set the DAC's own volume, where it has one. "fixed": it is left alone.
    public string Volume { get; set; } = "dac";
}

// The ways in besides UPnP (always on). Null: not chosen yet, the environment's default holds.
internal sealed class ServiceSettings
{
    public bool? Squeezelite { get; set; }
    public string? LmsServer { get; set; }
    public bool? Spotify { get; set; }
    // Secret: written here (settings.json, readable by its owner only), never sent back to the page.
    public string? SoloistKey { get; set; }
    public bool? Caldera { get; set; }
    public bool? Qobuz { get; set; }
    // Secret, like the Soloist key: the Plex token Caldera plays with.
    public string? PlexToken { get; set; }
    // Whose it is (their Plex username): shown on the page.
    public string? PlexAccount { get; set; }
    // The Music and Spotify apps on this Mac, through the DAC Bridge output, to this DAC (macOS).
    public bool? MacApps { get; set; }
    public string? MacDac { get; set; }
    // Roon Bridge, downloaded from Roon Labs and run beside the bridge.
    public bool? Roon { get; set; }
}

internal sealed class SettingsFile
{
    public Dictionary<string, DacSettings> Dacs { get; set; } = new();
    public ServiceSettings Services { get; set; } = new();
}

internal sealed class SettingsPatch
{
    public bool? Enabled { get; set; }
    public string? Dsd { get; set; }
    public string? Name { get; set; }
    public bool? Share { get; set; }
    public string? Volume { get; set; }
    // Not kept: the DAC's volume, set now (0–100).
    public int? Level { get; set; }
}

internal sealed class Settings
{
    private readonly string file;
    private readonly object gate = new();
    private readonly SettingsFile data;

    public string Dir { get; }

    public Settings(string dir)
    {
        Dir = dir;
        file = Path.Combine(dir, "settings.json");
        try { data = JsonSerializer.Deserialize(File.ReadAllText(file), BridgeJson.Default.SettingsFile) ?? new(); }
        catch (Exception) { data = new(); }
    }

    public DacSettings For(string id)
    {
        lock (gate)
        {
            var s = data.Dacs.GetValueOrDefault(id);
            return s == null ? new DacSettings() : new DacSettings { Enabled = s.Enabled, Dsd = s.Dsd, Name = s.Name, Share = s.Share, Volume = s.Volume };
        }
    }

    public DacSettings Apply(string id, SettingsPatch p)
    {
        lock (gate)
        {
            var s = For(id);
            if (p.Enabled is { } e) s.Enabled = e;
            if (p.Dsd is "auto" or "dop" or "pcm") s.Dsd = p.Dsd;
            if (p.Name != null) s.Name = p.Name.Trim()[..Math.Min(60, p.Name.Trim().Length)];
            if (p.Share is { } sh) s.Share = sh;
            if (p.Volume is "dac" or "fixed") s.Volume = p.Volume;
            data.Dacs[id] = s;
            Save();
            return s;
        }
    }

    public ServiceSettings Services()
    {
        lock (gate)
            return new ServiceSettings
            {
                Squeezelite = data.Services.Squeezelite, LmsServer = data.Services.LmsServer, Spotify = data.Services.Spotify,
                SoloistKey = data.Services.SoloistKey, Caldera = data.Services.Caldera, PlexToken = data.Services.PlexToken, PlexAccount = data.Services.PlexAccount,
                Qobuz = data.Services.Qobuz, MacApps = data.Services.MacApps, MacDac = data.Services.MacDac, Roon = data.Services.Roon
            };
    }

    public void Apply(ServiceSettings p)
    {
        lock (gate)
        {
            if (p.Squeezelite is { } q) data.Services.Squeezelite = q;
            if (p.LmsServer != null) data.Services.LmsServer = p.LmsServer.Trim()[..Math.Min(200, p.LmsServer.Trim().Length)];
            if (p.Spotify is { } sp) data.Services.Spotify = sp;
            if (p.SoloistKey != null) data.Services.SoloistKey = p.SoloistKey.Trim()[..Math.Min(512, p.SoloistKey.Trim().Length)];
            if (p.Caldera is { } c) data.Services.Caldera = c;
            if (p.Qobuz is { } qb) data.Services.Qobuz = qb;
            if (p.MacApps is { } ma) data.Services.MacApps = ma;
            if (p.MacDac != null) data.Services.MacDac = p.MacDac;
            if (p.Roon is { } rn) data.Services.Roon = rn;
            if (p.PlexToken != null) data.Services.PlexToken = p.PlexToken.Trim()[..Math.Min(512, p.PlexToken.Trim().Length)];
            if (p.PlexAccount != null) data.Services.PlexAccount = p.PlexAccount.Trim()[..Math.Min(200, p.PlexAccount.Trim().Length)];
            Save();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(data, BridgeJson.Default.SettingsFile));
            // It can hold the Soloist API key: its owner's only.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) { Log.Write("settings: " + e.Message); }
    }
}

internal sealed class Bridge : IDisposable
{
    private readonly Config config;
    private readonly Settings settings;

    public string Id { get; }
    public DacDevice Dev { get; set; }
    public string Udn { get; }
    public ISink Sink { get; }
    public Renderer Renderer { get; }
    public Arbiter Arbiter { get; }
    public event Action? Changed;
    public event Action? Gone;
    // The DAC's volume moved (from an app, the page, or elsewhere on this machine): 0–100.
    public event Action<int>? VolumeChanged;
    private bool wasExclusive;
    private readonly Timer shareTimer;
    private int? level;

    // The DAC's own volume (Audio/Volume.cs), where it has one.
    public IDacVolume Volume { get; }

    public SlimPlayer? Slim { get; set; }
    public SoloistPlayer? Spotify { get; set; }
    public Caldera.CalderaPlayer? Caldera { get; set; }
    // On while something else on this machine (Caldera) plays to the DAC by itself: the DAC is shared when idle.
    public bool ForceShare { get; set; }

    public Bridge(DacDevice dev, Config config, Settings settings)
    {
        this.config = config;
        this.settings = settings;
        Id = dev.Id;
        Dev = dev;
        Udn = Description.UdnFor(config.Hostname, dev.Key);
        Arbiter = new Arbiter(config.LockGrace, config.MandarinPort);
        Sink = SinkFactory.Create(dev, config);
        Renderer = new Renderer(Sink, config.Ffmpeg, () => new DacTraits(Dev.Rates, Dev.Channels, DsdMode()), Arbiter, Log);
        Renderer.Changed += Notify;
        Sink.StatusChanged += () =>
        {
            if (Sink.Exclusive != wasExclusive)
            {
                Log(Sink.Exclusive ? "exclusive: the DAC is the bridge's" : $"waiting: {Sink.Message}");
                wasExclusive = Sink.Exclusive;
            }
            Notify();
        };
        Sink.Gone += () => ThreadPool.QueueUserWorkItem(_ => Gone?.Invoke());
        if (settings.For(Id).Share) { sharing = true; Sink.Share(true); }
        Volume = VolumeFactory.Create(dev, config);
        if (Volume.Available)
        {
            level = Volume.Read();
            Log($"volume: the DAC's own ({Volume.Control}){(level is { } l ? $", at {l}%" : "")}");
        }
        shareTimer = new Timer(_ => { ShareIfIdle(); RefreshVolume(); }, null, 1000, 1000);
    }

    // The apps set the DAC's own volume: it has one, and it isn't set to be left alone.
    public bool VolumeOn => Volume.Available && settings.For(Id).Volume != "fixed";

    // 0–100 while the volume is the apps' to set; null when it stays where it is (100 to the apps).
    public int? Level => VolumeOn ? level : null;

    public double? LevelDb => VolumeOn ? Volume.ReadDb() : null;

    public bool SetLevel(int to)
    {
        if (!VolumeOn) return false;
        to = Math.Clamp(to, 0, 100);
        if (!Volume.Set(to)) { Log($"volume: couldn't set it to {to}%"); return false; }
        Moved(Volume.Read() ?? to);
        return true;
    }

    // Changed elsewhere too (alsamixer, the Mac's or Windows' own volume): followed every second.
    private void RefreshVolume()
    {
        if (!VolumeOn) return;
        try { if (Volume.Read() is { } v) Moved(v); }
        catch (Exception) { /* the DAC is going */ }
    }

    private void Moved(int v)
    {
        if (v == level) return;
        level = v;
        VolumeChanged?.Invoke(v);
        Notify();
    }

    public bool Idle => Renderer.Transport is "STOPPED" or "NO_MEDIA_PRESENT" && !Arbiter.Holding(false);

    // Shared: let go of the DAC once nothing plays (and no app is in its grace).
    private void ShareIfIdle()
    {
        if (!(settings.For(Id).Share || ForceShare) || !Sink.Exclusive || !Idle) return;
        Log("idle: letting go of the DAC for other players (shared)");
        Sink.Share(true);
    }

    private bool sharing;

    public void SetShare(bool on)
    {
        if (on || ForceShare) { sharing = true; ShareIfIdle(); }
        else if (sharing)
        {
            sharing = false;
            Sink.Share(false);
            Log("not shared: holding the DAC again");
        }
    }

    // The share setting as it stands (after Caldera was switched on or off).
    public void ApplyShare() => SetShare(settings.For(Id).Share);

    public void Log(string m) => MandarinDacBridge.Log.Write($"[{Dev.Name}] {m}");
    public void Notify() => Changed?.Invoke();
    public void Start() => Sink.Start();

    // DoP where asked; on "auto", where the DAC shows it knows DSD (native DSD formats, Linux).
    public string DsdMode()
    {
        var s = settings.For(Id).Dsd;
        return s is "dop" or "pcm" ? s : Dev.DsdNative.Length > 0 ? "dop" : "pcm";
    }

    public string FriendlyName()
    {
        var s = settings.For(Id);
        return (s.Name != "" ? s.Name : Dev.Name) + config.NameSuffix;
    }

    public string ProtocolInfo() => Control.ProtocolInfo(Dev.Rates, Dev.Bits);

    public Advert Advert => new(Udn, $"/upnp/{Id}/description.xml");

    public void Dispose()
    {
        shareTimer.Dispose();
        Volume.Dispose();
        Slim?.Dispose();
        Spotify?.Dispose();
        Caldera?.Dispose();
        try { Renderer.Halt().Wait(TimeSpan.FromSeconds(6)); } catch (Exception) { /* closing anyway */ }
        Renderer.Dispose();
        Sink.Dispose();
    }
}

internal sealed class Manager(Config config) : IDisposable
{
    private readonly object gate = new();
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
    private string roonDownload = "";
    private int roonDownloading;

    public Settings Settings { get; } = new(config.DataDir);
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
        new(Caldera.CalderaDownload.Program(config.DataDir), Caldera.CalderaDownload.Home(config.DataDir), PlexToken);

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

    // Starts or stops a bridge's Squeezebox player and Spotify speaker, as switched on the page.
    private void Connect(Bridge b, bool restartSpotify = false, bool restartCaldera = false)
    {
        SlimPlayer? dropSlim = null;
        SoloistPlayer? dropSpotify = null;
        Caldera.CalderaPlayer? dropCaldera = null;
        lock (servicesGate)
        {
            if (Get(b.Id) != b) return;
            if (SqueezeliteOn && b.Slim == null) b.Slim = new SlimPlayer(b, config.Hostname, () => LmsServer);
            else if (!SqueezeliteOn && b.Slim != null) { dropSlim = b.Slim; b.Slim = null; }
            if ((!SpotifyOn || restartSpotify) && b.Spotify != null) { dropSpotify = b.Spotify; b.Spotify = null; }
            if (SpotifyOn && b.Spotify == null)
            {
                b.Spotify = new SoloistPlayer(b, config.DataDir, config.Port, Soloist);
                b.Spotify.Expired += () => { if (config.FindSoloist().StartsWith(config.SoloistDir)) _ = DownloadSoloist(); };
            }
            if ((!CalderaOn || restartCaldera) && b.Caldera != null) { dropCaldera = b.Caldera; b.Caldera = null; }
            if (CalderaOn && b.Caldera == null)
            {
                b.Caldera = new Caldera.CalderaPlayer(b, config.DataDir, CalderaPort(b.Id), CalderaSetup);
                b.Caldera.TokenRejected += () => { if (PlexToken != "") { Log.Write("Plex: the sign-in was refused; sign in again"); Settings.Apply(new ServiceSettings { PlexToken = "" }); } };
            }
            // Caldera and Roon Bridge play to the DAC themselves: it is shared whenever nothing plays through the bridge.
            b.ForceShare = b.Caldera != null || RoonOn;
        }
        dropSlim?.Dispose();
        dropSpotify?.Dispose();
        dropCaldera?.Dispose();
        if (dropCaldera != null && b.Caldera == null) lock (calderaPorts) calderaPorts.Remove(b.Id);
        b.ApplyShare();
        b.Notify();
    }

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

    // The cover for what plays on this DAC, when the page can't fetch it itself: a URL (Caldera's, with the
    // Plex token) or a file (the Music app's).
    public string? Art(string id)
    {
        if (Get(id)?.Caldera?.ArtUrl is { Length: > 0 } u) return u;
        if (mac?.ArtFile is { Length: > 0 } f) return f;
        return null;
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
            Qobuz = QobuzOn, QobuzInstalled = qobuz?.Installed ?? false, QobuzInstall = qobuz?.Install ?? "",
            QobuzStatus = qobuz?.Status ?? "", QobuzSignedIn = qobuz?.SignedIn ?? false, QobuzWebPort = Qobuz.QobuzConnect.WebPort,
            MacPossible = OperatingSystem.IsMacOS(), MacApps = MacOn,
            MacDriver = OperatingSystem.IsMacOS() && Native.CoreAudio.FindDevice(Mac.MacSource.LoopbackUid) != Native.CoreAudio.Unknown, MacDac = MacTarget()?.Id ?? "", MacStatus = mac?.Status ?? "",
            RoonBridge = RoonBridge.Running(),
            Roon = RoonOn, RoonPossible = Roon.RoonDownload.Package != null, RoonVersion = Roon.RoonDownload.Version(config.DataDir),
            RoonDownload = roonDownload, RoonStatus = roon?.Status ?? ""
        };
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

    public DacSettings SetSettings(string id, SettingsPatch patch)
    {
        var s = Settings.Apply(id, patch);
        DacDevice? dev;
        Bridge? b;
        lock (gate) { dev = devices.GetValueOrDefault(id); b = bridges.GetValueOrDefault(id); }
        if (dev != null && patch.Enabled is { } on)
        {
            if (on && b == null) Open(dev);
            if (!on) CloseBridge(id);
        }
        if (patch.Name != null && b != null && s.Enabled)
        {
            Renamed?.Invoke(b);
            qobuz?.Changed();
            b.Slim?.Renamed();
            if (b.Spotify != null) Connect(b, restartSpotify: true);
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

    // Everything the page shows.
    public BridgeView View()
    {
        var list = new List<DacView>();
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
                Enabled = s.Enabled, Dsd = s.Dsd, Platform = config.Platform, Holder = holder, Share = s.Share, VolumeMode = s.Volume
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
                var blocked = b.Arbiter.LastBlocked;
                v.Control = new ControlView
                {
                    Owner = caldera != null ? "Plex (Caldera)" : b.Arbiter.Owner?.Name,
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

    public void Dispose()
    {
        timer?.Dispose();
        qobuz?.Dispose();
        mac?.Dispose();
        roon?.Dispose();
        foreach (var b in Bridges()) b.Dispose();
    }
}

// Roon Bridge (or Roon Server) on this machine: shown on the page, so "share when idle" makes sense.
internal static class RoonBridge
{
    private static readonly string[] Names = ["RoonBridge", "RAATServer", "RoonServer", "RoonAppliance"];
    private static (long At, string Found) cache = (long.MinValue, "");

    // The Roon program running here ("RoonBridge"), or "" (also in Docker, which can't see the host's programs).
    public static string Running(bool fresh = false)
    {
        var c = cache;
        if (!fresh && Environment.TickCount64 - c.At < 10_000) return c.Found;
        var found = "";
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string name;
                    try { name = p.ProcessName; } catch (Exception) { continue; }
                    var n = Names.FirstOrDefault(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
                    if (n != null) { found = n is "RAATServer" ? "RoonBridge" : n; break; }
                }
            }
        }
        catch (Exception) { /* not allowed to look */ }
        cache = (Environment.TickCount64, found);
        return found;
    }
}
