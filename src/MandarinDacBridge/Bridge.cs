// Bridge.cs — one DAC's bridge: the sink holding it, the renderer playing to it, the arbiter guarding
// it, its UPnP face, and the Squeezebox player, Spotify speaker and Plex player switched on for it.
// Made and closed by Manager.cs.
using MandarinDacBridge.Audio;
using MandarinDacBridge.Slim;
using MandarinDacBridge.Spotify;
using MandarinDacBridge.Upnp;

namespace MandarinDacBridge;

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
