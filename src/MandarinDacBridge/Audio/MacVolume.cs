// MacVolume.cs — a USB DAC's volume control on macOS: Core Audio's device
// volume (the DAC's own USB volume control: a hogged DAC gets no software
// volume from macOS), on the main element or on each channel. Core Audio's
// scalar already follows the DAC's dB range so that steps sound alike.
using MandarinDacBridge.Native;

namespace MandarinDacBridge.Audio;

internal sealed unsafe class MacVolume : IDacVolume
{
    private static readonly uint VolumeDb = CoreAudio.Code("vold");

    private readonly uint dev;
    private readonly uint[] elements;   // 0 (main), or each channel

    public bool Available => true;
    public string Control => "the DAC's volume";

    private MacVolume(uint dev, uint[] elements) { this.dev = dev; this.elements = elements; }

    public static MacVolume? Open(string uid)
    {
        var dev = CoreAudio.FindDevice(uid);
        if (dev == CoreAudio.Unknown) return null;
        if (Settable(dev, 0)) return new MacVolume(dev, [0]);
        var channels = Enumerable.Range(1, Math.Max(2, CoreAudio.OutputChannels(dev))).Select(i => (uint)i).Where(e => Settable(dev, e)).ToArray();
        return channels.Length > 0 ? new MacVolume(dev, channels) : null;
    }

    private static bool Settable(uint dev, uint element)
    {
        var a = new PropertyAddress(CoreAudio.DeviceVolumeScalar, CoreAudio.ScopeOutput, element);
        if (CoreAudio.HasProperty(dev, in a) == 0) return false;
        byte ok = 0;
        return CoreAudio.IsPropertySettable(dev, in a, &ok) == 0 && ok != 0;
    }

    public int? Read() =>
        CoreAudio.Get<float>(dev, CoreAudio.DeviceVolumeScalar, CoreAudio.ScopeOutput, elements[0]) is { } v ? (int)Math.Round(v * 100) : null;

    public double? ReadDb() => CoreAudio.Get<float>(dev, VolumeDb, CoreAudio.ScopeOutput, elements[0]);

    public bool Set(int level)
    {
        float v = Math.Clamp(level, 0, 100) / 100f;
        bool ok = true;
        foreach (var e in elements)
        {
            var a = new PropertyAddress(CoreAudio.DeviceVolumeScalar, CoreAudio.ScopeOutput, e);
            ok &= CoreAudio.SetPropertyData(dev, in a, 0, null, sizeof(float), &v) == 0;
        }
        return ok;
    }

    public void Dispose() { }
}
