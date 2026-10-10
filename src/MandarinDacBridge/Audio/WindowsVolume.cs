// WindowsVolume.cs — a USB DAC's volume control on Windows: the endpoint
// volume, used only where the DAC applies it itself (QueryHardwareSupport). In
// exclusive mode Windows applies no volume of its own, so a DAC without one
// stays where it is.
using System.Runtime.InteropServices.Marshalling;
using MandarinDacBridge.Native;

namespace MandarinDacBridge.Audio;

internal sealed class WindowsVolume : IDacVolume
{
    private readonly object gate = new();
    private IAudioEndpointVolume? ev;
    private readonly IMMDevice device;
    private readonly IMMDeviceEnumerator enumerator;

    public bool Available => true;
    public string Control => "the DAC's volume";

    private WindowsVolume(IMMDeviceEnumerator enumerator, IMMDevice device, IAudioEndpointVolume ev)
    {
        this.enumerator = enumerator;
        this.device = device;
        this.ev = ev;
    }

    public static WindowsVolume? Open(string endpointId)
    {
        Wasapi.Init();
        var enumerator = Wasapi.Enumerator();
        IMMDevice? device = null;
        IAudioEndpointVolume? ev = null;
        try
        {
            if (enumerator.GetDevice(endpointId, out device) != 0 || device == null) return null;
            var iid = Wasapi.IidEndpointVolume;
            if (device.Activate(ref iid, Wasapi.ClsCtxAll, 0, out var p) != 0 || p == 0) return null;
            ev = Wasapi.Wrap<IAudioEndpointVolume>(p);
            if (ev.QueryHardwareSupport(out var mask) != 0 || (mask & Wasapi.HardwareVolume) == 0) return null;
            var v = new WindowsVolume(enumerator, device, ev);
            ev = null; device = null; enumerator = null!;
            return v;
        }
        finally
        {
            Release(ev);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? com)
    {
        if (com is ComObject o) { try { o.FinalRelease(); } catch (Exception) { /* gone */ } }
    }

    public int? Read()
    {
        lock (gate)
        {
            if (ev == null) return null;
            Wasapi.Init();
            return ev.GetMasterVolumeLevelScalar(out var v) == 0 ? (int)Math.Round(v * 100) : null;
        }
    }

    public double? ReadDb()
    {
        lock (gate)
        {
            if (ev == null) return null;
            Wasapi.Init();
            return ev.GetMasterVolumeLevel(out var db) == 0 ? db : null;
        }
    }

    public bool Set(int level)
    {
        lock (gate)
        {
            if (ev == null) return false;
            Wasapi.Init();
            return ev.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, 0) == 0;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            Release(ev);
            ev = null;
            Release(device);
            Release(enumerator);
        }
    }
}
