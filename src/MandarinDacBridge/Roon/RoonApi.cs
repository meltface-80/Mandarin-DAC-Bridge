// RoonApi.cs — what Roon is playing, from Roon's own extension API (the way
// RoPieee's display gets it), for the page and the now-playing screen. Off
// unless switched on; once on, enable "Mandarin DAC Bridge" in Roon, under
// Settings → Extensions.
//
// Roon's Squeezebox support sends no track details, and Roon Bridge plays to a
// DAC without the bridge in between; the Roon Core knows both. The protocol is
// Roon's published one (github.com/RoonLabs/node-roon-api):
//
//   SOOD   a UDP query to 239.255.90.90:9003 (and the broadcast address) for
//          Roon's API service; Cores answer with their address and http_port.
//   MOO    requests and replies over a WebSocket (ws://core:http_port/api),
//          framed like HTTP: "MOO/1 REQUEST <service>/<method>", Request-Id,
//          Content-Length, a blank line, a JSON body. The Core pings; we answer.
//
// registry:1/info, then registry:1/register (pending until the extension is
// enabled in Roon; the token it returns is kept so it stays enabled), then
// transport:2/subscribe_zones: every zone's state, title, artist, album, cover
// key and position, and each change after. Covers come over HTTP from the
// Core: /api/image/<key>.
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MandarinDacBridge.Roon;

internal sealed record RoonZone(string Id, string Name, string[] Outputs, string State, string Title, string Artist, string Album,
    string ImageKey, double Length, double? Seek, long At)
{
    public bool InUse => State is "playing" or "paused" or "loading";
}

internal static class Sood
{
    public const int Port = 9003;
    public static readonly IPAddress Multicast = IPAddress.Parse("239.255.90.90");
    public const string RoonApiService = "00720724-5143-4a9b-abac-0e50cba674bb";

    // "SOOD", version 2, the type ('Q' query, 'R' reply), then each property: key length (1 byte), key, value
    // length (2 bytes, big-endian; 0xFFFF: none), value.
    public static byte[] Message(char type, IEnumerable<KeyValuePair<string, string?>> props)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes("SOOD")) { 2, (byte)type };
        foreach (var (k, v) in props)
        {
            var key = Encoding.UTF8.GetBytes(k);
            b.Add((byte)key.Length);
            b.AddRange(key);
            if (v == null) { b.Add(0xFF); b.Add(0xFF); continue; }
            var val = Encoding.UTF8.GetBytes(v);
            b.Add((byte)(val.Length >> 8));
            b.Add((byte)val.Length);
            b.AddRange(val);
        }
        return b.ToArray();
    }

    public static (char Type, Dictionary<string, string?> Props)? Parse(ReadOnlySpan<byte> m)
    {
        if (m.Length < 6 || !m[..4].SequenceEqual("SOOD"u8) || m[4] != 2) return null;
        var props = new Dictionary<string, string?>();
        int i = 6;
        while (i < m.Length)
        {
            int kl = m[i++];
            if (kl == 0 || i + kl + 2 > m.Length) return null;
            var k = Encoding.UTF8.GetString(m.Slice(i, kl));
            i += kl;
            int vl = BinaryPrimitives.ReadUInt16BigEndian(m[i..]);
            i += 2;
            if (vl == 0xFFFF) { props[k] = null; continue; }
            if (i + vl > m.Length) return null;
            props[k] = Encoding.UTF8.GetString(m.Slice(i, vl));
            i += vl;
        }
        return ((char)m[5], props);
    }

    // The Roon Cores that answer within the time given: (address, http_port, name).
    public static async Task<List<(string Host, int Port, string Name)>> Find(TimeSpan wait, CancellationToken ct)
    {
        var found = new List<(string, int, string)>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var q = Message('Q', [new("query_service_id", RoonApiService), new("_tid", Guid.NewGuid().ToString())]);
        var targets = new List<IPEndPoint> { new(Multicast, Port), new(IPAddress.Broadcast, Port) };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var ua in ni.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
            {
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                targets.Add(new IPEndPoint(new IPAddress(ip.Select((x, i) => (byte)(x | ~mask[i])).ToArray()), Port));
            }
        foreach (var t in targets.Distinct()) { try { await udp.SendAsync(q, t, ct); } catch (Exception) { /* that network can't */ } }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            for (;;)
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                if (Parse(r.Buffer) is not ('R', var p) || p.GetValueOrDefault("service_id") != RoonApiService) continue;
                if (!int.TryParse(p.GetValueOrDefault("http_port"), out var port)) continue;
                var host = r.RemoteEndPoint.Address.ToString();
                if (!found.Any(f => f.Item1 == host && f.Item2 == port)) found.Add((host, port, p.GetValueOrDefault("display_name") ?? host));
            }
        }
        catch (OperationCanceledException) { /* time's up */ }
        return found;
    }
}

internal sealed class RoonApi : IDisposable
{
    public const string ExtensionId = "com.musicd.mandarin-dac-bridge";

    private readonly string tokenFile;
    private readonly string fixedCore;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly Dictionary<string, RoonZone> zones = new();
    private readonly Dictionary<int, Action<string, string, byte[]>> pending = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private ClientWebSocket? ws;
    private int nextId;
    private string lastError = "";

    public string Status { get; private set; } = "looking for a Roon Core";
    public string CoreName { get; private set; } = "";
    public string Http { get; private set; } = "";     // http://core:port, for covers
    public event Action? Changed;

    // fixedCore: "host:port" (ROON_CORE), for a network where the Core can't be found by itself.
    public RoonApi(string dataDir, string fixedCore = "")
    {
        tokenFile = Path.Combine(dataDir, "roon-api.json");
        this.fixedCore = fixedCore;
        thread = new Thread(() => Run().GetAwaiter().GetResult()) { IsBackground = true, Name = "roon-api" };
        thread.Start();
    }

    private static void Log(string m) => MandarinDacBridge.Log.Write("Roon: " + m);

    public List<RoonZone> Zones() { lock (gate) return zones.Values.OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList(); }

    public string ImageUrl(string key, int size = 800) =>
        Http == "" || key == "" ? "" : $"{Http}/api/image/{Uri.EscapeDataString(key)}?scale=fit&width={size}&height={size}&format=image/jpeg";

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        Changed?.Invoke();
    }

    private async Task Run()
    {
        int quick = 0;
        while (!stop.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            try
            {
                var (host, port, name) = await Core();
                await Session(host, port, name);
            }
            catch (Exception e)
            {
                if (stop.IsCancellationRequested) return;
                var m = e is NotFound ? e.Message : "connection: " + e.Message;
                if (m != lastError) Log(m);
                lastError = m;
                SetStatus(m);
            }
            lock (gate) zones.Clear();
            Changed?.Invoke();
            quick = Environment.TickCount64 - started < 30_000 ? quick + 1 : 0;
            stop.Token.WaitHandle.WaitOne(Math.Min(60_000, 3000 * (1 << Math.Min(quick, 4))));
        }
    }

    private sealed class NotFound(string m) : Exception(m);

    private async Task<(string, int, string)> Core()
    {
        if (fixedCore != "")
        {
            var i = fixedCore.LastIndexOf(':');
            return i > 0 && int.TryParse(fixedCore[(i + 1)..], out var p) ? (fixedCore[..i], p, fixedCore) : (fixedCore, 9330, fixedCore);
        }
        SetStatus("looking for a Roon Core on the network");
        var cores = await Sood.Find(TimeSpan.FromSeconds(3), stop.Token);
        if (cores.Count == 0) throw new NotFound("no Roon Core found on the network (set ROON_CORE=host:port if it can't be found)");
        return cores[0];
    }

    private async Task Session(string host, int port, string name)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(new Uri($"ws://{host}:{port}/api"), stop.Token);
        lock (gate) { ws = socket; pending.Clear(); }
        Http = $"http://{host}:{port}";
        CoreName = name;
        lastError = "";
        var reader = Read(socket);

        // Who the Core is, then register (with the token from last time, if any).
        var info = await Ask("com.roonlabs.registry:1/info", null);
        var coreId = Str(info, "core_id");
        CoreName = Str(info, "display_name") is { Length: > 0 } n ? n : name;
        SetStatus($"found {CoreName}: in Roon, go to Settings → Extensions and enable Mandarin DAC Bridge");
        var reg = new JsonObject
        {
            ["extension_id"] = ExtensionId, ["display_name"] = "Mandarin DAC Bridge", ["display_version"] = Config.Version,
            ["publisher"] = "MusicD", ["email"] = "dacbridge@musicd.invalid", ["website"] = "https://github.com/meltface-80/Mandarin-DAC-Bridge",
            ["required_services"] = new JsonArray("com.roonlabs.transport:2"), ["optional_services"] = new JsonArray(),
            ["provided_services"] = new JsonArray("com.roonlabs.ping:1")
        };
        if (Token(coreId) is { Length: > 0 } t) reg["token"] = t;
        Send("com.roonlabs.registry:1/register", reg, (verb, nm, body) =>
        {
            if (nm != "Registered") return;
            var j = Json(body);
            if (Str(j, "token") is { Length: > 0 } tok) SaveToken(Str(j, "core_id") is { Length: > 0 } c ? c : coreId, tok);
            Log($"enabled in {CoreName}");
            SetStatus($"connected to {CoreName}");
            Send("com.roonlabs.transport:2/subscribe_zones", new JsonObject { ["subscription_key"] = 0 }, (_, zn, zb) => Zoned(zn, zb));
        });
        await reader;
    }

    // ------------------------------------------------------------ zones

    private void Zoned(string name, byte[] body)
    {
        var j = Json(body);
        if (j.ValueKind != JsonValueKind.Object) return;
        lock (gate)
        {
            if (name == "Subscribed") zones.Clear();
            foreach (var key in new[] { "zones", "zones_added", "zones_changed" })
                if (j.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var z in list.EnumerateArray()) if (Zone(z) is { } zone) zones[zone.Id] = zone;
            if (j.TryGetProperty("zones_removed", out var gone) && gone.ValueKind == JsonValueKind.Array)
                foreach (var g in gone.EnumerateArray()) zones.Remove(g.ValueKind == JsonValueKind.String ? g.GetString()! : Str(g, "zone_id"));
            if (j.TryGetProperty("zones_seek_changed", out var seeks) && seeks.ValueKind == JsonValueKind.Array)
                foreach (var s in seeks.EnumerateArray())
                    if (zones.TryGetValue(Str(s, "zone_id"), out var z) && s.TryGetProperty("seek_position", out var sp) && sp.ValueKind == JsonValueKind.Number)
                        zones[z.Id] = z with { Seek = sp.GetDouble(), At = Arbiter.Now };
        }
        Changed?.Invoke();
    }

    public static RoonZone? Zone(JsonElement z)
    {
        var id = Str(z, "zone_id");
        if (id == "") return null;
        var outputs = z.TryGetProperty("outputs", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => Str(x, "display_name")).Where(x => x != "").ToArray() : [];
        string title = "", artist = "", album = "", image = "";
        double length = 0;
        double? seek = null;
        if (z.TryGetProperty("now_playing", out var np) && np.ValueKind == JsonValueKind.Object)
        {
            if (np.TryGetProperty("three_line", out var three)) { title = Str(three, "line1"); artist = Str(three, "line2"); album = Str(three, "line3"); }
            else if (np.TryGetProperty("two_line", out var two)) { title = Str(two, "line1"); artist = Str(two, "line2"); }
            else if (np.TryGetProperty("one_line", out var one)) title = Str(one, "line1");
            image = Str(np, "image_key");
            if (np.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number) length = l.GetDouble();
            if (np.TryGetProperty("seek_position", out var s) && s.ValueKind == JsonValueKind.Number) seek = s.GetDouble();
        }
        return new RoonZone(id, Str(z, "display_name"), outputs, Str(z, "state"), title, artist, album, image, length, seek, Arbiter.Now);
    }

    // ------------------------------------------------------------ tokens (owner-only: they keep the extension enabled)

    private string Token(string coreId)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(tokenFile));
            return Str(doc.RootElement, coreId);
        }
        catch (Exception) { return ""; }
    }

    private void SaveToken(string coreId, string token)
    {
        try
        {
            var all = new JsonObject();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(tokenFile));
                foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) all[p.Name] = p.Value.GetString();
            }
            catch (Exception) { /* first one */ }
            all[coreId] = token;
            File.WriteAllText(tokenFile, all.ToJsonString());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) { Log("couldn't keep the token: " + e.Message); }
    }

    // ------------------------------------------------------------ MOO

    // "MOO/1 <verb> <name>" · headers · blank line · body.
    internal static (string Verb, string Name, Dictionary<string, string> Headers, byte[] Body)? ParseMoo(byte[] m)
    {
        int end = -1;
        for (int i = 0; i + 1 < m.Length; i++) if (m[i] == '\n' && m[i + 1] == '\n') { end = i; break; }
        var head = Encoding.UTF8.GetString(m, 0, end < 0 ? m.Length : end).Split('\n');
        var first = head[0].Split(' ', 3);
        if (first.Length < 2 || first[0] != "MOO/1") return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in head.Skip(1))
        {
            var c = l.IndexOf(':');
            if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
        }
        var body = end < 0 ? [] : m[(end + 2)..];
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n) && n < body.Length) body = body[..n];
        return (first[1], first.Length > 2 ? first[2] : "", headers, body);
    }

    internal static byte[] Moo(string verb, string name, int requestId, byte[]? body)
    {
        var sb = new StringBuilder($"MOO/1 {verb} {name}\nRequest-Id: {requestId}\n");
        if (body != null) sb.Append($"Content-Length: {body.Length}\nContent-Type: application/json\n");
        sb.Append('\n');
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        return body == null ? head : [.. head, .. body];
    }

    private void Send(string name, JsonObject? body, Action<string, string, byte[]> reply)
    {
        int id;
        ClientWebSocket? s;
        lock (gate) { id = nextId++; pending[id] = reply; s = ws; }
        var bytes = body == null ? null : Encoding.UTF8.GetBytes(body.ToJsonString());
        _ = Write(s, Moo("REQUEST", name, id, bytes));
    }

    private Task<JsonElement> Ask(string name, JsonObject? body)
    {
        var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(name, body, (verb, nm, b) =>
        {
            if (verb == "COMPLETE" && nm == "Success") done.TrySetResult(Json(b));
            else done.TrySetException(new InvalidOperationException($"{name}: {nm}"));
        });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
    }

    private async Task Write(ClientWebSocket? s, byte[] m)
    {
        if (s == null) return;
        await sendGate.WaitAsync();
        try { await s.SendAsync(m, WebSocketMessageType.Binary, true, stop.Token); }
        catch (Exception) { /* the reader sees it go */ }
        finally { sendGate.Release(); }
    }

    private async Task Read(ClientWebSocket s)
    {
        var buf = new byte[64 * 1024];
        var msg = new MemoryStream();
        while (!stop.IsCancellationRequested && s.State == WebSocketState.Open)
        {
            var r = await s.ReceiveAsync(buf, stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            msg.Write(buf, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var m = msg.ToArray();
            msg.SetLength(0);
            if (ParseMoo(m) is not { } moo) continue;
            if (!int.TryParse(moo.Headers.GetValueOrDefault("Request-Id"), out var id)) continue;   // not a request or reply of ours
            if (moo.Verb == "REQUEST")
            {
                // The Core's pings (and anything else it asks of a service we don't have) are answered.
                _ = Write(s, Moo("COMPLETE", moo.Name.StartsWith("com.roonlabs.ping:1/") ? "Success" : "InvalidRequest", id, null));
                continue;
            }
            Action<string, string, byte[]>? h;
            lock (gate)
            {
                pending.TryGetValue(id, out h);
                if (moo.Verb == "COMPLETE") pending.Remove(id);
            }
            try { h?.Invoke(moo.Verb, moo.Name, moo.Body); }
            catch (Exception e) { Log(e.Message); }
        }
        lock (gate) ws = null;
        throw new IOException("the Roon Core closed the connection");
    }

    private static JsonElement Json(byte[] b)
    {
        try { return b.Length == 0 ? default : JsonDocument.Parse(b).RootElement.Clone(); }
        catch (Exception) { return default; }
    }

    private static string Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose()
    {
        stop.Cancel();
        ClientWebSocket? s;
        lock (gate) s = ws;
        try { s?.Abort(); } catch (Exception) { /* closing */ }
        thread.Join(TimeSpan.FromSeconds(5));
    }
}
