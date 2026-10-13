// UpnpEventsTests.cs — UPnP events (Upnp/Events.cs) as a controller receives them: in order, one at a
// time, and none lost when one couldn't be delivered. A pretend controller answers the NOTIFYs.
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using MandarinDacBridge.Upnp;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class UpnpEventsTests : IDisposable
{
    private readonly HttpListener controller = new();
    private readonly int port;
    private readonly ConcurrentQueue<(uint Seq, string State)> received = new();
    private int inFlight, mostInFlight;
    private volatile int slowFirst;            // ms the first NOTIFY is answered after
    private volatile int failFirst;            // how many NOTIFYs are answered 500 first

    private Dictionary<string, string> state = new() { ["TransportState"] = "STOPPED" };
    private readonly object stateGate = new();

    public UpnpEventsTests()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        controller.Prefixes.Add($"http://127.0.0.1:{port}/");
        controller.Start();
        _ = Task.Run(async () =>
        {
            while (controller.IsListening)
            {
                HttpListenerContext c;
                try { c = await controller.GetContextAsync(); } catch (Exception) { return; }   // stopped
                _ = Task.Run(async () =>
                {
                    int now = Interlocked.Increment(ref inFlight);
                    InterlockedMax(ref mostInFlight, now);
                    var seq = uint.Parse(c.Request.Headers["SEQ"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                    if (seq == 0 && slowFirst > 0) await Task.Delay(slowFirst);
                    bool fail = Interlocked.Decrement(ref failFirst) >= 0;
                    if (!fail)
                    {
                        var m = TransportState().Match(WebUtility.HtmlDecode(body));
                        received.Enqueue((seq, m.Success ? m.Groups[1].Value : ""));
                    }
                    Interlocked.Decrement(ref inFlight);
                    c.Response.StatusCode = fail ? 500 : 200;
                    c.Response.Close();
                });
            }
        });
    }

    public void Dispose() => controller.Stop();

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    private Events Subscribed()
    {
        var events = new Events((bridge, service) => { lock (stateGate) return service == "AVTransport" ? new(state) : null; });
        var (status, sid, _) = events.Request("SUBSCRIBE", "dac", "AVTransport", null, $"<http://127.0.0.1:{port}/cb>", "upnp:event", "Second-300");
        Assert.Equal(200, status);
        Assert.NotNull(sid);
        return events;
    }

    private void Set(string transport) { lock (stateGate) state = new() { ["TransportState"] = transport }; }

    private static async Task Until(Func<bool> done, int ms = 8000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done()) { Assert.True(clock.ElapsedMilliseconds < ms, "timed out"); await Task.Delay(20); }
    }

    // While the controller takes its time answering one event, the next isn't sent over it: they arrive
    // one at a time, in order, and the last state is the one it ends with.
    [Fact]
    public async Task EventsGoOneAtATime_InOrder()
    {
        slowFirst = 600;
        var events = Subscribed();
        await Task.Delay(100);                    // the first event (everything) is on its way, slowly
        foreach (var s in new[] { "TRANSITIONING", "PLAYING", "PAUSED_PLAYBACK", "STOPPED" })
        {
            Set(s);
            events.Changed("dac");
            await Task.Delay(200);                // past the 150 ms in which changes are gathered
        }
        await Until(() => received.Count >= 5);
        var got = received.ToArray();
        Assert.Equal(1, Volatile.Read(ref mostInFlight));
        Assert.Equal(got.Select(g => g.Seq).Order(), got.Select(g => g.Seq));
        Assert.Equal("STOPPED", got[^1].State);
    }

    // An event the controller didn't take (an error, a moment's outage) goes again: it isn't lost.
    [Fact]
    public async Task AnEventThatWasntDelivered_GoesAgain()
    {
        var events = Subscribed();
        await Until(() => received.Any(g => g.State == "STOPPED"));
        failFirst = 1;
        Set("PLAYING");
        events.Changed("dac");
        await Until(() => received.Any(g => g.State == "PLAYING"));
    }

    [GeneratedRegex("TransportState val=\"([A-Z_]+)\"")] private static partial Regex TransportState();
}
