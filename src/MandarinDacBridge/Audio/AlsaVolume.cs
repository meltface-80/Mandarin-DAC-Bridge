// AlsaVolume.cs — a USB DAC's volume control on Linux, through the card's
// ALSA mixer (the control alsamixer shows for it, usually "PCM"). The mixer
// is separate from the PCM device the bridge holds, so it can be set while the
// DAC plays.
using System.Runtime.InteropServices;

namespace MandarinDacBridge.Audio;

internal static unsafe partial class AlsaMixer
{
    private const string Lib = "libasound.so.2";
    public const int FrontLeft = 0;

    [LibraryImport(Lib, EntryPoint = "snd_mixer_open")] public static partial int Open(out IntPtr mixer, int mode);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_attach", StringMarshalling = StringMarshalling.Utf8)] public static partial int Attach(IntPtr mixer, string name);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_register")] public static partial int Register(IntPtr mixer, IntPtr options, IntPtr classp);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_load")] public static partial int Load(IntPtr mixer);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_close")] public static partial int Close(IntPtr mixer);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_handle_events")] public static partial int HandleEvents(IntPtr mixer);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_first_elem")] public static partial IntPtr FirstElem(IntPtr mixer);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_elem_next")] public static partial IntPtr NextElem(IntPtr elem);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_get_name")] public static partial IntPtr Name(IntPtr elem);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_is_active")] public static partial int IsActive(IntPtr elem);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_has_playback_volume")] public static partial int HasPlaybackVolume(IntPtr elem);
    // C long is pointer-sized on Linux (LP64 and ILP32): nint.
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_get_playback_volume_range")] public static partial int VolumeRange(IntPtr elem, out nint min, out nint max);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_get_playback_dB_range")] public static partial int DbRange(IntPtr elem, out nint min, out nint max);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_get_playback_volume")] public static partial int GetVolume(IntPtr elem, int channel, out nint value);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_get_playback_dB")] public static partial int GetDb(IntPtr elem, int channel, out nint value);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_set_playback_volume_all")] public static partial int SetVolumeAll(IntPtr elem, nint value);
    [LibraryImport(Lib, EntryPoint = "snd_mixer_selem_set_playback_dB_all")] public static partial int SetDbAll(IntPtr elem, nint value, int dir);
}

internal sealed class AlsaVolume : IDacVolume
{
    // Preferred names, in order; else the first control with a playback volume.
    private static readonly string[] Names = ["PCM", "Master", "Headphone", "Speaker", "Digital", "Playback"];

    private readonly object gate = new();
    private IntPtr mixer;
    private readonly IntPtr elem;
    private readonly long rawMin, rawMax;
    private readonly double? minDb, maxDb;
    // The level last set, while the DAC is still where it was put (its steps can be coarser than 1%).
    private int setLevel = -1;
    private long setRaw = long.MinValue;

    public bool Available => true;
    public string Control { get; }

    private AlsaVolume(IntPtr mixer, IntPtr elem, string name)
    {
        this.mixer = mixer;
        this.elem = elem;
        Control = name;
        AlsaMixer.VolumeRange(elem, out var lo, out var hi);
        rawMin = lo; rawMax = hi;
        if (AlsaMixer.DbRange(elem, out var dlo, out var dhi) == 0 && dhi > dlo)
        {
            // A bottom of "muted" (SND_CTL_TLV_DB_GAIN_MUTE, -99999.99 dB) works out as no floor to the curve.
            minDb = dlo / 100.0;
            maxDb = dhi / 100.0;
        }
    }

    // The card of "hw:CARD=Cobalt,DEV=0" is "hw:CARD=Cobalt".
    public static AlsaVolume? Open(string spec)
    {
        var card = string.Join(',', spec.Split(',').Where(p => !p.StartsWith("DEV=", StringComparison.OrdinalIgnoreCase)));
        if (AlsaMixer.Open(out var m, 0) < 0) return null;
        try
        {
            if (AlsaMixer.Attach(m, card) < 0 || AlsaMixer.Register(m, IntPtr.Zero, IntPtr.Zero) < 0 || AlsaMixer.Load(m) < 0)
            {
                AlsaMixer.Close(m);
                return null;
            }
            var found = new List<(IntPtr Elem, string Name)>();
            for (var e = AlsaMixer.FirstElem(m); e != IntPtr.Zero; e = AlsaMixer.NextElem(e))
            {
                if (AlsaMixer.IsActive(e) == 0 || AlsaMixer.HasPlaybackVolume(e) == 0) continue;
                AlsaMixer.VolumeRange(e, out var lo, out var hi);
                if (hi <= lo) continue;
                found.Add((e, Marshal.PtrToStringUTF8(AlsaMixer.Name(e)) ?? ""));
            }
            if (found.Count == 0) { AlsaMixer.Close(m); return null; }
            var pick = found.OrderBy(f => Array.FindIndex(Names, n => f.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : Names.Length).First();
            return new AlsaVolume(m, pick.Elem, pick.Name);
        }
        catch (Exception) { AlsaMixer.Close(m); throw; }
    }

    private long Raw()
    {
        AlsaMixer.GetVolume(elem, AlsaMixer.FrontLeft, out var v);
        return v;
    }

    public int? Read()
    {
        lock (gate)
        {
            if (mixer == IntPtr.Zero) return null;
            AlsaMixer.HandleEvents(mixer);
            long raw = Raw();
            if (raw == setRaw && setLevel >= 0) return setLevel;
            if (minDb is { } lo && maxDb is { } hi && AlsaMixer.GetDb(elem, AlsaMixer.FrontLeft, out var db) == 0)
                return (int)Math.Round(100 * VolumeCurve.ToLevel(db / 100.0, lo, hi));
            return rawMax > rawMin ? (int)Math.Round(100.0 * (raw - rawMin) / (rawMax - rawMin)) : null;
        }
    }

    public double? ReadDb()
    {
        lock (gate)
        {
            if (mixer == IntPtr.Zero || minDb == null) return null;
            AlsaMixer.HandleEvents(mixer);
            return AlsaMixer.GetDb(elem, AlsaMixer.FrontLeft, out var db) == 0 ? db / 100.0 : null;
        }
    }

    public bool Set(int level)
    {
        level = Math.Clamp(level, 0, 100);
        lock (gate)
        {
            if (mixer == IntPtr.Zero) return false;
            AlsaMixer.HandleEvents(mixer);
            int err;
            if (minDb is { } lo && maxDb is { } hi)
            {
                double target = VolumeCurve.ToDb(level / 100.0, lo, hi);
                AlsaMixer.GetDb(elem, AlsaMixer.FrontLeft, out var now);
                // Rounded towards the way it moves, so a small step still moves it.
                err = AlsaMixer.SetDbAll(elem, (nint)Math.Round(target * 100), target * 100 >= now ? 1 : -1);
            }
            else
                err = AlsaMixer.SetVolumeAll(elem, (nint)(rawMin + Math.Round(level / 100.0 * (rawMax - rawMin))));
            if (err < 0) return false;
            setLevel = level;
            setRaw = Raw();
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (mixer != IntPtr.Zero) AlsaMixer.Close(mixer);
            mixer = IntPtr.Zero;
        }
    }
}
