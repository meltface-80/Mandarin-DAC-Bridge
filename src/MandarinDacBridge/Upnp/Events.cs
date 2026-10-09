// Events.cs — UPnP eventing (GENA): controllers SUBSCRIBE to a service and
// are sent a NOTIFY whenever its state changes (AVTransport and
// RenderingControl in a LastChange document, ConnectionManager plainly). This
// is how Audirvana and Mandarin see a track end or the next one begin without
// asking.
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace MandarinDacBridge.Upnp;

internal sealed class Events(Func<string, string, Dictionary<string, string>?> state)
{
    private const int TimeoutS = 1800;

    private sealed class Sub(string bridge, string service, string[] urls)
    {
        public string Bridge { get; } = bridge;
        public string Service { get; } = service;
        public string[] Urls { get; } = urls;
        public uint Seq;
        public long Expires;
        public Dictionary<string, string> Last { get; } = new();
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<string, Sub> subs = new();
    private readonly ConcurrentDictionary<string, bool> pending = new();
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly HttpMethod Notify = new("NOTIFY");

    public static string LastChange(string service, Dictionary<string, string> vars)
    {
        var ns = service == "AVTransport" ? "urn:schemas-upnp-org:metadata-1-0/AVT/" : "urn:schemas-upnp-org:metadata-1-0/RCS/";
        var sb = new StringBuilder($"<Event xmlns=\"{ns}\"><InstanceID val=\"0\">");
        foreach (var (k, v) in vars)
        {
            var ch = service == "RenderingControl" && k is "Volume" or "Mute" or "VolumeDB" ? " channel=\"Master\"" : "";
            sb.Append($"<{k}{ch} val=\"{Xml.Esc(v)}\"/>");
        }
        return sb.Append("</InstanceID></Event>").ToString();
    }

    public static string PropertySet(string service, Dictionary<string, string> vars)
    {
        var props = service is "AVTransport" or "RenderingControl"
            ? $"<e:property><LastChange>{Xml.Esc(LastChange(service, vars))}</LastChange></e:property>"
            : string.Concat(vars.Select(kv => $"<e:property><{kv.Key}>{Xml.Esc(kv.Value)}</{kv.Key}></e:property>"));
        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">{props}</e:propertyset>";
    }

    // SUBSCRIBE / UNSUBSCRIBE: → (status, sid, timeout)
    public (int Status, string? Sid, int Timeout) Request(string method, string bridge, string service, string? sid, string? callback, string? nt, string? timeoutHeader)
    {
        if (method == "UNSUBSCRIBE") return (sid != null && subs.TryRemove(sid, out _) ? 200 : 412, null, 0);
        var tm = Regex.Match(timeoutHeader ?? "", @"Second-(\d+)", RegexOptions.IgnoreCase);
        int timeout = tm.Success && int.TryParse(tm.Groups[1].Value, out var t) ? Math.Min(TimeoutS, t) : TimeoutS;
        if (!string.IsNullOrEmpty(sid))
        {
            if (!subs.TryGetValue(sid, out var s) || s.Bridge != bridge || s.Service != service) return (412, null, 0);
            s.Expires = Arbiter.Now + timeout * 1000L;
            return (200, sid, timeout);
        }
        var urls = Regex.Matches(callback ?? "", "<([^>]+)>").Select(m => m.Groups[1].Value)
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (urls.Length == 0 || !string.Equals(nt, "upnp:event", StringComparison.OrdinalIgnoreCase)) return (412, null, 0);
        var id = "uuid:" + Guid.NewGuid();
        var sub = new Sub(bridge, service, urls) { Expires = Arbiter.Now + timeout * 1000L };
        subs[id] = sub;
        // The first event carries everything, just after the answer.
        _ = Task.Delay(50).ContinueWith(_ => { if (state(bridge, service) is { } all) Send(id, sub, all, true); });
        return (200, id, timeout);
    }

    // Something about this bridge changed: tell its subscribers what, a moment later (changes come in bursts).
    public void Changed(string bridge)
    {
        foreach (var service in new[] { "AVTransport", "RenderingControl", "ConnectionManager" })
        {
            var key = bridge + "|" + service;
            if (!pending.TryAdd(key, true)) continue;
            _ = Task.Delay(150).ContinueWith(t =>
            {
                pending.TryRemove(key, out bool _);
                var now = state(bridge, service);
                if (now == null) return;
                foreach (var (sid, sub) in subs)
                {
                    if (sub.Bridge != bridge || sub.Service != service) continue;
                    if (sub.Expires < Arbiter.Now) { subs.TryRemove(sid, out _); continue; }
                    Dictionary<string, string> diff;
                    lock (sub.Gate) diff = now.Where(kv => !sub.Last.TryGetValue(kv.Key, out var v) || v != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
                    if (diff.Count > 0) Send(sid, sub, diff, false);
                }
            });
        }
    }

    // The bridge has gone: its subscriptions too.
    public void Drop(string bridge)
    {
        foreach (var (sid, sub) in subs) if (sub.Bridge == bridge) subs.TryRemove(sid, out _);
    }

    private void Send(string sid, Sub sub, Dictionary<string, string> vars, bool initial)
    {
        if (!subs.ContainsKey(sid)) return;
        uint seq;
        string body;
        lock (sub.Gate)
        {
            foreach (var (k, v) in vars) sub.Last[k] = v;
            seq = sub.Seq;
            sub.Seq = sub.Seq == uint.MaxValue ? 1 : sub.Seq + 1;
            body = PropertySet(sub.Service, vars);
        }
        _ = Task.Run(async () =>
        {
            foreach (var url in sub.Urls)
            {
                try
                {
                    using var req = new HttpRequestMessage(Notify, url) { Content = new StringContent(body, Encoding.UTF8) };
                    req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
                    req.Headers.TryAddWithoutValidation("NT", "upnp:event");
                    req.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
                    req.Headers.TryAddWithoutValidation("SID", sid);
                    req.Headers.TryAddWithoutValidation("SEQ", seq.ToString());
                    using var res = await Http.SendAsync(req);
                    if ((int)res.StatusCode == 412) subs.TryRemove(sid, out _);
                    return;
                }
                catch (Exception) { /* the next callback URL, if any */ }
            }
            if (!initial) Log.Write($"event to {sub.Urls[0]} failed");
        });
    }
}
