// DevicesWindows.cs — the USB DACs on Windows, from the MMDevice API: each
// active output endpoint whose device sits on USB. What a DAC takes is asked
// of the driver in exclusive mode (IsFormatSupported, at every standard rate,
// 32, 24-in-32, packed 24 and 16 bits), once per DAC: afterwards the bridge
// holds it, and the answer doesn't change.
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.RegularExpressions;
using MandarinDacBridge.Native;

namespace MandarinDacBridge;

internal static partial class Devices
{
    private static readonly Dictionary<string, (int[] Rates, int[] Bits, int Channels)> windowsCaps = new();

    private sealed record WinEndpoint(string Id, string Name, string Endpoint, string Enumerator, string Instance);

    private static List<WinEndpoint> WindowsEndpoints(IMMDeviceEnumerator e)
    {
        var list = new List<WinEndpoint>();
        Wasapi.Check(e.EnumAudioEndpoints(Wasapi.Render, Wasapi.StateActive, out var coll), "the outputs");
        coll.GetCount(out var n);
        for (uint i = 0; i < n; i++)
        {
            if (coll.Item(i, out var d) != 0 || d == null) continue;
            var id = Wasapi.Id(d);
            string name = "", endpoint = "", enumerator = "", instance = "";
            if (d.OpenPropertyStore(0 /* STGM_READ */, out var store) == 0 && store != null)
            {
                name = Wasapi.Text(store, Wasapi.InterfaceName);
                endpoint = Wasapi.Text(store, Wasapi.FriendlyName);
                enumerator = Wasapi.Text(store, Wasapi.EnumeratorName);
                instance = Wasapi.Text(store, Wasapi.InstanceId);
            }
            list.Add(new WinEndpoint(id, name != "" ? name : endpoint, endpoint, enumerator, instance));
        }
        return list;
    }

    public static List<DacDevice> ListWindows(bool allOutputs, List<string> skipped)
    {
        Wasapi.Init();
        var e = Wasapi.Enumerator();
        var list = new List<DacDevice>();
        foreach (var ep in WindowsEndpoints(e))
        {
            bool usb = ep.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) || ep.Instance.StartsWith("USB", StringComparison.OrdinalIgnoreCase);
            if (!usb && !allOutputs)
            {
                skipped.Add($"{ep.Endpoint}: not a USB device ({(ep.Enumerator != "" ? ep.Enumerator : "unknown bus")})");
                continue;
            }
            var caps = WindowsCaps(e, ep.Id);
            if (caps.Rates.Length == 0)
            {
                skipped.Add($"{ep.Endpoint}: takes no integer PCM in exclusive mode (or Windows doesn't allow exclusive mode for it)");
                continue;
            }
            var vidPid = VidPid().Match(ep.Instance);
            list.Add(new DacDevice
            {
                Key = "win:" + ep.Id, Name = ep.Name, Manufacturer = "", Model = ep.Endpoint, Transport = usb ? "USB" : "Other",
                Usb = vidPid.Success ? $"{vidPid.Groups[1].Value.ToLowerInvariant()}:{vidPid.Groups[2].Value.ToLowerInvariant()}" : null,
                Spec = ep.Id, Rates = caps.Rates, Bits = caps.Bits, Channels = caps.Channels,
                Formats = caps.Bits.Select(b => $"{b}-bit integer · {caps.Channels} ch").ToArray()
            });
        }
        return list;
    }

    // Rates and depths the DAC takes in exclusive mode (asked once, then remembered).
    private static unsafe (int[] Rates, int[] Bits, int Channels) WindowsCaps(IMMDeviceEnumerator e, string id)
    {
        lock (windowsCaps) if (windowsCaps.TryGetValue(id, out var known)) return known;
        var rates = new SortedSet<int>();
        var bits = new SortedSet<int>();
        int channels = 2;
        bool inUse = false;
        if (e.GetDevice(id, out var d) != 0 || d == null) return ([], [], 2);
        IAudioClient? c = null;
        try
        {
            c = Wasapi.Activate(d);
            if (c.GetMixFormat(out var mix) == 0 && mix != 0)
            {
                channels = Math.Max(1, (int)*(ushort*)(mix + 2));
                Wasapi.CoTaskMemFree(mix);
            }
            foreach (var r in StandardRates.Where(r => r <= 768000))
                foreach (var (cont, val) in new[] { (32, 32), (32, 24), (24, 24), (16, 16) })
                {
                    var f = WaveFormatExtensible.Of(r, Math.Min(channels, 2), cont, val);
                    int hr = c.IsFormatSupported(Wasapi.ShareExclusive, (nint)(&f), 0);
                    if (hr == Wasapi.DeviceInUse) inUse = true;
                    if (hr != 0) continue;
                    rates.Add(r);
                    bits.Add(val);
                }
        }
        catch (Exception) { /* answered nothing */ }
        finally { if ((object?)c is ComObject o) o.FinalRelease(); }
        var result = (rates.ToArray(), bits.Reverse().ToArray(), Math.Min(channels, 8));
        // Held by another program: asked again next time rather than remembered as nothing.
        if (rates.Count > 0 || !inUse) lock (windowsCaps) windowsCaps[id] = result;
        return result;
    }

    private static string DiagnoseWindows()
    {
        var o = new StringBuilder();
        try
        {
            Wasapi.Init();
            var e = Wasapi.Enumerator();
            foreach (var ep in WindowsEndpoints(e))
            {
                var caps = WindowsCaps(e, ep.Id);
                o.AppendLine($"\n{ep.Endpoint}  ·  {ep.Name}");
                o.AppendLine($"  id: {ep.Id}");
                o.AppendLine($"  bus: {ep.Enumerator}  instance: {ep.Instance}");
                o.AppendLine($"  exclusive: {(caps.Rates.Length > 0 ? string.Join(", ", caps.Rates.Select(KHz)) + " · " + string.Join("/", caps.Bits) + "-bit" : "nothing")} · {caps.Channels} ch");
            }
        }
        catch (Exception x) { o.AppendLine("! " + x.Message); }
        return o.ToString();
    }

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")] private static partial Regex VidPid();
}
