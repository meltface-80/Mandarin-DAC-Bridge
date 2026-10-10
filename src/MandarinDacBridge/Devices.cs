// Devices.cs — the USB DACs plugged into this machine, and what each says it
// can take.
//
//   Linux  /proc/asound: each USB audio card's stream file lists its playback
//          formats (S32_LE, S24_3LE…, or DSD_U32_BE for native DSD), channels
//          and rates exactly as the DAC reports them. Opened as
//          hw:CARD=<id>,DEV=0. In Docker this needs /dev/snd passed in.
//   macOS  Core Audio: the physical formats (rate ranges, bits, integer or
//          float), the nominal rates, the transport (USB) and which process
//          has the DAC in hog mode.
//   Windows the MMDevice API and WASAPI exclusive mode (DevicesWindows.cs).
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MandarinDacBridge.Native;

namespace MandarinDacBridge;

internal sealed class DacDevice
{
    public string Key { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Transport { get; set; } = "USB";
    public string? Usb { get; set; }
    public string Spec { get; set; } = "";             // hw:CARD=…,DEV=0, or the Core Audio UID
    public int[] Rates { get; set; } = [];
    public int[] Bits { get; set; } = [];
    public int Channels { get; set; } = 2;
    public string[] Formats { get; set; } = [];
    public int[] DsdNative { get; set; } = [];
    public int[] DopRates { get; set; } = [];
    public int CurrentRate { get; set; }
    public double? Volume { get; set; }
    // Tests only: a DAC with a volume control of its own, at this level.
    public int? TestVolume { get; set; }
    public int HolderPid { get; set; }
    public string HolderName { get; set; } = "";
}

// What Core Audio says about one output device (read in-process on a Mac; given in tests).
internal sealed record MacRawDevice(string Uid, string Name, string Manufacturer, string Transport, int Channels, double Rate,
    int Hog, double? Volume, (double Min, double Max)[] Rates, MacRawFormat[] Formats);

internal sealed record MacRawFormat(string Id, double Min, double Max, int Bits, bool Float, int Channels);

internal static partial class Devices
{
    public static readonly int[] StandardRates = [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000, 1411200, 1536000];
    public static readonly int[] DopCandidates = [176400, 352800, 705600, 1411200];

    public static string IdOf(string key) => "dac-" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..10];

    public static string KHz(int r) => (r % 1000 == 0 ? (r / 1000).ToString() : (r / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + " kHz";

    private static string Read(string f) { try { return File.ReadAllText(f); } catch (Exception) { return ""; } }

    // ------------------------------------------------------------------ Linux

    internal sealed record AltSet(string Format, int Channels, int[] Rates, int Bits);

    public static (List<AltSet> Alts, bool Running, int Freq) ParseStream(string text)
    {
        var play = Regex.Split(text, "^Capture:", RegexOptions.Multiline)[0];
        var parts = Regex.Split(play, "^Playback:", RegexOptions.Multiline);
        var sec = parts.Length > 1 ? parts[1] : "";
        var alts = new List<AltSet>();
        string format = ""; int channels = 0, bits = 0; int[] rates = [];
        bool inAlt = false;
        void Flush() { if (inAlt) alts.Add(new AltSet(format, channels, rates, bits)); }
        foreach (var raw in sec.Split('\n'))
        {
            var t = raw.Trim();
            Match m;
            if (Regex.IsMatch(t, @"^Altset\s+\d+")) { Flush(); inAlt = true; format = ""; channels = 0; bits = 0; rates = []; continue; }
            if (!inAlt) continue;
            if ((m = Regex.Match(t, @"^Format:\s*(.+)$")).Success) format = m.Groups[1].Value.Trim();
            else if ((m = Regex.Match(t, @"^Channels:\s*(\d+)")).Success) channels = int.Parse(m.Groups[1].Value);
            else if ((m = Regex.Match(t, @"^Bits:\s*(\d+)")).Success) bits = int.Parse(m.Groups[1].Value);
            else if ((m = Regex.Match(t, @"^Rates:\s*(.+)$")).Success)
            {
                var r = Regex.Match(m.Groups[1].Value, @"(\d+)\s*-\s*(\d+)\s*\(continuous\)");
                rates = r.Success
                    ? StandardRates.Where(x => x >= int.Parse(r.Groups[1].Value) && x <= int.Parse(r.Groups[2].Value)).ToArray()
                    : m.Groups[1].Value.Split(',').Select(x => int.TryParse(x.Trim(), out var n) ? n : 0).Where(x => x > 0).ToArray();
            }
        }
        Flush();
        var status = Regex.Match(sec, @"Status:\s*(\w+)");
        var freq = Regex.Match(sec, @"Momentary freq\s*=\s*(\d+)");
        return (alts, status.Success && status.Groups[1].Value == "Running", freq.Success ? int.Parse(freq.Groups[1].Value) : 0);
    }

    private static int BitsOf(AltSet a)
    {
        if (a.Bits > 0) return a.Bits;
        var m = Regex.Match(a.Format, "S(16|24|32)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    public static List<DacDevice> ListLinux(string root = "/proc/asound") => ListLinux(root, []);

    public static List<DacDevice> ListLinux(string root, List<string> skipped)
    {
        var cards = Read(Path.Combine(root, "cards"));
        if (cards.Trim() == "" || cards.Contains("no soundcards", StringComparison.OrdinalIgnoreCase))
            skipped.Add("no sound cards at all in " + root + (File.Exists("/.dockerenv") ? " (in Docker: is /dev/snd passed in, and is the DAC plugged into this machine?)" : ""));
        var lines = cards.Split('\n');
        var list = new List<DacDevice>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = CardLine().Match(lines[i]);
            if (!m.Success) continue;
            string num = m.Groups[1].Value, cardId = m.Groups[2].Value, driver = m.Groups[3].Value, shortName = m.Groups[4].Value.Trim();
            var dir = Path.Combine(root, "card" + num);
            var usbid = Read(Path.Combine(dir, "usbid")).Trim();
            if (driver != "USB-Audio" && usbid == "") { skipped.Add($"{shortName} — {driver}, not USB"); continue; }
            var longName = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
            var s = ParseStream(Read(Path.Combine(dir, "stream0")));
            var pcm = s.Alts.Where(a => !a.Format.Contains("DSD", StringComparison.OrdinalIgnoreCase)).ToList();
            var dsd = s.Alts.Where(a => a.Format.Contains("DSD", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pcm.Count == 0 && !Directory.Exists(Path.Combine(dir, "pcm0p"))) { skipped.Add($"{shortName} — USB, but has no playback (input only)"); continue; }
            var status = Read(Path.Combine(dir, "pcm0p", "sub0", "status"));
            var owner = Regex.Match(status, @"owner_pid\s*:\s*(\d+)");
            int ownerPid = owner.Success ? int.Parse(owner.Groups[1].Value) : 0;
            var maker = Regex.Replace(longName, @"\s+at usb-.*$", "").Replace(shortName, "").Trim();
            list.Add(new DacDevice
            {
                Key = $"alsa:{cardId}:{usbid}",
                Name = shortName,
                Manufacturer = maker,
                Model = shortName,
                Transport = "USB",
                Usb = usbid == "" ? null : usbid,
                Spec = $"hw:CARD={cardId},DEV=0",
                Rates = pcm.SelectMany(a => a.Rates).Distinct().Order().ToArray(),
                Bits = pcm.Select(BitsOf).Where(b => b > 0).Distinct().Order().ToArray(),
                Channels = Math.Max(2, pcm.Count > 0 ? pcm.Max(a => a.Channels) : 2),
                Formats = s.Alts.Select(a => $"{a.Format}{(a.Bits > 0 ? $" ({a.Bits}-bit)" : "")} · {a.Channels} ch · {string.Join(", ", a.Rates.Select(KHz))}").ToArray(),
                DsdNative = dsd.SelectMany(a => a.Rates).Distinct().Order().ToArray(),
                CurrentRate = s.Running ? s.Freq : 0,
                HolderPid = ownerPid,
                HolderName = ownerPid > 0 ? Read($"/proc/{ownerPid}/comm").Trim() : ""
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ macOS

    public static List<MacRawDevice> ReadMac() => ReadMac([]);

    // Every output device Core Audio has; one that can't be read is noted and the rest still listed.
    public static List<MacRawDevice> ReadMac(List<string> skipped)
    {
        var list = new List<MacRawDevice>();
        foreach (var d in CoreAudio.Devices())
        {
            string name = "";
            try
            {
                name = CoreAudio.GetString(d, CoreAudio.ObjectName);
                int ch = CoreAudio.OutputChannels(d);
                if (ch == 0) continue;   // an input (a microphone)
                var transport = CoreAudio.FourCc(CoreAudio.Get<uint>(d, CoreAudio.DeviceTransportType, CoreAudio.ScopeGlobal) ?? 0);
                double? vol = CoreAudio.Get<float>(d, CoreAudio.DeviceVolumeScalar, CoreAudio.ScopeOutput, 0)
                              ?? CoreAudio.Get<float>(d, CoreAudio.DeviceVolumeScalar, CoreAudio.ScopeOutput, 1);
                var rates = CoreAudio.GetArray<AudioValueRange>(d, CoreAudio.DeviceAvailableNominalSampleRates, CoreAudio.ScopeGlobal)
                    .Select(r => (r.Minimum, r.Maximum)).ToArray();
                var stream = CoreAudio.FirstOutputStream(d);
                var formats = stream == CoreAudio.Unknown ? [] : CoreAudio.GetArray<AudioStreamRangedDescription>(stream, CoreAudio.StreamAvailablePhysicalFormats, CoreAudio.ScopeGlobal)
                    .Select(f => new MacRawFormat(CoreAudio.FourCc(f.Format.FormatId), f.SampleRateRange.Minimum, f.SampleRateRange.Maximum,
                        (int)f.Format.BitsPerChannel, (f.Format.FormatFlags & CoreAudio.FlagIsFloat) != 0, (int)f.Format.ChannelsPerFrame)).ToArray();
                list.Add(new MacRawDevice(CoreAudio.GetString(d, CoreAudio.DeviceUid), name,
                    CoreAudio.GetString(d, CoreAudio.ObjectManufacturer), transport, ch, CoreAudio.NominalRate(d), CoreAudio.HogOwner(d),
                    vol, rates, formats));
            }
            catch (Exception e) { skipped.Add($"{(name == "" ? "device " + d : name)} — Core Audio couldn't read it: {e.Message}"); }
        }
        return list;
    }

    // USB by its transport, or — for a driver that reports none or an odd one — by Apple's USB driver's UID.
    public static bool IsUsb(MacRawDevice d) =>
        d.Transport == "usb" ||
        (d.Transport is not ("bltn" or "hdmi" or "dprt" or "airp" or "blue" or "bltl" or "virt" or "grup" or "aggr" or "cont") &&
         (d.Uid.StartsWith("AppleUSBAudioEngine", StringComparison.OrdinalIgnoreCase) || d.Uid.Contains("USB", StringComparison.OrdinalIgnoreCase)));

    // Core Audio's devices → DACs: USB only unless all is set; never virtual or aggregate devices.
    public static List<DacDevice> FromMac(IEnumerable<MacRawDevice> raw, bool all = false) => FromMac(raw, all, []);

    public static List<DacDevice> FromMac(IEnumerable<MacRawDevice> raw, bool all, List<string> skipped)
    {
        var list = new List<DacDevice>();
        foreach (var d in raw)
        {
            if (d.Uid == "" || d.Channels <= 0) continue;
            bool usb = IsUsb(d);
            if (d.Transport is "virt" or "grup" or "aggr") { skipped.Add($"{d.Name} — {(d.Transport == "virt" ? "virtual" : "aggregate")} device"); continue; }
            if (!usb && !all) { skipped.Add($"{d.Name} — {TransportName(d.Transport)}, not USB"); continue; }
            var ranges = d.Formats.Where(f => f.Id == "lpcm").ToArray();
            var fromFormats = StandardRates.Where(r => ranges.Any(f => r >= f.Min - 0.5 && r <= f.Max + 0.5)).ToArray();
            var fromNominal = StandardRates.Where(r => d.Rates.Any(x => r >= x.Min - 0.5 && r <= x.Max + 0.5)).ToArray();
            var ints = ranges.Where(f => !f.Float).ToArray();
            list.Add(new DacDevice
            {
                Key = "coreaudio:" + d.Uid,
                Name = d.Name == "" ? "USB DAC" : d.Name,
                Manufacturer = d.Manufacturer,
                Model = d.Name,
                Transport = usb ? "USB" : TransportName(d.Transport),
                Spec = d.Uid,
                Rates = fromFormats.Length > 0 ? fromFormats : fromNominal,
                Bits = (ints.Length > 0 ? ints : ranges).Select(f => f.Bits).Distinct().Order().ToArray(),
                Channels = d.Channels,
                Formats = GroupFormats(ranges),
                CurrentRate = (int)Math.Round(d.Rate),
                Volume = d.Volume,
                HolderPid = d.Hog > 0 ? d.Hog : 0
            });
        }
        return list;
    }

    private static string TransportName(string t) => t switch
    {
        "bltn" => "Built-in", "hdmi" => "HDMI", "dprt" => "DisplayPort", "thun" => "Thunderbolt", "1394" => "FireWire",
        "blue" => "Bluetooth", "bltl" => "Bluetooth", "airp" => "AirPlay", "pci" => "PCI", "virt" => "Virtual", "grup" or "aggr" => "Aggregate",
        "cont" => "Continuity", "" => "no transport reported", _ => t
    };

    // "24-bit integer · 2 ch · 44.1 kHz, 48 kHz…", one line per depth and kind.
    public static string[] GroupFormats(IEnumerable<MacRawFormat> ranges)
    {
        var by = new Dictionary<string, SortedSet<int>>();
        foreach (var f in ranges)
        {
            var key = $"{f.Bits}-bit {(f.Float ? "float" : "integer")} · {f.Channels} ch";
            if (!by.TryGetValue(key, out var set)) by[key] = set = [];
            foreach (var r in StandardRates) if (r >= f.Min - 0.5 && r <= f.Max + 0.5) set.Add(r);
        }
        return by.Select(kv => $"{kv.Key} · {string.Join(", ", kv.Value.Select(KHz))}").ToArray();
    }

    public static string FriendlyProcess(string path)
    {
        if (path == "") return "";
        if (path.Contains("audirvana", StringComparison.OrdinalIgnoreCase)) return "Audirvana";
        var app = Regex.Match(path, @"/([^/]+)\.app/");
        return app.Success ? app.Groups[1].Value : Path.GetFileName(path);
    }

    // ------------------------------------------------------------------ both

    public static (List<DacDevice> Devices, string Error) List(Config config)
    {
        var (devices, error, _) = ListWithSkipped(config);
        return (devices, error);
    }

    // The DACs, and every other output seen with why it isn't one.
    public static (List<DacDevice> Devices, string Error, List<string> Skipped) ListWithSkipped(Config config)
    {
        List<DacDevice> devices;
        var skipped = new List<string>();
        string error = "";
        if (config.TestDevices != null) devices = TestDevices(config.TestDevices);
        else if (OperatingSystem.IsLinux()) devices = ListLinux("/proc/asound", skipped);
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                devices = FromMac(ReadMac(skipped), config.AllOutputs, skipped);
                foreach (var d in devices) if (d.HolderPid > 0) d.HolderName = FriendlyProcess(CoreAudio.ProcessPath(d.HolderPid));
            }
            catch (Exception e) { devices = []; error = "Core Audio: " + e.Message; }
        }
        else if (OperatingSystem.IsWindows())
        {
            try { devices = ListWindows(config.AllOutputs, skipped); }
            catch (Exception e) { devices = []; error = "Windows audio: " + e.Message; }
        }
        else { devices = []; error = "only macOS, Windows and Linux are supported"; }
        foreach (var d in devices)
        {
            d.Id = IdOf(d.Key);
            d.DopRates = DopCandidates.Where(r => d.Rates.Contains(r)).ToArray();
        }
        return (devices, error, skipped);
    }

    // Everything the machine says about its sound devices, raw, for a bug report (--diagnose).
    public static string Diagnose()
    {
        var o = new StringBuilder();
        o.AppendLine($"Mandarin DAC Bridge {Config.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        if (OperatingSystem.IsMacOS())
        {
            foreach (var d in CoreAudio.Devices())
            {
                try
                {
                    o.AppendLine($"\n[{d}] {CoreAudio.GetString(d, CoreAudio.ObjectName)}  ·  maker: {CoreAudio.GetString(d, CoreAudio.ObjectManufacturer)}");
                    o.AppendLine($"  uid: {CoreAudio.GetString(d, CoreAudio.DeviceUid)}");
                    o.AppendLine($"  transport: '{CoreAudio.FourCc(CoreAudio.Get<uint>(d, CoreAudio.DeviceTransportType, CoreAudio.ScopeGlobal) ?? 0)}'  output channels: {CoreAudio.OutputChannels(d)}  rate: {CoreAudio.NominalRate(d)}  hog: {CoreAudio.HogOwner(d)}");
                    var stream = CoreAudio.FirstOutputStream(d);
                    if (stream != CoreAudio.Unknown)
                        foreach (var f in CoreAudio.GetArray<AudioStreamRangedDescription>(stream, CoreAudio.StreamAvailablePhysicalFormats, CoreAudio.ScopeGlobal))
                            o.AppendLine($"  format: {CoreAudio.FourCc(f.Format.FormatId)} {f.Format.BitsPerChannel}-bit {((f.Format.FormatFlags & CoreAudio.FlagIsFloat) != 0 ? "float" : "int")} {f.Format.ChannelsPerFrame} ch {f.SampleRateRange.Minimum}-{f.SampleRateRange.Maximum}");
                }
                catch (Exception e) { o.AppendLine($"  ! {e.GetType().Name}: {e.Message}"); }
            }
        }
        else if (OperatingSystem.IsWindows()) o.Append(DiagnoseWindows());
        else if (OperatingSystem.IsLinux())
        {
            o.AppendLine("\n/proc/asound/cards:\n" + Read("/proc/asound/cards"));
            if (!Directory.Exists("/dev/snd")) o.AppendLine("/dev/snd: missing (in Docker: pass it in with -v /dev/snd:/dev/snd)");
            else try { o.AppendLine("/dev/snd: " + string.Join(" ", Directory.GetFiles("/dev/snd").Select(Path.GetFileName))); }
            catch (Exception e) { o.AppendLine("/dev/snd: can't be read (" + e.GetType().Name + ")"); }
            foreach (var dir in Directory.Exists("/proc/asound") ? Directory.GetDirectories("/proc/asound", "card*") : [])
            {
                o.AppendLine($"\n{dir}: usbid={Read(Path.Combine(dir, "usbid")).Trim()} entries={string.Join(" ", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName))}");
                var st = Read(Path.Combine(dir, "stream0"));
                if (st != "") o.AppendLine(st);
            }
        }
        return o.ToString();
    }

    private static List<DacDevice> TestDevices(string json) =>
        System.Text.Json.JsonSerializer.Deserialize(json, BridgeJson.Default.ListDacDevice) ?? [];

    [GeneratedRegex(@"^\s*(\d+)\s+\[(\S+)\s*\]:\s*(\S+)\s+-\s+(.*)$")] private static partial Regex CardLine();
}
