// Settings.cs — what the owner chose on the page, kept in DATA_DIR/settings.json: each DAC's settings
// (by its id) and the ways in besides UPnP. The file can hold secrets (the Soloist API key, the Plex
// token): it is its owner's only, and none of them is ever sent back to the page.
using System.Text.Json;

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
    // The Roon zone whose now playing is this DAC's ("": found by name).
    public string RoonZone { get; set; } = "";
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
    // What Roon plays, from Roon's extension API (for the page and the now-playing screen).
    public bool? RoonApi { get; set; }
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
    public string? RoonZone { get; set; }
    // Not kept: the DAC's volume, set now (0–100).
    public int? Level { get; set; }
}

internal sealed class Settings
{
    private readonly string file;
    private readonly object gate = new();
    private readonly SettingsFile data;
    // settings.json is there but couldn't be read or kept aside: nothing is written over it.
    private readonly bool frozen;

    public string Dir { get; }

    public Settings(string dir)
    {
        Dir = dir;
        file = Path.Combine(dir, "settings.json");
        data = new();
        string? text = null;
        try { text = OwnerOnlyFile.Read(file); }
        catch (Exception e)
        {
            // There, but it can't be opened (its permissions, a lock): left as it is and never written over.
            frozen = true;
            Log.Write($"settings.json couldn't be opened ({e.Message}): the defaults hold, and changes aren't saved over it");
        }
        if (text != null)
        {
            try { data = JsonSerializer.Deserialize(text, BridgeJson.Default.SettingsFile) ?? new(); }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                // Not settings (cut short, edited by hand): kept aside, and said so; every DAC's settings and
                // the secrets are in it. If it can't be moved, it isn't written over either.
                frozen = !OwnerOnlyFile.KeepAside(file, e.Message);
            }
        }
        // {"dacs":null} is JSON too.
        data.Dacs ??= new();
        data.Services ??= new();
    }

    public DacSettings For(string id)
    {
        lock (gate)
        {
            var s = data.Dacs.GetValueOrDefault(id);
            return s == null ? new DacSettings() : new DacSettings { Enabled = s.Enabled, Dsd = s.Dsd, Name = s.Name, Share = s.Share, Volume = s.Volume, RoonZone = s.RoonZone };
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
            if (p.RoonZone != null) s.RoonZone = p.RoonZone.Trim()[..Math.Min(120, p.RoonZone.Trim().Length)];
            // A level alone (the volume slider, many a second) isn't kept: nothing to write.
            if (Same(s, For(id))) return s;
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
                Qobuz = data.Services.Qobuz, MacApps = data.Services.MacApps, MacDac = data.Services.MacDac, Roon = data.Services.Roon,
                RoonApi = data.Services.RoonApi
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
            if (p.RoonApi is { } ra) data.Services.RoonApi = ra;
            if (p.PlexToken != null) data.Services.PlexToken = p.PlexToken.Trim()[..Math.Min(512, p.PlexToken.Trim().Length)];
            if (p.PlexAccount != null) data.Services.PlexAccount = p.PlexAccount.Trim()[..Math.Min(200, p.PlexAccount.Trim().Length)];
            Save();
        }
    }

    private static bool Same(DacSettings a, DacSettings b) =>
        (a.Enabled, a.Dsd, a.Name, a.Share, a.Volume, a.RoonZone) == (b.Enabled, b.Dsd, b.Name, b.Share, b.Volume, b.RoonZone);

    private void Save()
    {
        if (frozen) { Log.Write("settings: not saved, so the settings.json that couldn't be read isn't written over"); return; }
        // It holds the Soloist API key and the Plex token: its owner's only.
        try { OwnerOnlyFile.Write(file, JsonSerializer.Serialize(data, BridgeJson.Default.SettingsFile)); }
        catch (Exception e) { Log.Write("settings: " + e.Message); }
    }
}
