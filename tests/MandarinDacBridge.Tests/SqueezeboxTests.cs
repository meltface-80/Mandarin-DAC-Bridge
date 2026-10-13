// SqueezeboxTests.cs — the bridge as a Squeezebox player, end to end, with a pretend Lyrion Music Server.
// Part of EndToEndTests (same bridge, same helpers) until those move to a shared fixture (dev/quality.md).
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace MandarinDacBridge.Tests;

public sealed partial class EndToEndTests
{
    // ------------------------------------------------------------ Squeezebox (a pretend Lyrion Music Server)

    // server → player: length (2) · opcode · payload
    private static async Task ToPlayer(NetworkStream s, string op, byte[] payload)
    {
        var b = new byte[2 + 4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)(4 + payload.Length));
        Encoding.ASCII.GetBytes(op, b.AsSpan(2));
        payload.CopyTo(b, 6);
        await s.WriteAsync(b);
    }

    private static byte[] Strm(char cmd, string path = "", int httpPort = 0, char format = 'f', char autostart = '1')
    {
        var head = Encoding.ASCII.GetBytes(path == "" ? "" : $"GET {path} HTTP/1.0\r\n\r\n");
        var b = new byte[24 + head.Length];
        b[0] = (byte)cmd; b[1] = (byte)autostart; b[2] = (byte)format;
        b[3] = b[4] = b[5] = (byte)'?'; b[6] = (byte)'1';
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), (ushort)httpPort);
        head.CopyTo(b, 24);
        return b;
    }

    // What the player sends: opcode · length (4) · payload, read in the background.
    private static async Task ReadPlayer(NetworkStream s, System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)> got, CancellationToken ct)
    {
        var head = new byte[8];
        try
        {
            for (;;)
            {
                await s.ReadExactlyAsync(head, ct);
                var data = new byte[BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4))];
                await s.ReadExactlyAsync(data, ct);
                got.Enqueue((Encoding.ASCII.GetString(head, 0, 4), data));
            }
        }
        catch (Exception) { /* closed */ }
    }

    private static List<string> Events(System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)> got) =>
        got.Where(x => x.Op == "STAT").Select(x => Encoding.ASCII.GetString(x.Data, 0, 4)).ToList();

    [Fact]
    public async Task Squeezebox_PlaysFromTheServer_Gapless_KeepsOthersOut()
    {
        if (!HasFfmpeg()) return;
        var id = await Start(Dev("clock"));
        var lms = new TcpListener(IPAddress.Loopback, 0);
        lms.Start();
        try
        {
            int lmsPort = ((IPEndPoint)lms.LocalEndpoint).Port;
            var sv = await Post("/api/services", $"{{\"squeezelite\":true,\"lmsServer\":\"127.0.0.1:{lmsPort}\"}}");
            Assert.True(sv.GetProperty("squeezelite").GetBoolean());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var player = await lms.AcceptTcpClientAsync(cts.Token);
            var s = player.GetStream();
            var got = new System.Collections.Concurrent.ConcurrentQueue<(string Op, byte[] Data)>();
            _ = ReadPlayer(s, got, cts.Token);

            // HELO: a squeezelite-like player that takes FLAC, at the DAC's highest rate.
            await Until(() => Task.FromResult(got.Any(x => x.Op == "HELO")));
            var helo = got.First(x => x.Op == "HELO").Data;
            var caps = Encoding.ASCII.GetString(helo, 36, helo.Length - 36);
            Assert.Contains("Model=squeezelite", caps);
            Assert.Contains("MaxSampleRate=192000", caps);
            Assert.Contains(",flc", caps);
            Assert.Equal(0x02, helo[2] & 0x03);    // a locally administered MAC

            // The server asks its name.
            await ToPlayer(s, "setd", [0]);
            await Until(() => Task.FromResult(got.Any(x => x.Op == "SETD")));
            var setd = got.First(x => x.Op == "SETD").Data;
            Assert.Equal("Test DAC (Bridge)", Encoding.UTF8.GetString(setd, 1, setd.Length - 2));

            // Play a 2 s track; when it has been read, the next (same rate: gapless).
            var httpPort = new Uri(files.Base).Port;
            await ToPlayer(s, "strm", Strm('s', "/c44.flac", httpPort));
            await Until(() => Task.FromResult(Events(got).Contains("STMs")));
            Assert.Equal("STMc", Events(got)[0]);
            var dac = await Dac();
            Assert.Equal("PLAYING", dac.GetProperty("player").GetProperty("transport").GetString());
            Assert.Equal("Squeezebox server", dac.GetProperty("control").GetProperty("owner").GetString());
            Assert.Equal("44.1 kHz · 16-bit · FLAC", dac.GetProperty("player").GetProperty("format").GetString());
            Assert.StartsWith("connected to", dac.GetProperty("squeezebox").GetString());

            // Audirvana is kept out while the server plays.
            var r = await Av(id, "SetAVTransportURI", new() { ["CurrentURI"] = Url("a96.flac"), ["CurrentURIMetaData"] = "" }, Aud);
            Assert.Contains("<errorCode>705</errorCode>", r.Text);

            await Until(() => Task.FromResult(Events(got).Contains("STMd")), 6000);
            await ToPlayer(s, "strm", Strm('s', "/c44.flac", httpPort));
            await Until(() => Task.FromResult(Events(got).Count(e => e == "STMs") == 2), 6000);
            Assert.Equal("PLAYING", await State(id));
            await Until(() => Task.FromResult(Events(got).Contains("STMu")), 8000);
            var ev = Events(got).Where(e => e != "STMt").ToList();
            Assert.Equal(["STMc", "STMs", "STMd", "STMc", "STMs", "STMd", "STMu"], ev);
            Assert.Equal("STOPPED", await State(id));

            // Status requests are answered with the server's timestamp; stop is answered with flushed.
            var t = Strm('t');
            BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(14), 0xC0FFEE);
            await ToPlayer(s, "strm", t);
            await Until(() => Task.FromResult(got.Any(x => x.Op == "STAT" && BinaryPrimitives.ReadUInt32BigEndian(x.Data.AsSpan(47)) == 0xC0FFEE)));
            await ToPlayer(s, "strm", Strm('s', "/a96.flac", httpPort));
            await Until(async () => await State(id) == "PLAYING");
            await ToPlayer(s, "strm", Strm('q'));
            await Until(() => Task.FromResult(Events(got).Contains("STMf")));
            await Until(async () => await State(id) == "STOPPED");
        }
        finally { lms.Stop(); }
    }
}
