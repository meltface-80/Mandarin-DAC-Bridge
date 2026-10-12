// Strm.cs — SlimProto's strm command, as the server sends it to a player.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace MandarinDacBridge.Slim;

// One strm packet: what to play, from where, in what form.
internal sealed record Strm(char Command, char Autostart, char Format, char SampleSize, char SampleRate, char Channels, char Endian,
    uint ReplayGain, int ServerPort, uint ServerIp, string Header)
{
    public static Strm? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24) return null;
        return new Strm((char)b[0], (char)b[1], (char)b[2], (char)b[3], (char)b[4], (char)b[5], (char)b[6],
            BinaryPrimitives.ReadUInt32BigEndian(b[14..]), BinaryPrimitives.ReadUInt16BigEndian(b[18..]),
            BinaryPrimitives.ReadUInt32BigEndian(b[20..]), Encoding.Latin1.GetString(b[24..]));
    }

    // "GET /stream.mp3?player=… HTTP/1.0" → "/stream.mp3?player=…"
    public string Path()
    {
        var m = Regex.Match(Header, @"^GET\s+(\S+)\s+HTTP", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "/stream.mp3";
    }

    // The MIME type the renderer is given, so it needn't ask the server (a stream is read once).
    public string Mime()
    {
        switch (Format)
        {
            case 'f': return "audio/flac";
            case 'm': return "audio/mpeg";
            case 'o': return "audio/ogg";
            case 'a': return "audio/aac";
            case 'l': return "audio/mp4";
            case 'p':
                int bits = SampleSize switch { '1' => 16, '2' => 24, '3' => 32, _ => 0 };
                int rate = SampleRate switch
                {
                    '0' => 11025, '1' => 22050, '2' => 32000, '3' => 44100, '4' => 48000, '5' => 8000, '6' => 12000,
                    '7' => 16000, '8' => 24000, '9' => 96000, _ => 0
                };
                int ch = Channels switch { '1' => 1, '2' => 2, _ => 0 };
                // Self-describing (a WAV or AIFF header follows): ffmpeg reads the header.
                if (bits == 0 || rate == 0 || ch == 0) return "audio/wav";
                return $"audio/L{bits};rate={rate};channels={ch}{(Endian == '1' ? ";endian=little" : "")}";
            default: return "";
        }
    }
}
