// Dsd.cs — DSD files (DSF and DSDIFF/DFF) sent to the DAC as DoP.
//
// DoP ("DSD over PCM", v1.1) carries DSD through a PCM path untouched: each
// 24-bit PCM sample holds 16 DSD bits of one channel under a marker byte
// (0x05 and 0xFA, taking turns frame by frame) that tells the DAC it is DSD.
// The PCM rate is the DSD rate / 16: DSD64 → 176.4 kHz, DSD128 → 352.8 kHz,
// DSD256 → 705.6 kHz. The bridge's PCM path is bit-perfect, so the DAC gets
// the DSD exactly.
//
// In our 32-bit samples a DoP word is  marker << 24 | first byte << 16 |
// second byte << 8,  the first byte being the earlier in time, most
// significant bit first.
using System.Buffers.Binary;
using System.Text;

namespace MandarinDacBridge;

internal sealed record DsdHeader(string Kind, int Rate, int Channels, long DataStart, long DataBytes,
    int BlockSize, bool LsbFirst, long BytesPerChannel, double Seconds);

internal static class Dsd
{
    public const byte Silence = 0x69;   // DSD's idle pattern
    private static readonly byte[] Markers = [0x05, 0xFA];

    public static readonly byte[] Reverse = BuildReverse();

    private static byte[] BuildReverse()
    {
        var r = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = 0;
            for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) v |= 0x80 >> b;
            r[i] = (byte)v;
        }
        return r;
    }

    public static string Name(int rate) => "DSD" + (int)Math.Round(rate / 44100.0);

    // The start of a DSD file → its header, or null when it isn't one (or is
    // DST-compressed, which can't go as DoP).
    public static DsdHeader? Parse(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 16) return null;
        var tag = Encoding.Latin1.GetString(buf[..4]);
        try
        {
            return tag switch { "DSD " => ParseDsf(buf), "FRM8" => ParseDff(buf), _ => null };
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string Tag(ReadOnlySpan<byte> b, long at) => Encoding.Latin1.GetString(b.Slice((int)at, 4));

    private static DsdHeader? ParseDsf(ReadOnlySpan<byte> b)
    {
        // 'DSD ' chunk (28 bytes), then 'fmt ' (52), then 'data' (12 + samples).
        long at = (long)BinaryPrimitives.ReadUInt64LittleEndian(b[4..]);
        if (b.Length < at + 52 || Tag(b, at) != "fmt ") return null;
        var f = b[(int)at..];
        long fmtSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[4..]);
        int channels = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[24..]);
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[28..]);
        int bits = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[32..]);
        long samples = (long)BinaryPrimitives.ReadUInt64LittleEndian(f[36..]);
        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[44..]);
        at += fmtSize;
        if (b.Length < at + 12 || Tag(b, at) != "data") return null;
        long dataSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(b[(int)(at + 4)..]) - 12;
        if (channels <= 0 || rate <= 0 || blockSize <= 0) return null;
        return new DsdHeader("dsf", rate, channels, at + 12, dataSize, blockSize, bits == 1,
            (samples + 7) / 8, (double)samples / rate);
    }

    private static DsdHeader? ParseDff(ReadOnlySpan<byte> b)
    {
        // FRM8 <size> 'DSD ' then chunks; PROP holds SND with FS, CHNL and CMPR.
        if (Tag(b, 12) != "DSD ") return null;
        long at = 16;
        int rate = 0, channels = 0;
        bool compressed = false;
        while (at + 12 <= b.Length)
        {
            var id = Tag(b, at);
            long size = (long)BinaryPrimitives.ReadUInt64BigEndian(b[(int)(at + 4)..]);
            if (id == "PROP")
            {
                long p = at + 16;   // after 'SND '
                long end = Math.Min(at + 12 + size, b.Length);
                while (p + 12 <= end)
                {
                    var sid = Tag(b, p);
                    long ssize = (long)BinaryPrimitives.ReadUInt64BigEndian(b[(int)(p + 4)..]);
                    if (sid == "FS  " && p + 16 <= end) rate = (int)BinaryPrimitives.ReadUInt32BigEndian(b[(int)(p + 12)..]);
                    if (sid == "CHNL" && p + 14 <= end) channels = BinaryPrimitives.ReadUInt16BigEndian(b[(int)(p + 12)..]);
                    if (sid == "CMPR" && p + 16 <= end) compressed = Tag(b, p + 12) != "DSD ";
                    p += 12 + ssize + (ssize & 1);
                }
            }
            else if (id == "DSD ")
            {
                if (rate <= 0 || channels <= 0 || compressed) return null;
                return new DsdHeader("dff", rate, channels, at + 12, size, 0, false, size / channels, (double)(size / channels) * 8 / rate);
            }
            else if (id == "DST ") return null;
            at += 12 + size + (size & 1);
        }
        return null;
    }

    // Where to start reading the samples for a seek: a byte offset from
    // DataStart on a boundary the packer can start at.
    public static (long Offset, long ConsumedPerChannel) SeekOffset(DsdHeader h, double seconds)
    {
        long perChannel = (long)Math.Floor(Math.Max(0, seconds) * h.Rate / 8);
        if (h.Kind == "dsf")
        {
            long block = perChannel / h.BlockSize;
            return (block * h.BlockSize * h.Channels, block * h.BlockSize);
        }
        long even = perChannel - perChannel % 2;
        return (even * h.Channels, even);
    }

    // A muted DoP buffer, in place: the markers kept (so the DAC stays in DSD), the DSD idle.
    public static void MuteDop(Span<byte> buf)
    {
        for (int o = 0; o + 4 <= buf.Length; o += 4)
        {
            buf[o] = 0;
            buf[o + 1] = Silence;
            buf[o + 2] = Silence;
        }
    }

    internal static byte Marker(int i) => Markers[i];
}

// The sample bytes (from ConsumedPerChannel on) → DoP frames as signed 32-bit
// little-endian PCM, channel-interleaved. Stops at the end of the real
// samples (a DSF file's last block is padded) and ends with 50 ms of DoP
// silence so the DAC doesn't click on the way out.
internal sealed class DopPacker(DsdHeader h, long consumedPerChannel = 0)
{
    private byte[] pending = [];
    private int pendingLength;
    private long left = Math.Max(0, h.BytesPerChannel - consumedPerChannel);
    private int marker;
    private bool finished;

    public byte[] Push(ReadOnlySpan<byte> data)
    {
        Append(data);
        return h.Kind == "dsf" ? Dsf(false) : Dff(false);
    }

    public byte[] Finish()
    {
        if (finished) return [];
        finished = true;
        var tail = h.Kind == "dsf" ? Dsf(true) : Dff(true);
        var quiet = SilenceFrames((int)Math.Round(h.Rate / 16 * 0.05));
        var all = new byte[tail.Length + quiet.Length];
        tail.CopyTo(all, 0);
        quiet.CopyTo(all, tail.Length);
        return all;
    }

    public bool Done => left <= 0;

    private void Append(ReadOnlySpan<byte> data)
    {
        if (pendingLength + data.Length > pending.Length)
        {
            var n = new byte[Math.Max(pending.Length * 2, pendingLength + data.Length)];
            pending.AsSpan(0, pendingLength).CopyTo(n);
            pending = n;
        }
        data.CopyTo(pending.AsSpan(pendingLength));
        pendingLength += data.Length;
    }

    private void Consume(int n)
    {
        pending.AsSpan(n, pendingLength - n).CopyTo(pending);
        pendingLength -= n;
    }

    // DSF: blocks of BlockSize bytes per channel, one channel after another.
    private byte[] Dsf(bool final)
    {
        int ch = h.Channels, bs = h.BlockSize, group = bs * ch;
        int groups = pendingLength / group;
        var outList = new List<byte[]>();
        int g = 0;
        for (; g < groups && left > 0; g++)
        {
            int baseAt = g * group;
            int use = (int)Math.Min(bs, left);
            outList.Add(Pack(use, (c, i) => Bits(pending[baseAt + c * bs + i])));
            left -= use;
        }
        Consume(left > 0 ? groups * group : pendingLength);
        if (final && pendingLength > 0 && left > 0)
        {
            int per = (int)Math.Min(Math.Min(pendingLength / ch, left), bs);
            var p = pending;
            int have = pendingLength;
            outList.Add(Pack(per, (c, i) => c * bs + i < have ? Bits(p[c * bs + i]) : Dsd.Silence));
            left = 0;
            pendingLength = 0;
        }
        return Join(outList);
    }

    // DFF: one byte per channel at a time, MSB first.
    private byte[] Dff(bool final)
    {
        int ch = h.Channels;
        long perChannel = Math.Min(pendingLength / ch, left);
        if (!final) perChannel -= perChannel % 2;
        if (perChannel <= 0)
        {
            if (final || left <= 0) pendingLength = 0;
            return [];
        }
        var p = pending;
        var o = Pack((int)perChannel, (c, i) => p[i * ch + c]);
        left -= perChannel;
        if (final || left <= 0) pendingLength = 0;
        else Consume((int)perChannel * ch);
        return o;
    }

    private byte Bits(byte v) => h.LsbFirst ? Dsd.Reverse[v] : v;

    // `bytes` DSD bytes per channel, read by at(channel, index), → DoP frames.
    private byte[] Pack(int bytes, Func<int, int, byte> at)
    {
        int ch = h.Channels;
        int frames = (bytes + 1) / 2;
        var o = new byte[frames * ch * 4];
        int w = 0;
        for (int f = 0; f < frames; f++)
        {
            int i = f * 2;
            byte m = Dsd.Marker(marker);
            for (int c = 0; c < ch; c++)
            {
                o[w] = 0;
                o[w + 1] = i + 1 < bytes ? at(c, i + 1) : Dsd.Silence;
                o[w + 2] = at(c, i);
                o[w + 3] = m;
                w += 4;
            }
            marker ^= 1;
        }
        return o;
    }

    private byte[] SilenceFrames(int frames)
    {
        var o = new byte[frames * h.Channels * 4];
        int w = 0;
        for (int f = 0; f < frames; f++)
        {
            byte m = Dsd.Marker(marker);
            for (int c = 0; c < h.Channels; c++) { o[w + 1] = Dsd.Silence; o[w + 2] = Dsd.Silence; o[w + 3] = m; w += 4; }
            marker ^= 1;
        }
        return o;
    }

    private static byte[] Join(List<byte[]> parts)
    {
        if (parts.Count == 0) return [];
        if (parts.Count == 1) return parts[0];
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }
}
