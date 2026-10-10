// Arbiter.cs — who is in charge of a DAC, and keeping everyone else out.
//
// Every control request (SOAP) comes from a controller: Audirvana, Mandarin,
// or anything else that speaks UPnP. The first to start playing owns the
// DAC. While it plays, and for a short grace after it stops or pauses (so a
// controller that loads each track with Stop → SetAVTransportURI → Play keeps
// it between tracks), any other controller's request to change anything is
// refused with UPnP error 705, "Transport is locked". Reading the state is
// open to all.
//
// Controllers are told apart by address and User-Agent, and named from the
// User-Agent or, for Mandarin (which sends none), by its stream URLs.
using System.Text.RegularExpressions;

namespace MandarinDacBridge;

internal sealed record Caller(string Key, string Name, string Ip);

internal sealed record Blocked(string Name, string Action, long At);

internal sealed partial class Arbiter(TimeSpan grace, int mandarinPort)
{
    // Stream ports that say who is sending (QobuzProxy's audio proxies, one per speaker).
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> StreamPorts = new();

    private readonly object gate = new();
    private readonly Dictionary<string, string> names = new();
    private long lastActive;

    public Caller? Owner { get; private set; }
    public Blocked? LastBlocked { get; private set; }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Caller Identify(string ip, string userAgent, string? uri)
    {
        ip = ip.Replace("::ffff:", "");
        if (ip == "") ip = "?";
        var ua = userAgent.Trim();
        var key = ip + "|" + ua;
        var name = NameFromUa(ua);
        if (name == "" && !string.IsNullOrEmpty(uri)) name = NameFromUri(uri);
        lock (gate)
        {
            if (name != "") names[key] = name;
            else if (!names.TryGetValue(key, out name!))
                name = ua != "" && !GenericUa().IsMatch(ua) ? Regex.Split(ua, @"[/\s(]")[0] : $"a controller at {ip}";
        }
        return new Caller(key, name, ip);
    }

    private string NameFromUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return "";
        if (u.Port == mandarinPort) return "Mandarin";
        if (StreamPorts.TryGetValue(u.Port, out var who)) return who;
        if (u.AbsolutePath.Contains("audirvana", StringComparison.OrdinalIgnoreCase)) return "Audirvana";
        return "";
    }

    public static string NameFromUa(string ua)
    {
        if (Regex.IsMatch(ua, "audirvana", RegexOptions.IgnoreCase)) return "Audirvana";
        if (Regex.IsMatch(ua, "mandarin|musicd", RegexOptions.IgnoreCase)) return "Mandarin";
        if (Regex.IsMatch(ua, "bubbleupnp", RegexOptions.IgnoreCase)) return "BubbleUPnP";
        if (Regex.IsMatch(ua, "roon", RegexOptions.IgnoreCase)) return "Roon";
        if (Regex.IsMatch(ua, @"jriver|media\s*center", RegexOptions.IgnoreCase)) return "JRiver";
        if (Regex.IsMatch(ua, "foobar", RegexOptions.IgnoreCase)) return "foobar2000";
        if (Regex.IsMatch(ua, "mconnect", RegexOptions.IgnoreCase)) return "mconnect";
        if (Regex.IsMatch(ua, "kazoo|lumin|linn", RegexOptions.IgnoreCase)) return Regex.Split(ua, @"[/\s]")[0];
        return "";
    }

    // A change request: null if allowed (and the caller now owns the DAC), else why not.
    public string? Claim(Caller caller, string action, bool active)
    {
        lock (gate)
        {
            if (Owner != null && Owner.Key != caller.Key && (active || Now - lastActive < grace.TotalMilliseconds))
            {
                LastBlocked = new Blocked(caller.Name, action, Now);
                return $"Transport is locked: {Owner.Name} is using this DAC";
            }
            Owner = caller;
            lastActive = Now;
            return null;
        }
    }

    // Volume and mute: refused while another app has the DAC, like a claim, but without taking it (turning the
    // volume while nothing plays doesn't keep the other app out).
    public string? Check(Caller caller, string action, bool active)
    {
        lock (gate)
        {
            if (Owner != null && Owner.Key != caller.Key && (active || Now - lastActive < grace.TotalMilliseconds))
            {
                LastBlocked = new Blocked(caller.Name, action, Now);
                return $"Transport is locked: {Owner.Name} is using this DAC";
            }
            return null;
        }
    }

    public void Touch() { lock (gate) lastActive = Now; }

    public void Release() { lock (gate) { Owner = null; lastActive = 0; } }

    // Tests: as if the grace had run out.
    internal void Expire() { lock (gate) lastActive = 0; }

    public bool Holding(bool active) { lock (gate) return Owner != null && (active || Now - lastActive < grace.TotalMilliseconds); }

    [GeneratedRegex("^(cfnetwork|darwin|java|dalvik|okhttp|python|go-http|curl|wget|mozilla|node|libupnp|portable sdk|upnp|dlna)", RegexOptions.IgnoreCase)]
    private static partial Regex GenericUa();
}
