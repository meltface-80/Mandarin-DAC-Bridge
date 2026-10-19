// MacFormats.cs — choosing the physical format that sets a Mac's DAC to a track's rate, and what is said
// when there is none (dev/design/install.md, Part 2). Pure: Core Audio's answers come in as data, read by
// Native/CoreAudioProbe.cs, so all of it is tested on any system.
//
// A FiiO K5 Pro on macOS 27 listed no physical formats at all: the bridge said "the DAC doesn't take 44.1 kHz"
// for a DAC its own panel showed taking it (the panel's rates were the device's nominal rates). Now the
// refusal says which it was (no stream, no formats listed, or a rate the DAC doesn't list), and the probe of
// what Core Audio said goes to the log and to --diagnose. A DAC is never played in a format it didn't list.
using System.Globalization;
using System.Text;

namespace MandarinDacBridge.Audio;

// One property read: whether it is there, the size call's result and size, the read's result, what was read.
// Entry: the size of one entry in bytes, to tell a list cut short (0: not a list).
internal sealed record CoreAudioRead(string Selector, string What, bool Has, int SizeStatus, uint Size, int ReadStatus, string[] Values, int Entry = 0);

internal sealed record CoreAudioStreamProbe(uint Id, CoreAudioRead[] Reads);

// What Core Audio said about a DAC: the device's own properties, then each output stream's.
internal sealed record CoreAudioProbe(string Device, string MacOs, int Rate, CoreAudioRead[] DeviceReads, CoreAudioStreamProbe[] Streams);

internal static class MacFormats
{
    private const string Details = "what Core Audio said is in the log, and mandarin-dac-bridge --diagnose prints it all";

    // The format to set for a rate: linear PCM that covers it, with the channels; integer over float, then the
    // widest, then the fewest channels. -1 when none does.
    public static int Pick(IReadOnlyList<MacRawFormat> formats, int rate, int channels)
    {
        int best = -1, bestScore = int.MinValue;
        for (int k = 0; k < formats.Count; k++)
        {
            var f = formats[k];
            if (f.Id != "lpcm" || !Covers(f, rate) || f.Channels < channels) continue;
            int score = (f.Float ? 0 : 1000) + f.Bits - f.Channels;
            if (score > bestScore) { bestScore = score; best = k; }
        }
        return best;
    }

    // Whether a format covers a rate (half a hertz either way: Core Audio's ranges are doubles). The panel's
    // rates (Devices.FromMac) and the sink's choice use this one test.
    public static bool Covers(MacRawFormat f, int rate) => rate + 0.5 >= f.Min && rate - 0.5 <= f.Max;

    public const string ListSelector = "pft?";

    // Why a rate can't be set, when Pick found nothing in the formats the sink read. Which refusal it is comes
    // from the probe taken just after: the stream, and each step of reading its list ('pft?').
    public static string Refusal(IReadOnlyList<MacRawFormat> formats, CoreAudioProbe probe, int channels)
    {
        var rate = probe.Rate;
        var pcm = formats.Where(f => f.Id == "lpcm").ToArray();
        var list = probe.Streams.Length > 0 ? probe.Streams[0].Reads.FirstOrDefault(r => r.Selector == ListSelector) : null;
        var cant = $" ({probe.MacOs}), so it can't be set to {Devices.KHz(rate)}";
        string why;
        if (probe.Streams.Length == 0) why = "Core Audio shows no output stream for this DAC";
        else if (pcm.Length > 0)
        {
            if (pcm.Any(f => Covers(f, rate))) why = $"the DAC takes {Devices.KHz(rate)} only with fewer than {channels} channels";
            else
            {
                var listed = Devices.StandardRates.Where(r => pcm.Any(f => Covers(f, r))).Select(Devices.KHz).ToArray();
                why = listed.Length > 0
                    ? $"the DAC doesn't take {Devices.KHz(rate)} (it lists {string.Join(", ", listed)})"
                    : $"the DAC doesn't take {Devices.KHz(rate)} (it lists {string.Join(", ", pcm.Select(f => Range(f.Min, f.Max) + " Hz").Distinct())})";
            }
        }
        else if (list == null || !list.Has) why = "Core Audio has no list of formats for this DAC" + cant;
        else if (list.SizeStatus != 0) why = $"Core Audio couldn't read this DAC's formats (asking their size: {Status(list.SizeStatus)})" + cant;
        else if (list.ReadStatus != 0) why = $"Core Audio couldn't read this DAC's formats (reading them: {Status(list.ReadStatus)})" + cant;
        else if (list.Entry > 0 && list.Size % list.Entry != 0) why = $"Core Audio's list of this DAC's formats is {list.Size} bytes, not whole entries of {list.Entry}" + cant;
        else if (formats.Count > 0) why = $"Core Audio lists no PCM formats for this DAC, only {string.Join(", ", formats.Select(f => f.Id).Distinct())}" + cant;
        else if (list.Values.Length > 0) why = "Core Audio's list of this DAC's formats changed between two reads; try again" + cant;
        else why = "Core Audio lists no formats for this DAC" + cant;
        return $"{why}; {Details}";
    }

    // A property's four letters as Core Audio spells them, spaces kept ('pft ', 'uid ').
    public static string Selector(uint code) => FourChars(code) ?? code.ToString(CultureInfo.InvariantCulture);

    // A code's four letters, or null when they aren't all printable.
    private static string? FourChars(uint code)
    {
        var chars = new[] { (char)(code >> 24), (char)((code >> 16) & 0xFF), (char)((code >> 8) & 0xFF), (char)(code & 0xFF) };
        return chars.All(c => c is >= ' ' and <= '~') ? new string(chars) : null;
    }

    // "lpcm 24-bit integer 2 ch 44100-768000": a format as the probe and --diagnose show it.
    public static string Describe(MacRawFormat f) =>
        $"{f.Id} {f.Bits}-bit {(f.Float ? "float" : "integer")} {f.Channels} ch {Range(f.Min, f.Max)}";

    public static string Range(double min, double max) =>
        Math.Abs(min - max) < 0.5 ? Num(min) : $"{Num(min)}-{Num(max)}";

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // Everything the probe read, one line per read, for the log and --diagnose.
    public static string Report(CoreAudioProbe p)
    {
        var o = new StringBuilder();
        o.Append(CultureInfo.InvariantCulture, $"Core Audio on {p.Device} ({p.MacOs})");
        if (p.Rate > 0) o.Append(CultureInfo.InvariantCulture, $", asked for {p.Rate} Hz");
        o.Append(':');
        foreach (var r in p.DeviceReads) Line(o, "  ", r);
        if (p.Streams.Length == 0) o.Append("\n  no output stream");
        foreach (var s in p.Streams)
        {
            o.Append(CultureInfo.InvariantCulture, $"\n  stream {s.Id}:");
            foreach (var r in s.Reads) Line(o, "    ", r);
        }
        return o.ToString();
    }

    private static void Line(StringBuilder o, string indent, CoreAudioRead r)
    {
        o.Append(CultureInfo.InvariantCulture, $"\n{indent}'{r.Selector}' {r.What}: ");
        if (!r.Has) { o.Append("not there"); return; }
        if (r.SizeStatus != 0) { o.Append(CultureInfo.InvariantCulture, $"size failed: {Status(r.SizeStatus)}"); return; }
        o.Append(CultureInfo.InvariantCulture, $"size {r.Size} (0), ");
        if (r.ReadStatus != 0) { o.Append(CultureInfo.InvariantCulture, $"read failed: {Status(r.ReadStatus)}"); return; }
        o.Append(CultureInfo.InvariantCulture, $"{r.Values.Length} read");
        foreach (var v in r.Values) o.Append(CultureInfo.InvariantCulture, $"\n{indent}  {v}");
    }

    // An OSStatus: the number, and its four letters when it is one ('who?': no such property).
    public static string Status(int status)
    {
        var name = FourChars(unchecked((uint)status));
        return name != null
            ? string.Create(CultureInfo.InvariantCulture, $"{status} ('{name}')")
            : status.ToString(CultureInfo.InvariantCulture);
    }
}
