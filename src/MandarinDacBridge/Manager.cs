// Manager.cs — the DACs found, and one bridge for each: a sink holding the
// DAC, a renderer playing to it, an arbiter guarding it, and its UPnP face.
//
// Looked for every few seconds, so a DAC plugged in later appears by itself
// (and on the network a moment after), and one unplugged goes. A DAC can be
// switched off on its page: the bridge lets go of it and stops offering it.
using System.Text.Json;
using MandarinDacBridge.Audio;
using MandarinDacBridge.Upnp;

namespace MandarinDacBridge;

internal sealed class DacSettings
{
    public bool Enabled { get; set; } = true;
    public string Dsd { get; set; } = "auto";
    public string Name { get; set; } = "";
}

internal sealed class SettingsFile
{
    public Dictionary<string, DacSettings> Dacs { get; set; } = new();
}

internal sealed class SettingsPatch
{
    public bool? Enabled { get; set; }
    public string? Dsd { get; set; }
    public string? Name { get; set; }
}

internal sealed class Settings
{
    private readonly string file;
    private readonly object gate = new();
    private readonly SettingsFile data;

    public Settings(string dir)
    {
        file = Path.Combine(dir, "settings.json");
        try { data = JsonSerializer.Deserialize(File.ReadAllText(file), BridgeJson.Default.SettingsFile) ?? new(); }
        catch (Exception) { data = new(); }
    }

    public DacSettings For(string id)
    {
        lock (gate)
        {
            var s = data.Dacs.GetValueOrDefault(id);
            return s == null ? new DacSettings() : new DacSettings { Enabled = s.Enabled, Dsd = s.Dsd, Name = s.Name };
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
            data.Dacs[id] = s;
            try { File.WriteAllText(file, JsonSerializer.Serialize(data, BridgeJson.Default.SettingsFile)); }
            catch (Exception e2) { Log.Write("settings: " + e2.Message); }
            return s;
        }
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
    private bool wasExclusive;

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
    }

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

    public string ProtocolInfo() => Control.ProtocolInfo(Dev.Rates);

    public Advert Advert => new(Udn, $"/upnp/{Id}/description.xml");

    public void Dispose()
    {
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
        if (patch.Name != null && b != null && s.Enabled) Renamed?.Invoke(b);
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
                Enabled = s.Enabled, Dsd = s.Dsd, Platform = config.Platform, Holder = holder
            };
            if (b != null)
            {
                v.UpnpName = b.FriendlyName();
                v.DsdMode = b.DsdMode();
                v.Exclusive = b.Sink.Exclusive;
                v.Waiting = b.Sink.Exclusive ? "" : b.Sink.Message;
                v.Holder = b.Sink.Exclusive ? "" : holder;
                v.Player = b.Renderer.Now();
                var active = b.Renderer.IsActive;
                var blocked = b.Arbiter.LastBlocked;
                v.Control = new ControlView
                {
                    Owner = b.Arbiter.Owner?.Name,
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
            Others = Others()
        };
    }

    private List<string> Others() { lock (gate) return skipped.ToList(); }

    public void Dispose()
    {
        timer?.Dispose();
        foreach (var b in Bridges()) b.Dispose();
    }
}
