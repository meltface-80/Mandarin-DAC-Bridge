// Mandarin DAC Bridge — takes each USB DAC on this machine for itself
// (exclusive, bit-perfect, the DAC switched to every track's own rate) and
// offers it on the network as a UPnP renderer, so Audirvana and Mandarin can
// both play to it — one at a time, never over each other.
//
//   mandarin-dac-bridge            run (the page is at http://<this machine>:55500)
//   mandarin-dac-bridge --list     the DACs found, and what each takes
//   mandarin-dac-bridge --diagnose everything the machine says about its sound devices
//   mandarin-dac-bridge --loopback-test  (macOS) the DAC Bridge output gives back exactly what it gets
//   mandarin-dac-bridge --version
//
// Settings: Config.cs.
using System.Net;
using System.Runtime.InteropServices;
using MandarinDacBridge;
using MandarinDacBridge.Upnp;

if (args.Contains("--version")) { Console.WriteLine(Config.Version); return 0; }

var config = Config.FromEnvironment();

if (args.Contains("--diagnose")) { Console.WriteLine(Devices.Diagnose()); return 0; }

if (args.Contains("--loopback-test")) return MandarinDacBridge.Mac.LoopbackTest.Run();

if (args.Contains("--list"))
{
    var (devices, error, skipped) = Devices.ListWithSkipped(config);
    if (error != "") Console.WriteLine("! " + error);
    if (devices.Count == 0) Console.WriteLine("No USB DACs found.");
    foreach (var d in devices)
    {
        Console.WriteLine($"{d.Name} — {d.Manufacturer} ({d.Transport}{(d.Usb != null ? " " + d.Usb : "")})");
        Console.WriteLine($"  rates: {string.Join(", ", d.Rates.Select(Devices.KHz))}");
        Console.WriteLine($"  bits: {string.Join(", ", d.Bits)}  channels: {d.Channels}{(d.DsdNative.Length > 0 ? "  native DSD" : "")}");
        if (d.HolderPid > 0) Console.WriteLine($"  held by {(d.HolderName != "" ? d.HolderName : "process " + d.HolderPid)}");
    }
    if (skipped.Count > 0)
    {
        Console.WriteLine("Also seen, not bridged:");
        foreach (var s in skipped) Console.WriteLine("  " + s);
    }
    return 0;
}

var host = new BridgeHost(config);
try { await host.StartAsync(); }
catch (IOException e) when (e.InnerException is System.Net.Sockets.SocketException || e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    Log.Write($"port {config.Port} is in use: is the bridge already running?");
    return 1;
}

var stop = new TaskCompletionSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.TrySetResult(); });
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.TrySetResult(); });
await stop.Task;
Log.Write("stopping: letting go of the DACs");
await host.StopAsync();
return 0;

namespace MandarinDacBridge
{
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.Extensions.Logging;

    // Everything, started and stopped together (the tests run it in-process).
    internal sealed class BridgeHost(Config config)
    {
        private WebApplication? app;
        public Manager Manager { get; } = new(config);
        public Events Events { get; private set; } = null!;
        public Ssdp Ssdp { get; private set; } = null!;

        public async Task StartAsync()
        {
            Events = new Events((id, service) =>
            {
                var b = Manager.Get(id);
                if (b == null) return null;
                return service switch
                {
                    "AVTransport" => Control.AvtState(b.Renderer),
                    "RenderingControl" => Control.RcsState(b),
                    _ => Control.CmsState(b)
                };
            });
            Ssdp = new Ssdp(config.Port, config.BindIp,
                $"{(OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux")}/{Environment.OSVersion.Version} UPnP/1.0 MandarinDacBridge/{Config.Version}",
                () => Manager.Bridges().Select(b => b.Advert));

            Manager.Added += b =>
            {
                b.Log($"offering {b.FriendlyName()} on the network");
                _ = Task.Delay(300).ContinueWith(_ => Ssdp.Announce(b.Advert));
            };
            Manager.Removed += b => { Ssdp.ByeBye(b.Advert); Events.Drop(b.Id); };
            Manager.Renamed += b =>
            {
                // Controllers re-read a device that says goodbye and comes back.
                Ssdp.ByeBye(b.Advert);
                _ = Task.Delay(1500).ContinueWith(_ => Ssdp.Announce(b.Advert));
            };
            Manager.BridgeChanged += b => Events.Changed(b.Id);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Any, config.Port);
                k.AddServerHeader = false;
            });
            app = builder.Build();
            app.Run(ctx => Web.Handle(ctx, Manager, Events));
            await app.StartAsync();

            Log.Write($"Mandarin DAC Bridge {Config.Version} on {config.Platform}");
            var ips = Ssdp.Interfaces(config.BindIp).Select(i => $"http://{i.Address}:{config.Port}");
            Log.Write($"the page: http://localhost:{config.Port}  ·  {string.Join("  ·  ", ips)}");
            try { Ssdp.Start(); }
            catch (Exception e) { Log.Write("SSDP (discovery) couldn't start: " + e.Message); }
            Manager.Start();
        }

        public async Task StopAsync()
        {
            try { Ssdp.Dispose(); } catch (Exception) { /* closing */ }
            Manager.Dispose();
            if (app != null) await app.StopAsync();
        }
    }
}
