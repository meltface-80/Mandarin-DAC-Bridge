// Volume.cs — the DAC's own volume, where it has one: set in the DAC (its
// USB volume control, applied after the samples arrive), so the stream stays
// bit-perfect. A headphone DAC like the DragonFly has no knob: its volume is
// only ever set this way.
//
//   Linux    the card's ALSA mixer (its playback volume control)   AlsaVolume.cs
//   macOS    Core Audio's device volume, where it is settable      MacVolume.cs
//   Windows  the endpoint volume, where the hardware does it       WindowsVolume.cs
//
// Levels are 0–100 and follow the DAC's dB range the way alsamixer shows it:
// linear in dB over a small range, else so that each step sounds alike.
namespace MandarinDacBridge.Audio;

internal interface IDacVolume : IDisposable
{
    // The DAC has a volume the bridge can set.
    bool Available { get; }
    // What it is, for the page ("PCM", "the DAC's volume").
    string Control { get; }
    // 0–100 now (it can be changed elsewhere too); null when it can't be read.
    int? Read();
    // The level in dB, where the DAC says.
    double? ReadDb() => null;
    bool Set(int level);
}

internal sealed class NoVolume : IDacVolume
{
    public static readonly NoVolume Instance = new();
    public bool Available => false;
    public string Control => "";
    public int? Read() => null;
    public bool Set(int level) => false;
    public void Dispose() { }
}

// For the tests: a DAC with a volume control from -60 to 0 dB.
internal sealed class TestVolume(int level = 40) : IDacVolume
{
    public bool Available => true;
    public string Control => "Test";
    public int Level { get; private set; } = level;
    public int? Read() => Level;
    public double? ReadDb() => VolumeCurve.ToDb(Level / 100.0, -60, 0);
    public bool Set(int level) { Level = Math.Clamp(level, 0, 100); return true; }
    public void Dispose() { }
}

internal static class VolumeCurve
{
    // alsa-utils' volume_mapping: over 24 dB or less, linear in dB; over more, 10^(dB/60), scaled so the bottom is 0.
    private const double LinearUpTo = 24;

    public static double ToLevel(double db, double minDb, double maxDb)
    {
        if (maxDb <= minDb) return 1;
        if (maxDb - minDb <= LinearUpTo) return Math.Clamp((db - minDb) / (maxDb - minDb), 0, 1);
        double minN = Math.Pow(10, (minDb - maxDb) / 60);
        return Math.Clamp((Math.Pow(10, (db - maxDb) / 60) - minN) / (1 - minN), 0, 1);
    }

    public static double ToDb(double level, double minDb, double maxDb)
    {
        level = Math.Clamp(level, 0, 1);
        if (maxDb <= minDb) return maxDb;
        if (maxDb - minDb <= LinearUpTo) return minDb + level * (maxDb - minDb);
        double minN = Math.Pow(10, (minDb - maxDb) / 60);
        double n = level * (1 - minN) + minN;
        return n <= 0 ? minDb : Math.Max(minDb, 60 * Math.Log10(n) + maxDb);
    }

    // A gain in dB (a Squeezebox server's, say) as a level: the same curve over 60 dB.
    public static int LevelOfGainDb(double db) => db <= -60 ? 0 : (int)Math.Round(100 * ToLevel(db, -60, 0));
}

internal static class VolumeFactory
{
    public static IDacVolume Create(DacDevice dev, Config config)
    {
        try
        {
            if (config.TestDevices != null) return dev.TestVolume is { } v ? new TestVolume(v) : NoVolume.Instance;
            if (OperatingSystem.IsLinux()) return AlsaVolume.Open(dev.Spec) ?? (IDacVolume)NoVolume.Instance;
            if (OperatingSystem.IsMacOS()) return MacVolume.Open(dev.Spec) ?? (IDacVolume)NoVolume.Instance;
            if (OperatingSystem.IsWindows()) return WindowsVolume.Open(dev.Spec) ?? (IDacVolume)NoVolume.Instance;
        }
        catch (Exception e) { Log.Write($"[{dev.Name}] volume: {e.Message}"); }
        return NoVolume.Instance;
    }
}
