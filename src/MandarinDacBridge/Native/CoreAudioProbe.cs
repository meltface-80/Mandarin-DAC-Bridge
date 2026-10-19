// CoreAudioProbe.cs — asking Core Audio about a DAC's formats and keeping every answer, for when it lists
// none (dev/design/install.md, Part 2). GetArray says nothing about why a list is empty; this records each
// step (is the property there, the size, the read) for the device and every output stream, so the log and
// --diagnose show which one failed. What it read is described by Audio/MacFormats.cs.
using System.Runtime.InteropServices;
using MandarinDacBridge.Audio;

namespace MandarinDacBridge.Native;

internal static unsafe partial class CoreAudio
{
    public static readonly uint StreamAvailableVirtualFormats = Code("sfma");

    // The physical formats a stream lists, as Core Audio gives them (to set one) and as the bridge reads them.
    public static (AudioStreamRangedDescription[] Raw, MacRawFormat[] Formats) PhysicalFormats(uint stream)
    {
        var raw = GetArray<AudioStreamRangedDescription>(stream, StreamAvailablePhysicalFormats, ScopeGlobal);
        return (raw, raw.Select(Of).ToArray());
    }

    private static MacRawFormat Of(AudioStreamRangedDescription f) => Of(f.Format, f.SampleRateRange.Minimum, f.SampleRateRange.Maximum);

    private static MacRawFormat Of(AudioStreamBasicDescription f, double min, double max) =>
        new(FourCc(f.FormatId), min, max, (int)f.BitsPerChannel, (f.FormatFlags & FlagIsFloat) != 0, (int)f.ChannelsPerFrame);

    public static string MacOs()
    {
        var v = Environment.OSVersion.Version;
        return v.Build > 0 ? $"macOS {v.Major}.{v.Minor}.{v.Build}" : $"macOS {v.Major}.{v.Minor}";
    }

    // Everything Core Audio says about a device's rates and its output streams' formats. Rate: what was asked
    // for (0 for --diagnose).
    public static CoreAudioProbe Probe(uint dev, int rate)
    {
        var streams = Read(dev, DeviceStreams, ScopeOutput, "output streams", sizeof(uint),
            b => MemoryMarshal.Read<uint>(b).ToString(System.Globalization.CultureInfo.InvariantCulture));
        CoreAudioRead[] device =
        [
            Read(dev, DeviceNominalSampleRate, ScopeGlobal, "nominal rate", sizeof(double),
                b => { var v = MemoryMarshal.Read<double>(b); return MacFormats.Range(v, v); }),
            Read(dev, DeviceAvailableNominalSampleRates, ScopeGlobal, "available nominal rates", sizeof(AudioValueRange),
                b => { var r = MemoryMarshal.Read<AudioValueRange>(b); return MacFormats.Range(r.Minimum, r.Maximum); }),
            streams,
        ];
        // The streams from that one read, so the report and the list of streams can't disagree.
        var ids = streams.Values.Select(v => uint.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : Unknown)
            .Where(id => id != Unknown);
        var each = ids.Select(s => new CoreAudioStreamProbe(s,
        [
            Read(s, StreamAvailablePhysicalFormats, ScopeGlobal, "available physical formats", sizeof(AudioStreamRangedDescription), Ranged),
            Read(s, StreamPhysicalFormat, ScopeGlobal, "physical format", sizeof(AudioStreamBasicDescription), Basic),
            Read(s, StreamAvailableVirtualFormats, ScopeGlobal, "available virtual formats", sizeof(AudioStreamRangedDescription), Ranged),
            Read(s, StreamVirtualFormat, ScopeGlobal, "virtual format", sizeof(AudioStreamBasicDescription), Basic),
        ])).ToArray();
        return new CoreAudioProbe(GetString(dev, ObjectName), MacOs(), rate, device, each);
    }

    private static string Ranged(ReadOnlySpan<byte> b) => MacFormats.Describe(Of(MemoryMarshal.Read<AudioStreamRangedDescription>(b)));

    private static string Basic(ReadOnlySpan<byte> b)
    {
        var f = MemoryMarshal.Read<AudioStreamBasicDescription>(b);
        return MacFormats.Describe(Of(f, f.SampleRate, f.SampleRate));
    }

    private delegate string Item(ReadOnlySpan<byte> bytes);

    // One property, each step's answer kept; its value cut into entries of a size, any bytes left over said.
    private static CoreAudioRead Read(uint obj, uint selector, uint scope, string what, int entry, Item item)
    {
        var a = new PropertyAddress(selector, scope);
        var name = MacFormats.Selector(selector);
        if (HasProperty(obj, in a) == 0) return new CoreAudioRead(name, what, false, 0, 0, 0, [], entry);
        int sizeStatus = GetPropertyDataSize(obj, in a, 0, null, out var size);
        if (sizeStatus != 0) return new CoreAudioRead(name, what, true, sizeStatus, 0, 0, [], entry);
        // An empty list: nothing to read (reading 0 bytes fails with '!dat', which isn't the answer; macOS 26
        // gives a size of 0 for 'pft?' on its own virtual devices).
        if (size == 0) return new CoreAudioRead(name, what, true, 0, 0, 0, [], entry);
        var data = new byte[size];
        int readStatus;
        fixed (byte* p = data) readStatus = GetPropertyData(obj, in a, 0, null, ref size, p);
        if (readStatus != 0) return new CoreAudioRead(name, what, true, 0, size, readStatus, [], entry);
        var values = new List<string>();
        int k = 0;
        for (; k + entry <= size; k += entry) values.Add(item(data.AsSpan(k, entry)));
        if (k < size) values.Add($"{size - k} bytes left over (an entry is {entry})");
        return new CoreAudioRead(name, what, true, 0, size, 0, values.ToArray(), entry);
    }
}
