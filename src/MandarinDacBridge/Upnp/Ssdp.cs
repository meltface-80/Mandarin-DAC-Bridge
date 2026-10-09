// Ssdp.cs — how Audirvana and Mandarin find the DACs: every DAC is a UPnP
// root device, announced on 239.255.255.250:1900 when it appears (and every
// minute after), withdrawn when it goes, and described to any M-SEARCH that
// asks for it. The address in each answer is the one on the asker's network.
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MandarinDacBridge.Upnp;

internal sealed record Advert(string Udn, string Path);

internal sealed record LocalAddress(string Name, IPAddress Address, IPAddress Mask);

internal sealed partial class Ssdp(int port, string bindIp, string server, Func<IEnumerable<Advert>> devices) : IDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");
    private const int SsdpPort = 1900;
    private const int MaxAge = 1800;
    public const string Renderer = "urn:schemas-upnp-org:device:MediaRenderer:1";
    private static readonly string[] ServiceTypes = [Scpd.AvtType, Scpd.RcsType, Scpd.CmsType];

    private Socket? sock;
    private Timer? timer;
    private readonly HashSet<string> joined = [];
    private List<LocalAddress> ifs = [];
    internal Action<byte[], IPEndPoint>? SendHook;   // tests

    // This machine's IPv4 addresses on real networks.
    public static List<LocalAddress> Interfaces(string pinned)
    {
        var all = new List<LocalAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up && ni.OperationalStatus != OperationalStatus.Unknown) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) continue;
                all.Add(new LocalAddress(ni.Name, u.Address, u.IPv4Mask ?? IPAddress.Parse("255.255.255.0")));
            }
        }
        if (pinned != "") return all.Where(a => a.Address.ToString() == pinned).DefaultIfEmpty(new LocalAddress("pinned", IPAddress.Parse(pinned), IPAddress.Parse("255.255.255.0"))).ToList();
        var real = all.Where(a => !SkipIf().IsMatch(a.Name)).ToList();
        return real.Count > 0 ? real : all;
    }

    private static uint Num(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }

    // Our address on the network `remote` is on.
    public static IPAddress AddressFor(IPAddress remote, IReadOnlyList<LocalAddress> ifs)
    {
        if (ifs.Count == 0) return IPAddress.Loopback;
        foreach (var i in ifs) if (i.Address.Equals(remote)) return i.Address;
        foreach (var i in ifs)
        {
            uint m = Num(i.Mask);
            if ((Num(i.Address) & m) == (Num(remote) & m)) return i.Address;
        }
        return ifs[0].Address;
    }

    public void Start()
    {
        ifs = Interfaces(bindIp);
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsMacOS())
        {
            // Audirvana and others listen on 1900 too: share it (SO_REUSEPORT).
            try { s.SetRawSocketOption(0xffff, 0x0200, BitConverter.GetBytes(1)); } catch (Exception) { /* best effort */ }
        }
        s.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        sock = s;
        Join();
        _ = Task.Run(Receive);
        AnnounceAll();
        // Interfaces come and go (Wi-Fi, DHCP): look again with each round.
        timer = new Timer(_ => { Join(); AnnounceAll(); }, null, 60_000, 60_000);
    }

    private void Join()
    {
        ifs = Interfaces(bindIp);
        foreach (var i in ifs)
        {
            if (!joined.Add(i.Address.ToString())) continue;
            try { sock?.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, i.Address)); }
            catch (Exception) { /* not multicast-capable */ }
        }
    }

    private async Task Receive()
    {
        var buf = new byte[4096];
        while (sock is { } s)
        {
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var r = await s.ReceiveFromAsync(buf, SocketFlags.None, from);
                OnMessage(Encoding.UTF8.GetString(buf, 0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint);
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { await Task.Delay(100); }
        }
    }

    private string Location(Advert d, IPAddress a) => $"http://{a}:{port}{d.Path}";

    private static IEnumerable<(string Nt, string Usn)> Targets(Advert d) =>
        new[] { ("upnp:rootdevice", $"{d.Udn}::upnp:rootdevice"), (d.Udn, d.Udn), (Renderer, $"{d.Udn}::{Renderer}") }
            .Concat(ServiceTypes.Select(t => (t, $"{d.Udn}::{t}")));

    private void Notify(Advert d, string nts)
    {
        var s = sock;
        if (s == null) return;
        foreach (var i in ifs)
        {
            foreach (var (nt, usn) in Targets(d))
            {
                var lines = new List<string> { "NOTIFY * HTTP/1.1", $"HOST: {Group}:{SsdpPort}", $"NT: {nt}", $"NTS: {nts}", $"USN: {usn}" };
                if (nts == "ssdp:alive") lines.AddRange([$"CACHE-CONTROL: max-age={MaxAge}", $"LOCATION: {Location(d, i.Address)}", $"SERVER: {server}"]);
                var msg = Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n");
                try
                {
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, i.Address.GetAddressBytes());
                    s.SendTo(msg, new IPEndPoint(Group, SsdpPort));
                }
                catch (Exception) { /* the interface went */ }
            }
        }
    }

    public void Announce(Advert d)
    {
        Notify(d, "ssdp:alive");
        _ = Task.Delay(1000).ContinueWith(_ => Notify(d, "ssdp:alive"));
    }

    public void AnnounceAll() { foreach (var d in devices()) Notify(d, "ssdp:alive"); }

    public void ByeBye(Advert d) => Notify(d, "ssdp:byebye");

    internal void UseInterfaces(List<LocalAddress> list) => ifs = list;   // tests

    internal void OnMessage(string text, IPEndPoint from)
    {
        if (!text.StartsWith("M-SEARCH * HTTP/1.1", StringComparison.OrdinalIgnoreCase)) return;
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n').Skip(1))
        {
            int i = line.IndexOf(':');
            if (i > 0) h[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        if (!h.TryGetValue("MAN", out var man) || !man.Contains("ssdp:discover", StringComparison.OrdinalIgnoreCase)) return;
        var st = h.GetValueOrDefault("ST", "");
        int mx = Math.Clamp(int.TryParse(h.GetValueOrDefault("MX", "0"), out var x) ? x : 0, 0, 3);
        var address = AddressFor(from.Address, ifs);
        foreach (var d in devices())
        {
            foreach (var (nt, usn) in Targets(d))
            {
                bool match = st == "ssdp:all" || st == nt || (st.StartsWith("urn:") && SameTypeOlder(st, nt));
                if (!match) continue;
                var reply = string.Join("\r\n",
                    "HTTP/1.1 200 OK", $"CACHE-CONTROL: max-age={MaxAge}", $"DATE: {DateTime.UtcNow:R}", "EXT:",
                    $"LOCATION: {Location(d, address)}", $"SERVER: {server}", $"ST: {(st == "ssdp:all" ? nt : st)}",
                    $"USN: {(st == "ssdp:all" || st == nt ? usn : $"{d.Udn}::{st}")}", "Content-Length: 0") + "\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(reply);
                var delay = mx > 0 ? Random.Shared.Next(mx * 300) : 0;
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    if (SendHook != null) { SendHook(bytes, from); return; }
                    try { sock?.SendTo(bytes, from); } catch (Exception) { /* closed */ }
                });
            }
        }
    }

    // "…:MediaRenderer:1" asked, "…:MediaRenderer:1" or a later version offered.
    private static bool SameTypeOlder(string asked, string offered)
    {
        var a = Regex.Match(asked, @"^(.*):(\d+)$");
        var o = Regex.Match(offered, @"^(.*):(\d+)$");
        return a.Success && o.Success && a.Groups[1].Value == o.Groups[1].Value && int.Parse(a.Groups[2].Value) <= int.Parse(o.Groups[2].Value);
    }

    public void Dispose()
    {
        foreach (var d in devices()) ByeBye(d);
        timer?.Dispose();
        var s = sock;
        sock = null;
        Thread.Sleep(100);
        s?.Dispose();
    }

    [GeneratedRegex("^(docker|veth|br-|virbr|lo|utun|awdl|llw|bridge|tailscale|zt)", RegexOptions.IgnoreCase)]
    private static partial Regex SkipIf();
}
