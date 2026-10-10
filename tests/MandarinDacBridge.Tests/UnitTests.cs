using System.Buffers.Binary;
using System.Net;
using System.Text;
using MandarinDacBridge.Upnp;
using Xunit;

namespace MandarinDacBridge.Tests;

public class DsdTests
{
    internal static byte[] Dsf(int channels = 2, int bytesPerChannel = 4096 * 2, Func<int, int, byte>? fill = null, byte pad = 0)
    {
        fill ??= (ch, i) => (byte)((ch * 16 + i) & 255);
        const int block = 4096;
        int blocks = (bytesPerChannel + block - 1) / block;
        var data = new byte[blocks * block * channels];
        for (int b = 0; b < blocks; b++)
            for (int ch = 0; ch < channels; ch++)
                for (int i = 0; i < block; i++)
                {
                    int n = b * block + i;
                    data[b * block * channels + ch * block + i] = n < bytesPerChannel ? fill(ch, n) : pad;
                }
        var head = new byte[92];
        Encoding.ASCII.GetBytes("DSD ").CopyTo(head, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(12), (ulong)(92 + data.Length));
        Encoding.ASCII.GetBytes("fmt ").CopyTo(head, 28);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(32), 52);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(52), (uint)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(56), 2822400);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(60), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(64), (ulong)bytesPerChannel * 8);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(72), block);
        Encoding.ASCII.GetBytes("data").CopyTo(head, 80);
        BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(84), (ulong)(12 + data.Length));
        return [.. head, .. data];
    }

    [Fact]
    public void DsfHeader()
    {
        var h = Dsd.Parse(Dsf())!;
        Assert.Equal("dsf", h.Kind);
        Assert.Equal(2822400, h.Rate);
        Assert.Equal(2, h.Channels);
        Assert.Equal(92, h.DataStart);
        Assert.True(h.LsbFirst);
        Assert.Equal("DSD64", Dsd.Name(h.Rate));
    }

    [Fact]
    public void DsfToDop_MarkersAlternate_BytesInTimeOrder_BitsReversed()
    {
        var file = Dsf(bytesPerChannel: 6000);   // the second block is part padding
        var h = Dsd.Parse(file)!;
        var p = new DopPacker(h);
        var data = file.AsSpan((int)h.DataStart);
        var o = new List<byte>();
        o.AddRange(p.Push(data[..8]));
        o.AddRange(p.Push(data[8..5000]));
        o.AddRange(p.Push(data[5000..]));
        o.AddRange(p.Finish());
        var bytes = o.ToArray();
        int silence = (int)Math.Round(176400 * 0.05);
        Assert.Equal((3000 + silence) * 8, bytes.Length);
        for (int f = 0; f < 3000; f++)
            for (int ch = 0; ch < 2; ch++)
            {
                uint w = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((f * 2 + ch) * 4));
                Assert.Equal(f % 2 == 1 ? 0xFAu : 0x05u, w >> 24);
                Assert.Equal(Dsd.Reverse[(ch * 16 + f * 2) & 255], (byte)(w >> 16));
                Assert.Equal(Dsd.Reverse[(ch * 16 + f * 2 + 1) & 255], (byte)(w >> 8));
                Assert.Equal(0u, w & 255);
            }
        uint last = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4));
        Assert.Equal(0x6969u, (last >> 8) & 0xFFFF);
    }

    [Fact]
    public void DffHeaderAndDop()
    {
        const int channels = 2, perCh = 1000;
        var data = new byte[perCh * channels];
        for (int i = 0; i < perCh; i++) for (int ch = 0; ch < channels; ch++) data[i * channels + ch] = (byte)((i * 3 + ch) & 255);
        static byte[] Chunk(string id, byte[] body)
        {
            var h = new byte[12];
            Encoding.ASCII.GetBytes(id).CopyTo(h, 0);
            BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(4), (ulong)body.Length);
            return [.. h, .. body];
        }
        var fs = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(fs, 5644800);
        var chnl = new byte[10]; BinaryPrimitives.WriteUInt16BigEndian(chnl, 2); Encoding.ASCII.GetBytes("SLFTSRGT").CopyTo(chnl, 2);
        var prop = Chunk("PROP", [.. Encoding.ASCII.GetBytes("SND "), .. Chunk("FS  ", fs), .. Chunk("CHNL", chnl), .. Chunk("CMPR", Encoding.ASCII.GetBytes("DSD \u000enot compressed\0"))]);
        byte[] body = [.. Encoding.ASCII.GetBytes("DSD "), .. Chunk("FVER", [1, 5, 0, 0]), .. prop, .. Chunk("DSD ", data)];
        var file = new byte[12 + body.Length];
        Encoding.ASCII.GetBytes("FRM8").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(4), (ulong)body.Length);
        body.CopyTo(file, 12);
        var h = Dsd.Parse(file)!;
        Assert.Equal("dff", h.Kind);
        Assert.Equal(5644800, h.Rate);
        Assert.Equal("DSD128", Dsd.Name(h.Rate));
        var p = new DopPacker(h);
        byte[] o = [.. p.Push(file.AsSpan((int)h.DataStart)), .. p.Finish()];
        uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(o.AsSpan(4));   // frame 0, right channel
        Assert.Equal(0x05u, w0 >> 24);
        Assert.Equal((byte)1, (byte)(w0 >> 16));
        Assert.Equal((byte)4, (byte)(w0 >> 8));
    }

    [Fact]
    public void SeekLandsOnABlock()
    {
        var h = Dsd.Parse(Dsf(bytesPerChannel: 4096 * 100))!;
        Assert.Equal((8L * 4096 * 2, 8L * 4096), Dsd.SeekOffset(h, 0.1));   // 0.1 s = 35280 bytes per channel → block 8
    }

    [Fact]
    public void MutingDopKeepsTheMarkers()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x05123400);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0xFA567800);
        Dsd.MuteDop(b);
        Assert.Equal(0x05696900u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(0xFA696900u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
    }
}

public class DeviceTests
{
    [Fact]
    public void Linux_UsbDacStreamFile_WithNativeDsd()
    {
        var root = Directory.CreateTempSubdirectory("asound-").FullName;
        File.WriteAllText(Path.Combine(root, "cards"),
            " 0 [PCH            ]: HDA-Intel - HDA Intel PCH\n                      HDA Intel PCH at 0xf7f10000 irq 33\n" +
            " 1 [D90            ]: USB-Audio - Topping D90\n                      Topping Topping D90 at usb-0000:00:14.0-2, high speed\n");
        Directory.CreateDirectory(Path.Combine(root, "card0"));
        Directory.CreateDirectory(Path.Combine(root, "card1", "pcm0p", "sub0"));
        File.WriteAllText(Path.Combine(root, "card1", "usbid"), "152a:8750\n");
        File.WriteAllText(Path.Combine(root, "card1", "pcm0p", "sub0", "status"), "closed\n");
        File.WriteAllText(Path.Combine(root, "card1", "stream0"), """
            Topping Topping D90 at usb-0000:00:14.0-2, high speed : USB Audio

            Playback:
              Status: Running
                Interface = 1
                Altset = 1
                Packet Size = 216
                Momentary freq = 96000 Hz (0xc.0000)
              Interface 1
                Altset 1
                Format: S32_LE
                Channels: 2
                Endpoint: 0x01 (1 OUT) (ASYNC)
                Rates: 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000
                Data packet interval: 125 us
                Bits: 32
              Interface 1
                Altset 2
                Format: SPECIAL DSD_U32_BE
                Channels: 2
                Rates: 88200, 176400, 352800, 705600
                Bits: 32

            Capture:
              Interface 2
                Altset 1
                Format: S16_LE
                Channels: 2
                Rates: 8000
            """);
        var list = Devices.ListLinux(root);
        var d = Assert.Single(list);
        Assert.Equal("Topping D90", d.Name);
        Assert.Equal("Topping", d.Manufacturer);
        Assert.Equal("152a:8750", d.Usb);
        Assert.Equal("hw:CARD=D90,DEV=0", d.Spec);
        Assert.Equal([44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000], d.Rates);
        Assert.Equal([32], d.Bits);
        Assert.Equal([88200, 176400, 352800, 705600], d.DsdNative);
        Assert.Equal(96000, d.CurrentRate);
    }

    [Fact]
    public void Mac_UsbDacsOnly_RatesFromThePhysicalFormats()
    {
        var all = new[]
        {
            new MacRawDevice("BuiltInSpeakerDevice", "MacBook Pro Speakers", "Apple Inc.", "bltn", 2, 48000, -1, null, [(44100, 44100), (48000, 48000)], []),
            new MacRawDevice("AppleUSBAudioEngine:Chord:Mojo 2:1234:1", "Mojo 2", "Chord Electronics Ltd", "usb", 2, 44100, 812, 0.8,
                [(44100, 44100), (768000, 768000)],
                [new MacRawFormat("lpcm", 44100, 768000, 32, false, 2), new MacRawFormat("lpcm", 44100, 768000, 24, false, 2)])
        };
        var d = Assert.Single(Devices.FromMac(all));
        Assert.Equal("Mojo 2", d.Name);
        Assert.Equal("AppleUSBAudioEngine:Chord:Mojo 2:1234:1", d.Spec);
        Assert.Equal([44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000], d.Rates);
        Assert.Equal([24, 32], d.Bits);
        Assert.Equal(812, d.HolderPid);
        Assert.Equal(0.8, d.Volume);
        Assert.StartsWith("32-bit integer · 2 ch · 44.1 kHz, 48 kHz", d.Formats[0]);
        Assert.Equal(2, Devices.FromMac(all, all: true).Count);
        // A USB DAC whose driver reports no transport (or an odd one) still counts, by Apple's USB driver's UID;
        // and every output passed over says why.
        var odd = new[]
        {
            new MacRawDevice("AppleUSBAudioEngine:SMSL:SU-1:00112233:1", "SMSL USB AUDIO", "SMSL", "", 2, 44100, -1, null, [(44100, 768000)], []),
            new MacRawDevice("BuiltInSpeakerDevice", "MacBook Pro Speakers", "Apple Inc.", "bltn", 2, 48000, -1, null, [(48000, 48000)], [])
        };
        var skipped = new List<string>();
        Assert.Equal("SMSL USB AUDIO", Assert.Single(Devices.FromMac(odd, false, skipped)).Name);
        Assert.Equal("MacBook Pro Speakers — Built-in, not USB", Assert.Single(skipped));
        Assert.Equal("Audirvana", Devices.FriendlyProcess("/Applications/Audirvana Studio.app/Contents/MacOS/Audirvana Studio"));
        Assert.Equal("Music", Devices.FriendlyProcess("/System/Applications/Music.app/Contents/MacOS/Music"));
    }
}

public class SourceTests
{
    [Fact]
    public void TheRateSent_TheTracksOwn_ElseTheNearestInItsFamily()
    {
        int[] dac = [44100, 48000, 88200, 96000, 176400, 192000];
        Assert.Equal(96000, Sources.PickRate(96000, dac));
        Assert.Equal(176400, Sources.PickRate(352800, dac));
        Assert.Equal(192000, Sources.PickRate(384000, dac));
        Assert.Equal(44100, Sources.PickRate(22050, dac));
        Assert.Equal(48000, Sources.PickRate(96000, [44100, 48000]));
        Assert.Equal(96000, Sources.PickRate(88200, [48000, 96000]));
        Assert.Equal(12345, Sources.PickRate(12345, []));
        Assert.Equal(new RawPcm("s24be", 96000, 2, 24), Sources.RawPcmOf("audio/L24;rate=96000;channels=2"));
        Assert.Null(Sources.RawPcmOf("audio/flac"));
        Assert.True(Sources.IsDsd("audio/x-dsf", ""));
        Assert.True(Sources.IsDsd("", "http://x/a.dff?id=1"));
        Assert.False(Sources.IsDsd("audio/flac", "http://x/a.flac"));
    }

    [Fact]
    public void WhatFfmpegSaysAboutItsInput()
    {
        var p = Sources.ParseProbe("""
            Input #0, flac, from 'http://x/a.flac':
              Duration: 00:03:21.45, start: 0.000000, bitrate: 2944 kb/s
              Stream #0:0: Audio: flac, 96000 Hz, stereo, s32 (24 bit)
            Stream mapping:
              Stream #0:0 -> #0:0 (flac (native) -> pcm_s32le (native))
            Output #0, wav, to 'pipe:1':
              Stream #0:0: Audio: pcm_s32le, 96000 Hz, stereo, s32, 6144 kb/s
            """);
        Assert.Equal(("flac", 96000, 24, false, 201.45, 2), p);
        Assert.Equal("96 kHz · 24-bit · FLAC", Renderer.Describe(new TrackInfo(96000, 2, 24, "flac", 96000, false, 0, false)));
        Assert.Equal("DSD64 · DoP at 176.4 kHz", Renderer.Describe(new TrackInfo(176400, 2, 1, "DSD64", 2822400, false, 0, true)));
        Assert.Equal("352.8 kHz → 176.4 kHz · 24-bit · FLAC", Renderer.Describe(new TrackInfo(176400, 2, 24, "flac", 352800, false, 0, false, true)));
    }
}

public class SoapTests
{
    [Fact]
    public void SoapRequestsAndDidl()
    {
        var meta = "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
                   "<item id=\"1\" parentID=\"0\" restricted=\"1\"><dc:title>So What &amp; More</dc:title><upnp:artist>Miles Davis</upnp:artist><upnp:album>Kind of Blue</upnp:album>" +
                   "<res protocolInfo=\"http-get:*:audio/flac:*\" duration=\"0:09:22.000\">http://10.0.0.2:3500/stream/1.flac</res></item></DIDL-Lite>";
        var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
                   "<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\"><InstanceID>0</InstanceID>" +
                   $"<CurrentURI>http://10.0.0.2:3500/stream/1.flac?a=1&amp;b=2</CurrentURI><CurrentURIMetaData>{Xml.Esc(meta)}</CurrentURIMetaData></u:SetAVTransportURI></s:Body></s:Envelope>";
        var (action, args) = Xml.ParseSoap(body, "\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"");
        Assert.Equal("SetAVTransportURI", action);
        Assert.Equal("0", args["InstanceID"]);
        Assert.Equal("http://10.0.0.2:3500/stream/1.flac?a=1&b=2", args["CurrentURI"]);
        Assert.Equal(meta, args["CurrentURIMetaData"]);
        var d = Xml.ParseDidl(args["CurrentURIMetaData"], "http://10.0.0.2:3500/stream/1.flac");
        Assert.Equal("So What & More", d.Title);
        Assert.Equal("Miles Davis", d.Artist);
        Assert.Equal("audio/flac", d.Mime);
        Assert.Equal(562, d.Duration);
        Assert.Equal("Play", Xml.ParseSoap("<s:Envelope><s:Body><u:Play xmlns:u=\"x\"/></s:Body></s:Envelope>", "").Action);
        Assert.Equal("1:02:03", Xml.SecondsToHms(3723.9));
        Assert.Contains("<errorCode>705</errorCode>", Control.Fault(705, "Transport is locked"));
        Assert.Contains("<name>SetNextAVTransportURI</name>", Scpd.Document("AVTransport"));
    }

    [Fact]
    public void LastChangeDocuments()
    {
        var x = Events.LastChange("AVTransport", new() { ["TransportState"] = "PLAYING", ["CurrentTrackURI"] = "http://a/b?c=1&d=2" });
        Assert.Equal("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\"><InstanceID val=\"0\"><TransportState val=\"PLAYING\"/><CurrentTrackURI val=\"http://a/b?c=1&amp;d=2\"/></InstanceID></Event>", x);
        Assert.Contains("<Volume channel=\"Master\" val=\"100\"/>", Events.LastChange("RenderingControl", new() { ["Volume"] = "100" }));
        Assert.Contains("<LastChange>&lt;Event xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/AVT/&quot;&gt;", Events.PropertySet("AVTransport", new() { ["TransportState"] = "STOPPED" }));
        Assert.Contains("<e:property><CurrentConnectionIDs>0</CurrentConnectionIDs></e:property>", Events.PropertySet("ConnectionManager", new() { ["CurrentConnectionIDs"] = "0" }));
    }
}

public class ArbiterTests
{
    [Fact]
    public void TheFirstToPlayOwnsTheDac_OthersWaitOutTheGrace()
    {
        var a = new Arbiter(TimeSpan.FromSeconds(1), 3500);
        var aud = a.Identify("::ffff:10.0.0.5", "Audirvana Studio/2.1.0", null);
        var man = a.Identify("::ffff:10.0.0.9", "", "http://10.0.0.9:3500/stream/42.flac");
        Assert.Equal("Audirvana", aud.Name);
        Assert.Equal("Mandarin", man.Name);
        Assert.Equal("Mandarin", a.Identify("10.0.0.9", "", null).Name);   // remembered without a URI
        Assert.Null(a.Claim(aud, "Play", false));
        Assert.NotNull(a.Claim(man, "Play", true));                           // kept out while it plays
        Assert.NotNull(a.LastBlocked);
        Assert.NotNull(a.Claim(man, "Play", false));                          // and within the grace after
        a.Expire();
        Assert.Null(a.Claim(man, "SetAVTransportURI", false));                // after the grace it may
        Assert.Equal("Mandarin", a.Owner!.Name);
        a.Release();
        Assert.Null(a.Owner);
    }
}

public class SsdpTests
{
    [Fact]
    public async Task AnswersAnMSearchForARenderer_FromTheAskersNetwork()
    {
        var two = new List<LocalAddress>
        {
            new("en1", IPAddress.Parse("10.0.0.2"), IPAddress.Parse("255.255.255.0")),
            new("en0", IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"))
        };
        Assert.Equal(IPAddress.Parse("192.168.1.10"), Ssdp.AddressFor(IPAddress.Parse("192.168.1.40"), two));
        var sent = new List<string>();
        var s = new Ssdp(55500, "", "test", () => [new Advert("uuid:abc", "/upnp/dac-1/description.xml")]);
        s.UseInterfaces(two);
        s.SendHook = (b, _) => { lock (sent) sent.Add(Encoding.ASCII.GetString(b)); };
        var from = new IPEndPoint(IPAddress.Parse("192.168.1.40"), 50000);
        void Ask(string st) => s.OnMessage($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 0\r\nST: {st}\r\n\r\n", from);
        Ask("urn:schemas-upnp-org:device:MediaRenderer:1");
        Ask("urn:schemas-upnp-org:service:AVTransport:1");
        Ask("urn:schemas-upnp-org:device:MediaServer:1");
        await Task.Delay(100);
        Assert.Equal(2, sent.Count);
        Assert.Contains(sent, x => x.Contains("LOCATION: http://192.168.1.10:55500/upnp/dac-1/description.xml") && x.Contains("USN: uuid:abc::urn:schemas-upnp-org:device:MediaRenderer:1"));
        sent.Clear();
        Ask("ssdp:all");
        await Task.Delay(100);
        Assert.Equal(6, sent.Count);   // root, uuid, the device type and three services
    }
}

public class ConnectionTests
{
    [Fact]
    public void Strm_SaysWhatToFetch_AndHow()
    {
        var head = Encoding.ASCII.GetBytes("GET /stream.mp3?player=02:aa:bb:cc:dd:ee HTTP/1.0\r\n\r\n");
        var b = new byte[24 + head.Length];
        b[0] = (byte)'s'; b[1] = (byte)'1'; b[2] = (byte)'p'; b[3] = (byte)'1'; b[4] = (byte)'3'; b[5] = (byte)'2'; b[6] = (byte)'1';
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), 9000);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 0xC0A80105);
        head.CopyTo(b, 24);
        var s = Slim.Strm.Parse(b)!;
        Assert.Equal('s', s.Command);
        Assert.Equal(9000, s.ServerPort);
        Assert.Equal(0xC0A80105u, s.ServerIp);
        Assert.Equal("/stream.mp3?player=02:aa:bb:cc:dd:ee", s.Path());
        Assert.Equal("audio/L16;rate=44100;channels=2;endian=little", s.Mime());
        Assert.Equal(new RawPcm("s16le", 44100, 2, 16), Sources.RawPcmOf(s.Mime()));
        Assert.Equal(new RawPcm("s24be", 96000, 2, 24), Sources.RawPcmOf("audio/L24;rate=96000;channels=2"));
        Assert.Equal("audio/wav", (s with { SampleSize = '?' }).Mime());
        Assert.Equal("audio/flac", (s with { Format = 'f' }).Mime());
        Assert.Equal("", (s with { Format = 'd' }).Mime());
        Assert.Null(Slim.Strm.Parse(new byte[10]));
    }

    [Fact]
    public void Discovery_ReadsTheServersAnswer()
    {
        var b = new List<byte>();
        foreach (var (k, v) in new[] { ("NAME", "lms-box"), ("JSON", "9000") })
        {
            b.AddRange(Encoding.ASCII.GetBytes(k));
            b.Add((byte)v.Length);
            b.AddRange(Encoding.UTF8.GetBytes(v));
        }
        var t = Slim.SlimDiscovery.ParseTags(b.ToArray());
        Assert.Equal("lms-box", t["NAME"]);
        Assert.Equal("9000", t["JSON"]);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), Slim.SlimDiscovery.Parse("10.0.0.2")!.Ip);
        Assert.Equal(3500, Slim.SlimDiscovery.Parse("10.0.0.2:3500")!.Port);
        Assert.Null(Slim.SlimDiscovery.Parse(""));
    }

    [Fact]
    public void PlayerMac_IsStable_AndLocallyAdministered()
    {
        var a = Slim.SlimPlayer.MacFor("host", "usb:1");
        Assert.Equal(a, Slim.SlimPlayer.MacFor("host", "usb:1"));
        Assert.NotEqual(a, Slim.SlimPlayer.MacFor("host", "usb:2"));
        Assert.Equal(0x02, a[0] & 0x03);
    }

    [Fact]
    public void LmsStatus_GivesTitleArtistCover()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"result":{"mode":"play","time":12.5,"duration":"201.3","playlist_loop":[{"title":"Song","artist":"Artist","album":"Album","coverid":"abc123","duration":201.3}]}}
            """);
        var m = Slim.SlimPlayer.ParseStatus(doc.RootElement, "http://lms:9000", "02:aa:bb:cc:dd:ee")!;
        Assert.Equal("Song", m.Title);
        Assert.Equal("Artist", m.Artist);
        Assert.Equal("http://lms:9000/music/abc123/cover.jpg", m.Art);
        Assert.Equal(201.3, m.Duration, 3);
        Assert.Equal(12.5, m.Position);

        using var radio = System.Text.Json.JsonDocument.Parse("""{"result":{"remoteMeta":{"title":"Live","artwork_url":"/imageproxy/x/image.jpg"}}}""");
        Assert.Equal("http://lms:9000/imageproxy/x/image.jpg", Slim.SlimPlayer.ParseStatus(radio.RootElement, "http://lms:9000", "m")!.Art);
    }
}

public class SoloistTests
{
    [Fact]
    public void Entity_GivesTitleArtistsAlbumCover()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"uri":"spotify:track:1","entity_type":"track","decorations":{"identity":{"name":"Blue in Green"},
             "visual_identity":{"cover":[{"url":"https://i/s","size":"small"},{"url":"https://i/d","size":"default"}]},
             "parent":{"entity":{"decorations":{"identity":{"name":"Kind of Blue"}}}},
             "creators":[{"decorations":{"identity":{"name":"Miles Davis"}}},{"entity":{"decorations":{"identity":{"name":"Bill Evans"}}}}],
             "playback":{"duration_ms":337000}}}
            """);
        var m = Spotify.SoloistPlayer.Entity(doc.RootElement)!;
        Assert.Equal("Blue in Green", m.Title);
        Assert.Equal("Miles Davis, Bill Evans", m.Artist);
        Assert.Equal("Kind of Blue", m.Album);
        Assert.Equal("https://i/d", m.Art);     // large wanted; default is the best there
        Assert.Equal(337, m.Duration);
        using var empty = System.Text.Json.JsonDocument.Parse("""{"uri":"","entity_type":"unknown"}""");
        Assert.Null(Spotify.SoloistPlayer.Entity(empty.RootElement));
    }

    [Fact]
    public void Download_IsSpotifysOwnBuild_ForThisProcessor()
    {
        Assert.Equal("https://soloist-builds.spotifycdn.com/soloist_release_arm64.tar.gz", Spotify.SoloistDownload.Url("arm64"));
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            Assert.Equal("x86_64", Spotify.SoloistDownload.Arch);
    }
}

public class SoloistExpiryTests
{
    [Fact]
    public void BuildDate_PlusNinetyDays()
    {
        Assert.Equal(new DateOnly(2027, 1, 8), Spotify.SoloistDownload.Expires("soloist 1.3.9.7 build 1791612060 (20261010) (g440165f200) (linux/x86_64)"));
        Assert.Null(Spotify.SoloistDownload.Expires("soloist"));
    }

    [Fact]
    public void LoaderErrors_AreSaidPlainly()
    {
        Assert.Equal("Soloist needs libatomic.so.1: sudo apt install libatomic1",
            Spotify.SoloistDownload.Explain("/data/soloist/soloist: error while loading shared libraries: libatomic.so.1: cannot open shared object file: No such file or directory"));
        Assert.StartsWith("Soloist needs a newer Linux", Spotify.SoloistDownload.Explain("soloist: /lib/x86_64-linux-gnu/libc.so.6: version `GLIBC_2.38' not found"));
        Assert.Equal("soloist 1.3.9.7", Spotify.SoloistDownload.Explain("soloist 1.3.9.7"));
    }
}

public class CalderaTests
{
    [Fact]
    public void Timeline_GivesStateTimeTrackAndServer()
    {
        var t = Caldera.CalderaPlayer.Timeline("""
            <MediaContainer commandID="3"><Timeline type="video" state="stopped"/>
            <Timeline type="music" state="paused" time="12500" duration="200000" key="/library/metadata/7" ratingKey="7"
              address="192.168.1.9" port="32400" protocol="https" token="t0k"/></MediaContainer>
            """)!;
        Assert.Equal("paused", t.State);
        Assert.Equal(12.5, t.Time);
        Assert.Equal(200, t.Duration);
        Assert.Equal("/library/metadata/7", t.Key);
        Assert.Equal("https://192.168.1.9:32400", t.Server);
        Assert.Equal("t0k", t.Token);
        Assert.Null(Caldera.CalderaPlayer.Timeline("<MediaContainer/>"));
    }

    [Fact]
    public void PlexMetadata_GivesTrackFormatAndCover()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"MediaContainer":{"Metadata":[{"title":"T","grandparentTitle":"Album Artist","originalTitle":"Track Artist","parentTitle":"A",
             "duration":1000,"parentThumb":"/p/thumb","Media":[{"audioCodec":"alac","Part":[{"Stream":[{"streamType":1},{"streamType":2,"samplingRate":44100,"bitDepth":16}]}]}]}]}}
            """);
        var (m, f, thumb) = Caldera.CalderaPlayer.Metadata(doc.RootElement);
        Assert.Equal("Track Artist", m.Artist);
        Assert.Equal("A", m.Album);
        Assert.Equal("44.1 kHz · 16-bit · ALAC", f);
        Assert.Equal("/p/thumb", thumb);
    }
}

public class QobuzTests
{
    [Fact]
    public void Config_HasASpeakerPerDac_WithAStableUuid()
    {
        var list = new List<Qobuz.QobuzSpeaker>
        {
            new("dac-1", "usb:1", "D90 \"Pro\" (Bridge)", "http://127.0.0.1:55500/upnp/dac-1/description.xml", "127.0.0.1", 55500),
            new("dac-2", "usb:2", "RME (Bridge)", "http://127.0.0.1:55500/upnp/dac-2/description.xml", "127.0.0.1", 55500)
        };
        var yaml = Qobuz.QobuzConnect.Config(list);
        Assert.Contains("name: \"D90 \\\"Pro\\\" (Bridge)\"", yaml);
        Assert.Contains("proxy_port: 7121", yaml);
        Assert.Contains("http_port: 8691", yaml);
        Assert.Equal(Qobuz.QobuzConnect.Uuid("usb:1"), Qobuz.QobuzConnect.Uuid("usb:1"));
        Assert.NotEqual(Qobuz.QobuzConnect.Uuid("usb:1"), Qobuz.QobuzConnect.Uuid("usb:2"));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", Qobuz.QobuzConnect.Uuid("usb:1"));
    }

    [Fact]
    public void ProtocolInfo_SaysTheDacsBest_ForFlac()
    {
        var pi = Upnp.Control.ProtocolInfo([44100, 96000, 192000], [16, 24, 32]);
        Assert.StartsWith("http-get:*:audio/flac:sampleRate=192000;bitsPerSample=32,", pi);
        Assert.Contains("http-get:*:audio/L24;rate=192000;channels=2:*", pi);
    }
}

public class MacTests
{
    [Fact]
    public void WhatTheAppsSay_IsRead()
    {
        var m = Mac.MacPlayers.Parse("Music", "playing\nSo What\nMiles Davis\nKind of Blue\n562,4\n12.5\n4711");
        Assert.Equal(("playing", "So What", "Miles Davis", "Kind of Blue", 562.4, 12.5, "4711"), (m.State, m.Title, m.Artist, m.Album, m.Duration, m.Position, m.Id));
        // Spotify gives the duration in milliseconds, and no id here.
        var s = Mac.MacPlayers.Parse("Spotify", "paused\r\nTrack\r\nArtist\r\nAlbum\r\n200000\r\n3.0");
        Assert.Equal(("paused", 200.0, "Track|Artist|Album"), (s.State, s.Duration, s.Id));
        Assert.Equal("stopped", Mac.MacPlayers.Parse("Music", "stopped").State);
        Assert.Equal("stopped", Mac.MacPlayers.Parse("Music", "fast forwarding\nx").State);
    }

    [Fact]
    public void FloatsFromTheDevice_GiveThe24BitSamplesBack_Exactly()
    {
        foreach (int v in new[] { 0, 1, -1, 8388607, -8388608, 4711, -123456, 0x5A5A5A })
            Assert.Equal(v << 8, Mac.MacSource.ToInt((float)(v / 8388608.0)));
        Assert.Equal(8388607 << 8, Mac.MacSource.ToInt(1.5f));
        Assert.Equal(-8388608 << 8, Mac.MacSource.ToInt(-1.5f));
    }
}

public class VolumeTests
{
    [Fact]
    public void TheCurve_IsAlsamixers_AndGoesBothWays()
    {
        // A wide range (the DragonFly's kind): steps that sound alike, 0 at the bottom, 100 at the top.
        Assert.Equal(0, Audio.VolumeCurve.ToLevel(-127.5, -127.5, 0), 6);
        Assert.Equal(1, Audio.VolumeCurve.ToLevel(0, -127.5, 0), 6);
        foreach (var level in new[] { 0.0, 0.05, 0.25, 0.5, 0.75, 1 })
            Assert.Equal(level, Audio.VolumeCurve.ToLevel(Audio.VolumeCurve.ToDb(level, -127.5, 0), -127.5, 0), 6);
        Assert.InRange(Audio.VolumeCurve.ToDb(0.5, -127.5, 0), -18.5, -17.5);    // half way is about -18 dB
        // A small range (24 dB or less): linear in dB.
        Assert.Equal(-12, Audio.VolumeCurve.ToDb(0.5, -24, 0), 6);
        // "Muted" at the bottom (ALSA's -99999.99 dB): no floor, and 0 is silence.
        Assert.Equal(-99999.99, Audio.VolumeCurve.ToDb(0, -99999.99, 0), 6);
        Assert.InRange(Audio.VolumeCurve.ToDb(0.5, -99999.99, 0), -18.1, -17.9);
        // A Squeezebox server's gain in dB.
        Assert.Equal(100, Audio.VolumeCurve.LevelOfGainDb(0));
        Assert.Equal(24, Audio.VolumeCurve.LevelOfGainDb(-30));
        Assert.Equal(0, Audio.VolumeCurve.LevelOfGainDb(double.NegativeInfinity));
    }

    [Fact]
    public void Qobuz_SetsTheVolume_OnlyOfDacsThatHaveTheirOwn()
    {
        var yaml = Qobuz.QobuzConnect.Config([
            new("dac-1", "usb:1", "A (Bridge)", "http://127.0.0.1:55500/upnp/dac-1/description.xml", "127.0.0.1", 55500, FixedVolume: false),
            new("dac-2", "usb:2", "B (Bridge)", "http://127.0.0.1:55500/upnp/dac-2/description.xml", "127.0.0.1", 55500)
        ]);
        var a = yaml.IndexOf("A (Bridge)", StringComparison.Ordinal);
        var b = yaml.IndexOf("B (Bridge)", StringComparison.Ordinal);
        Assert.Contains("dlna_fixed_volume: false", yaml[a..b]);
        Assert.Contains("dlna_fixed_volume: true", yaml[b..]);
    }
}

public class AlsaVolumeTests
{
    [Fact]
    public void ACardThatIsntThere_HasNoVolume_AndNothingBreaks()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/share/alsa/alsa.conf")) return;
        Assert.Null(Audio.AlsaVolume.Open("hw:CARD=NoSuchDac,DEV=0"));
    }
}
