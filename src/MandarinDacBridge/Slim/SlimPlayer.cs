// SlimPlayer.cs — a DAC as a Squeezebox player (what squeezelite is), for
// Lyrion Music Server (LMS) and anything else that speaks SlimProto, Roon's
// Squeezebox support included.
//
// The bridge doesn't run squeezelite: squeezelite would open the DAC itself,
// and the bridge holds it. Instead each DAC talks SlimProto on its own (TCP
// 3483) and plays what the server streams through the same renderer as
// UPnP: same exclusive hold, same rate switching, same arbiter. So a server
// playing to "<DAC> (Bridge)" keeps Audirvana and Mandarin out while it
// plays, and is kept out while they do.
//
//   server ── strm s (an HTTP request) ──▶ the renderer fetches it (ffmpeg) ─▶ DAC
//   server ◀── STAT: STMc connected, STMs started, STMd read to the end
//              (send the next, for gapless), STMu played out, STMt time
//
// The server is found by broadcast (UDP 3483) unless one is set. What's
// playing (title, artist, cover) comes from LMS's JSON-RPC, when it has one.
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

// One strm packet: what to play, from where, in what form.
internal sealed record Strm(char Command, char Autostart, char Format, char SampleSize, char SampleRate, char Channels, char Endian,
    uint ReplayGain, int ServerPort, uint ServerIp, string Header)
{
    public static Strm? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24) return null;
        return new Strm((char)b[0], (char)b[1], (char)b[2], (char)b[3], (char)b[4], (char)b[5], (char)b[6],
            BinaryPrimitives.ReadUInt32BigEndian(b[14..]), BinaryPrimitives.ReadUInt16BigEndian(b[18..]),
            BinaryPrimitives.ReadUInt32BigEndian(b[20..]), Encoding.Latin1.GetString(b[24..]));
    }

    // "GET /stream.mp3?player=… HTTP/1.0" → "/stream.mp3?player=…"
    public string Path()
    {
        var m = Regex.Match(Header, @"^GET\s+(\S+)\s+HTTP", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "/stream.mp3";
    }

    // The MIME type the renderer is given, so it needn't ask the server (a stream is read once).
    public string Mime()
    {
        switch (Format)
        {
            case 'f': return "audio/flac";
            case 'm': return "audio/mpeg";
            case 'o': return "audio/ogg";
            case 'a': return "audio/aac";
            case 'l': return "audio/mp4";
            case 'p':
                int bits = SampleSize switch { '1' => 16, '2' => 24, '3' => 32, _ => 0 };
                int rate = SampleRate switch
                {
                    '0' => 11025, '1' => 22050, '2' => 32000, '3' => 44100, '4' => 48000, '5' => 8000, '6' => 12000,
                    '7' => 16000, '8' => 24000, '9' => 96000, _ => 0
                };
                int ch = Channels switch { '1' => 1, '2' => 2, _ => 0 };
                // Self-describing (a WAV or AIFF header follows): ffmpeg reads the header.
                if (bits == 0 || rate == 0 || ch == 0) return "audio/wav";
                return $"audio/L{bits};rate={rate};channels={ch}{(Endian == '1' ? ";endian=little" : "")}";
            default: return "";
        }
    }
}

internal sealed class SlimPlayer : IDisposable
{
    private static readonly HttpClient Json = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly Bridge bridge;
    private readonly Func<string> serverSetting;
    private readonly byte[] mac;
    private readonly CancellationTokenSource stop = new();
    private readonly object sendGate = new();
    private readonly object gate = new();
    private readonly HashSet<Track> mine = [];
    private readonly Thread thread;
    private readonly Timer heartbeat;
    private readonly long born = Environment.TickCount64;
    private NetworkStream? stream;
    private TcpClient? tcp;
    private SlimServer? server;
    private IPAddress? switchTo;
    private Caller? caller;
    private bool decodedSent;
    private long bytesReceived;

    public string Status { get; private set; } = "looking for a Squeezebox server";
    public string ServerName => server?.Name ?? "";
    public string Mac { get; }

    public SlimPlayer(Bridge bridge, string hostname, Func<string> serverSetting)
    {
        this.bridge = bridge;
        this.serverSetting = serverSetting;
        mac = MacFor(hostname, bridge.Dev.Key);
        Mac = string.Join(":", mac.Select(x => x.ToString("x2")));
        var r = bridge.Renderer;
        r.Reached += OnReached;
        r.Decoded += OnDecoded;
        r.Finished += OnFinished;
        thread = new Thread(Run) { IsBackground = true, Name = "slimproto " + bridge.Id };
        thread.Start();
        heartbeat = new Timer(_ =>
        {
            if (!Ours() || !bridge.Renderer.IsActive) return;
            // Roughly what a player would have fetched by now; the server only shows it.
            Interlocked.Add(ref bytesReceived, 1 << 16);
            Stat("STMt");
        }, null, 1000, 1000);
    }

    // A stable, locally administered MAC for this DAC on this machine: the server knows the player by it.
    public static byte[] MacFor(string hostname, string key)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("slim|" + hostname + "|" + key));
        var m = h[..6];
        m[0] = (byte)((m[0] & 0xFE) | 0x02);
        return m;
    }

    private void Log(string m) => bridge.Log("squeezebox: " + m);

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        bridge.Notify();
    }

    // ------------------------------------------------------------ the connection

    private void Run()
    {
        bool reconnect = false;
        string lastError = "";
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var s = switchTo != null ? new SlimServer(switchTo, SlimDiscovery.Port, switchTo.ToString(), server?.JsonPort ?? 9000) : null;
                switchTo = null;
                s ??= SlimDiscovery.Parse(serverSetting()) ?? SlimDiscovery.Find(TimeSpan.FromSeconds(3), stop.Token);
                if (s == null) { SetStatus("looking for a Squeezebox server"); Wait(5000); continue; }
                server = s;
                using var client = new TcpClient { NoDelay = true };
                client.ConnectAsync(s.Ip, s.Port, stop.Token).AsTask().Wait(TimeSpan.FromSeconds(8));
                if (!client.Connected) throw new IOException($"{s.Ip} didn't answer");
                tcp = client;
                stream = client.GetStream();
                caller = new Caller("slim|" + s.Ip, ServerLabel(s), s.Ip.ToString());
                Helo(reconnect);
                Log($"connected to {ServerLabel(s)} at {s.Ip} as {Mac}");
                SetStatus($"connected to {ServerLabel(s)}");
                lastError = "";
                reconnect = true;
                Read(stream);
            }
            catch (Exception e)
            {
                // Switched off: the connection was closed on purpose (and nothing may escape this thread).
                if (stop.IsCancellationRequested) return;
                var why = e is AggregateException a ? a.InnerException?.Message ?? a.Message : e.Message;
                if (why != lastError) Log("connection: " + why);
                lastError = why;
            }
            finally
            {
                lock (sendGate) { stream = null; tcp = null; }
            }
            if (stop.IsCancellationRequested) return;
            SetStatus(server != null ? $"lost {ServerLabel(server)}; reconnecting" : "looking for a Squeezebox server");
            Wait(switchTo != null ? 100 : 3000);
        }
    }

    private static string ServerLabel(SlimServer s) =>
        s.Name.Contains("roon", StringComparison.OrdinalIgnoreCase) ? "Roon" : s.Name == s.Ip.ToString() ? "Squeezebox server" : s.Name;

    private void Wait(int ms) => stop.Token.WaitHandle.WaitOne(ms);

    private void Read(NetworkStream s)
    {
        var len = new byte[2];
        while (!stop.IsCancellationRequested)
        {
            s.ReadExactly(len);
            int n = BinaryPrimitives.ReadUInt16BigEndian(len);
            if (n < 4) { s.ReadExactly(new byte[n]); continue; }
            var body = new byte[n];
            s.ReadExactly(body);
            Handle(Encoding.ASCII.GetString(body, 0, 4), body.AsSpan(4).ToArray());
        }
    }

    private void Send(string op, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes(op, pkt);
        BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(pkt.AsSpan(8));
        lock (sendGate)
        {
            try { stream?.Write(pkt); }
            catch (Exception) { try { tcp?.Close(); } catch (Exception) { /* closing */ } }
        }
    }

    // HELO: who this player is and what it plays.
    private void Helo(bool reconnect)
    {
        var dac = bridge.Dev;
        int max = dac.Rates.Length > 0 ? dac.Rates.Max() : 384000;
        // What ffmpeg reads from a stream it can't seek in. DSD isn't offered: the server converts it.
        var caps = $"Model=squeezelite,ModelName=Mandarin DAC Bridge,AccuratePlayPoints=1,HasDigitalOut=1,Firmware={Config.Version},"
                   + $"MaxSampleRate={max},flc,pcm,aif,mp3,ogg";
        var b = new byte[36 + Encoding.ASCII.GetByteCount(caps)];
        b[0] = 12;                     // device: squeezeplay (what squeezelite says)
        b[1] = 0;
        mac.CopyTo(b, 2);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(24), (ushort)(reconnect ? 0x4000 : 0));
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(26), (ulong)Interlocked.Read(ref bytesReceived));
        Encoding.ASCII.GetBytes(caps, b.AsSpan(36));
        Send("HELO", b);
    }

    // STAT: an event, with where playback is.
    private void Stat(string ev, uint serverTimestamp = 0)
    {
        var b = new byte[53];
        Encoding.ASCII.GetBytes(ev, b);
        var r = bridge.Renderer;
        bool ours = Ours();
        double pos = ours ? r.Position() : 0;
        bool active = ours && r.IsActive;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(7), 2 << 20);                          // stream buffer size
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(11), active ? 1 << 20 : 0u);          // … fullness
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(15), (ulong)Interlocked.Read(ref bytesReceived));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(23), 0xFFFF);                          // signal: wired
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(25), Jiffies());
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(29), 1 << 20);                         // output buffer size
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(33), active ? 1 << 19 : 0u);          // … fullness
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(37), (uint)pos);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(43), (uint)(pos * 1000));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(47), serverTimestamp);
        Send("STAT", b);
    }

    private uint Jiffies() => (uint)(Environment.TickCount64 - born);

    private void SendName()
    {
        var name = Encoding.UTF8.GetBytes(bridge.FriendlyName());
        var b = new byte[name.Length + 2];
        name.CopyTo(b, 1);
        Send("SETD", b);
    }

    // The name was changed on the bridge's page.
    public void Renamed() => SendName();

    // ------------------------------------------------------------ the server's commands

    private bool Ours()
    {
        var cur = bridge.Renderer.Current;
        lock (gate) return cur != null && mine.Contains(cur);
    }

    private void Handle(string op, byte[] data)
    {
        switch (op)
        {
            case "strm":
                if (Strm.Parse(data) is { } s) Stream(s);
                break;
            case "setd":
                if (data.Length >= 1 && data[0] == 0 && data.Length == 1) SendName();
                break;
            case "serv":
                if (data.Length >= 4)
                {
                    var ip = new IPAddress(data[..4]);
                    Log($"asked to move to the server at {ip}");
                    switchTo = ip.Equals(IPAddress.Any) ? null : ip;
                    try { tcp?.Close(); } catch (Exception) { /* reconnecting */ }
                }
                break;
            // aude, audg (volume: fixed at 100%, bit-perfect), vers, display and the rest: nothing to do.
        }
    }

    private void Stream(Strm s)
    {
        var r = bridge.Renderer;
        try
        {
            switch (s.Command)
            {
                case 't': Stat("STMt", s.ReplayGain); return;
                case 'q':
                case 'f':
                    if (Ours() && (r.IsActive || r.Transport == "PAUSED_PLAYBACK")) r.Stop().GetAwaiter().GetResult();
                    lock (gate) decodedSent = false;
                    Stat("STMf");
                    return;
                case 'p':
                    if (s.ReplayGain != 0) return;      // a timed pause, for players in sync
                    if (Ours()) r.Pause().GetAwaiter().GetResult();
                    Stat("STMp");
                    return;
                case 'u':
                    if (!Ours()) { Stat("STMn"); return; }
                    if (Claim("Play") is { } no) { Log(no); Stat("STMn"); return; }
                    r.Play().GetAwaiter().GetResult();
                    Stat("STMr");
                    return;
                case 's': Start(s); return;
            }
        }
        catch (UpnpException e) { Log(e.Message); Stat("STMn"); }
        catch (Exception e) { Log($"strm {s.Command}: {e.Message}"); Stat("STMn"); }
    }

    private string? Claim(string action)
    {
        var c = caller;
        if (c == null) return "not connected";
        var no = bridge.Arbiter.Claim(c, action, bridge.Renderer.IsActive);
        if (no != null)
        {
            Log($"refused {c.Name}'s {action}: {bridge.Arbiter.Owner?.Name} is using the DAC");
            bridge.Notify();
        }
        return no;
    }

    private void Start(Strm s)
    {
        var srv = server;
        if (srv == null) return;
        var ipBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ipBytes, s.ServerIp);
        var ip = s.ServerIp == 0 ? srv.Ip : new IPAddress(ipBytes);
        var url = $"http://{(ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString())}:{s.ServerPort}{s.Path()}";
        var mime = s.Mime();
        if (mime == "") { Log($"format '{s.Format}' isn't one this player offered"); Stat("STMn"); return; }
        if (Claim("Play") is { } no) { Log(no); Stat("STMn"); return; }

        var meta = $"<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">"
                   + $"<item id=\"slim\" parentID=\"0\" restricted=\"1\"><dc:title></dc:title><upnp:class>object.item.audioItem.musicTrack</upnp:class>"
                   + $"<res protocolInfo=\"http-get:*:{Xml.Esc(mime)}:*\">{Xml.Esc(url)}</res></item></DIDL-Lite>";
        var r = bridge.Renderer;
        Stat("STMc");
        bool gapless;
        lock (gate) gapless = decodedSent && r.IsActive && mine.Contains(r.Current!);
        if (gapless)
        {
            // The one playing has been read to its end and asked for this: it follows with no gap.
            var next = r.SetNext(url, meta).GetAwaiter().GetResult();
            lock (gate) { if (next != null) mine.Add(next); decodedSent = false; }
        }
        else
        {
            lock (gate) { mine.Clear(); decodedSent = false; }
            var t = r.SetUri(url, meta).GetAwaiter().GetResult();
            lock (gate) mine.Add(t);
            if (s.Autostart is '1' or '3') { if (!r.IsActive) r.Play().GetAwaiter().GetResult(); }
            else Stat("STMl");        // loaded: waiting for the server's 'u'
        }
        Log($"{bridge.Arbiter.Owner?.Name}: {(gapless ? "next " : "")}{s.Path()[..Math.Min(80, s.Path().Length)]} ({mime})");
    }

    // ------------------------------------------------------------ the renderer's side

    private bool IsMine(Track t) { lock (gate) return mine.Contains(t); }

    private void OnReached(Track t)
    {
        if (!IsMine(t)) return;
        Stat("STMs");
        _ = Task.Run(() => FetchMeta(t));
    }

    private void OnDecoded(Track t)
    {
        if (!IsMine(t)) return;
        lock (gate) decodedSent = true;
        bridge.Renderer.ExpectNext();
        Stat("STMd");
    }

    private void OnFinished(bool error)
    {
        if (!Ours()) return;
        lock (gate) decodedSent = false;
        Stat(error ? "STMn" : "STMu");
    }

    // What LMS says is playing: title, artist, album, cover. Not every server has JSON-RPC (Roon doesn't).
    private async Task FetchMeta(Track t)
    {
        var srv = server;
        if (srv == null) return;
        await Task.Delay(300);
        try
        {
            var body = $"{{\"id\":1,\"method\":\"slim.request\",\"params\":[\"{Mac}\",[\"status\",\"-\",\"1\",\"tags:aldjKc\"]]}}";
            using var res = await Json.PostAsync($"http://{srv.Ip}:{srv.JsonPort}/jsonrpc.js", new StringContent(body, Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var meta = ParseStatus(doc.RootElement, $"http://{srv.Ip}:{srv.JsonPort}", Mac);
            if (meta != null && IsMine(t)) bridge.Renderer.SetLive(t, meta);
        }
        catch (Exception) { /* no JSON-RPC: the page shows the format only */ }
    }

    public static LiveMeta? ParseStatus(JsonElement root, string web, string mac)
    {
        if (!root.TryGetProperty("result", out var res)) return null;
        static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
        static double D(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0) is var d ? d : 0;
        JsonElement song = default;
        if (res.TryGetProperty("playlist_loop", out var loop) && loop.ValueKind == JsonValueKind.Array && loop.GetArrayLength() > 0) song = loop[0];
        else if (res.TryGetProperty("remoteMeta", out var rm)) song = rm;
        if (song.ValueKind != JsonValueKind.Object) return null;
        var art = S(song, "artwork_url");
        if (art != "" && !art.StartsWith("http", StringComparison.OrdinalIgnoreCase)) art = web + (art.StartsWith('/') ? "" : "/") + art;
        if (art == "" && S(song, "coverid") is { Length: > 0 } cover) art = $"{web}/music/{Uri.EscapeDataString(cover)}/cover.jpg";
        if (art == "") art = $"{web}/music/current/cover.jpg?player={Uri.EscapeDataString(mac)}";
        double duration = D(song, "duration") is > 0 and var sd ? sd : D(res, "duration");
        double? time = res.TryGetProperty("time", out _) ? D(res, "time") : null;
        return new LiveMeta(S(song, "title"), S(song, "artist"), S(song, "album"), art, duration, time, Arbiter.Now);
    }

    public void Dispose()
    {
        stop.Cancel();
        heartbeat.Dispose();
        var r = bridge.Renderer;
        r.Reached -= OnReached;
        r.Decoded -= OnDecoded;
        r.Finished -= OnFinished;
        // Switched off while the server plays: that stops too.
        if (Ours() && (r.IsActive || r.Transport == "PAUSED_PLAYBACK"))
            try { r.Stop().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* closing */ }
        lock (sendGate) { try { tcp?.Close(); } catch (Exception) { /* closing */ } }
        thread.Join(TimeSpan.FromSeconds(2));
    }
}
