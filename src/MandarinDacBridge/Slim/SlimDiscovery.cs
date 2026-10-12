// SlimDiscovery.cs — finding a Squeezebox server: the one set on the page ("192.168.1.5",
// "lms.local:3483"), or the first to answer a broadcast on UDP 3483.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MandarinDacBridge.Slim;

internal sealed record SlimServer(IPAddress Ip, int Port, string Name, int JsonPort);

internal static partial class SlimDiscovery
{
    public const int Port = 3483;

    // "192.168.1.5", "lms.local:3483" → the server; "" → null.
    public static SlimServer? Parse(string spec)
    {
        spec = spec.Trim();
        if (spec == "") return null;
        var m = HostPort().Match(spec);
        if (!m.Success) return null;
        var host = m.Groups[1].Value.Trim('[', ']');
        int port = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : Port;
        IPAddress? ip;
        if (!IPAddress.TryParse(host, out ip))
        {
            try { ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch (Exception) { ip = null; }
        }
        return ip == null ? null : new SlimServer(ip, port, host, 9000);
    }

    // Asks the network for a server ("e" with the tags wanted) and takes the first to answer ("E" + tags).
    public static SlimServer? Find(TimeSpan wait, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        var ask = Encoding.ASCII.GetBytes("eNAME\0JSON\0VERS\0");
        try { udp.Send(ask, ask.Length, new IPEndPoint(IPAddress.Broadcast, Port)); }
        catch (SocketException) { return null; }
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            var task = udp.ReceiveAsync(ct).AsTask();
            try { if (!task.Wait(until - DateTime.UtcNow, ct)) return null; }
            catch (Exception) { return null; }
            var r = task.Result;
            if (r.Buffer.Length == 0 || r.Buffer[0] != (byte)'E') continue;
            var tags = ParseTags(r.Buffer.AsSpan(1));
            var name = tags.GetValueOrDefault("NAME", r.RemoteEndPoint.Address.ToString());
            int json = int.TryParse(tags.GetValueOrDefault("JSON", ""), out var j) ? j : 9000;
            return new SlimServer(r.RemoteEndPoint.Address, Port, name, json);
        }
        return null;
    }

    // tag (4) · length (1) · value, repeated.
    public static Dictionary<string, string> ParseTags(ReadOnlySpan<byte> b)
    {
        var d = new Dictionary<string, string>();
        int at = 0;
        while (at + 5 <= b.Length)
        {
            var tag = Encoding.ASCII.GetString(b.Slice(at, 4));
            int len = b[at + 4];
            if (at + 5 + len > b.Length) break;
            d[tag] = Encoding.UTF8.GetString(b.Slice(at + 5, len));
            at += 5 + len;
        }
        return d;
    }

    [GeneratedRegex(@"^(\[[^\]]+\]|[^:]+)(?::(\d+))?$")] private static partial Regex HostPort();
}
