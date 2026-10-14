// DsdTests.cs — DSD files the bridge is sent that aren't what they claim (Dsd.cs). A file comes from
// whatever a controller points at: a broken or hostile one must be refused, never hang the feed.
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace MandarinDacBridge.Tests;

public class DsdMalformedTests
{
    private static byte[] Chunk(string id, byte[] body, ulong? size = null)
    {
        var h = new byte[12];
        Encoding.ASCII.GetBytes(id).CopyTo(h, 0);
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(4), size ?? (ulong)body.Length);
        return [.. h, .. body];
    }

    private static byte[] Dff(params byte[][] chunks)
    {
        byte[] body = [.. Encoding.ASCII.GetBytes("DSD "), .. chunks.SelectMany(c => c)];
        var file = new byte[12 + body.Length];
        Encoding.ASCII.GetBytes("FRM8").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(4), (ulong)body.Length);
        body.CopyTo(file, 12);
        return file;
    }

    // Parsed on another thread, so a parser that never returns fails the test instead of hanging it.
    private static DsdHeader? ParseWithin(byte[] file)
    {
        var parse = Task.Run(() => Dsd.Parse(file));
        Assert.True(parse.Wait(TimeSpan.FromSeconds(3)), "the parser didn't return");
        return parse.Result;
    }

    // A size that reads as -12 leaves the position where it was: the parser read the same bytes for ever.
    [Theory]
    [InlineData(0xFFFFFFFFFFFFFFF4UL)]
    public void AChunkWhoseSizeGoesBackwards_IsRefused(ulong size) =>
        Assert.Null(ParseWithin(Dff(Chunk("FVER", [1, 5, 0, 0], size), Chunk("DSD ", new byte[16]))));

    [Theory]
    [InlineData(0xFFFFFFFFFFFFFFF4UL)]
    public void APropertyWhoseSizeGoesBackwards_IsRefused(ulong size)
    {
        var fs = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(fs, 2822400);
        var prop = Chunk("PROP", [.. Encoding.ASCII.GetBytes("SND "), .. Chunk("FS  ", fs, size), .. Chunk("CHNL", [0, 2])]);
        Assert.Null(ParseWithin(Dff(prop, Chunk("DSD ", new byte[16]))));
    }

    // A DSF whose data is said to be smaller than its own header gave a negative length and duration.
    [Theory]
    [InlineData(84, 5UL)]
    public void ADsfWhoseSizesDontAddUp_IsRefused(int at, ulong value)
    {
        var file = DsdTests.Dsf();
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), value);
        Assert.Null(ParseWithin(file));
    }

    // A DSF's values held to what the format allows. A block size of 2^30 had the bridge hold everything it
    // read while waiting for one block group, until it ran out of memory (before 1.1.21).
    [Theory]
    [InlineData(72, 0x40000000u)]   // block size
    [InlineData(72, 2048u)]
    [InlineData(52, 0u)]            // channels
    [InlineData(52, 7u)]
    [InlineData(56, 1234u)]         // rate
    [InlineData(56, 44100u)]
    [InlineData(60, 4u)]            // bits per sample
    public void ADsfWithValuesDsdDoesntHave_IsRefused(int at, uint value)
    {
        var file = DsdTests.Dsf();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);
        Assert.Null(ParseWithin(file));
    }

    [Theory]
    [InlineData(2822400u)]   // DSD64
    [InlineData(5644800u)]   // DSD128
    [InlineData(3072000u)]   // DSD64, 48 kHz family
    [InlineData(45158400u)]  // DSD1024
    [InlineData(1411200u)]   // DSD32
    [InlineData(49152000u)]  // DSD1024, 48 kHz family
    public void ADsfAtAnyDsdRate_IsTaken(uint rate)
    {
        var file = DsdTests.Dsf(channels: 6);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(56), rate);
        Assert.Equal((int)rate, ParseWithin(file)?.Rate);
    }

    // A DFF too: its channel count and rate.
    [Theory]
    [InlineData(2822400u, 0)]
    [InlineData(1234u, 2)]
    public void ADffWithValuesDsdDoesntHave_IsRefused(uint rate, int channels)
    {
        var fs = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(fs, rate);
        var chnl = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(chnl, (ushort)channels);
        var prop = Chunk("PROP", [.. Encoding.ASCII.GetBytes("SND "), .. Chunk("FS  ", fs), .. Chunk("CHNL", chnl)]);
        Assert.Null(ParseWithin(Dff(prop, Chunk("DSD ", new byte[16]))));
    }

    // A DFF with an odd number of bytes per channel, and cover art after its samples bigger than the limit:
    // the last byte is packed, the packer is done, and nothing after the samples is held (or read).
    [Fact]
    public void ADffWithAnOddLastByte_EndsThere_AndWhatFollowsIsntHeld()
    {
        const int channels = 2, perCh = 1001;
        var fs = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(fs, 2822400);
        var chnl = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(chnl, channels);
        var prop = Chunk("PROP", [.. Encoding.ASCII.GetBytes("SND "), .. Chunk("FS  ", fs), .. Chunk("CHNL", chnl)]);
        var file = Dff(prop, Chunk("DSD ", new byte[perCh * channels]), Chunk("DIIN", new byte[3 << 20]));
        var h = ParseWithin(file)!;
        var packer = new DopPacker(h);
        int frames = 0;
        for (long at = h.DataStart; at < file.Length && !packer.Done; at += 1 << 16)
            frames += packer.Push(file.AsSpan((int)at, (int)Math.Min(1 << 16, file.Length - at))).Length / (4 * channels);
        Assert.True(packer.Done);
        Assert.Equal((perCh + 1) / 2, frames);
    }

    [Theory]
    [InlineData(2822400, "DSD64")]
    [InlineData(3072000, "DSD64 (48 kHz)")]
    [InlineData(49152000, "DSD1024 (48 kHz)")]
    public void DsdRatesAreNamedByTheirFamily(int rate, string name) => Assert.Equal(name, Dsd.Name(rate));

    // Whatever the header says, no more than a mebibyte waits to be packed: past it, the file is refused.
    [Fact]
    public void SamplesThatNeverMakeAWholeBlock_AreRefused_NotHeld()
    {
        var packer = new DopPacker(new DsdHeader("dsf", 2822400, 2, 92, long.MaxValue / 2, 1 << 30, false, long.MaxValue / 4, 1e9));
        var chunk = new byte[1 << 16];
        Assert.Throws<InvalidDataException>(() => { for (int i = 0; i < 64; i++) packer.Push(chunk); });
    }

    // A server that sends a DSD file's start and then nothing more: stopping (a seek, a new track) must not
    // wait on it. Before 1.1.13 its reads couldn't be cancelled, and every stop waited five seconds.
    [Fact]
    public async Task ADsdServerThatStalls_DoesNotHoldAStopUp()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var held = new List<System.Net.Sockets.TcpClient>();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; }
                lock (held) held.Add(c);
                var n = c.GetStream();
                var buf = new byte[4096];
                _ = await n.ReadAsync(buf);
                await n.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/x-dsf\r\nContent-Length: 100000\r\n\r\nDSD "));
                // and then nothing: the connection stays open
            }
        });
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var open = Task.Run(() => DsdSource.Open($"http://127.0.0.1:{port}/a.dsf", 0, [], _ => { }, cancel.Token));
            Assert.True(await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(4))) == open, "opening didn't give up when cancelled");
            Assert.True(open.IsCanceled || open.Exception?.InnerException is OperationCanceledException, $"ended with {open.Status}");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"took {clock.Elapsed}");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            lock (held) foreach (var c in held) c.Dispose();
        }
    }

    // The header comes whole, then the server stalls on the request for the samples (from the seek point,
    // which a server that ignores ranges makes the bridge read up to): stopping must not wait on that either.
    [Theory]
    [InlineData(false)]   // it ignores the range, and the bridge skips to the seek point itself
    public async Task ADsdServerThatStallsOnTheSamples_DoesNotHoldAStopUp(bool honoursRange)
    {
        var file = DsdTests.Dsf(bytesPerChannel: 4096 * 4);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var held = new List<System.Net.Sockets.TcpClient>();
        int requests = 0;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; }
                lock (held) held.Add(c);
                var n = c.GetStream();
                var buf = new byte[4096];
                _ = await n.ReadAsync(buf);
                if (Interlocked.Increment(ref requests) == 1)
                {
                    await n.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nContent-Type: audio/x-dsf\r\nContent-Length: {file.Length}\r\nContent-Range: bytes 0-{file.Length - 1}/{file.Length}\r\nConnection: close\r\n\r\n"));
                    await n.WriteAsync(file);
                    c.Dispose();   // so the samples are asked for on a new connection, which this loop answers
                    continue;
                }
                var status = honoursRange ? "206 Partial Content" : "200 OK";
                await n.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: audio/x-dsf\r\nContent-Length: 5000000\r\n\r\n"));
                await n.WriteAsync(file.AsMemory(0, 64));
                // and then nothing: the connection stays open
            }
        });
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // From one second in, so that a server ignoring the range has the bridge skip to there.
            var open = Task.Run(() =>
            {
                var s = DsdSource.Open($"http://127.0.0.1:{port}/a.dsf", 1.0, [], _ => { }, cancel.Token);
                // Opened before the stall began (the server honoured the range): its first read stalls instead.
                if (s != null) { using (s) _ = s.Read(new byte[1 << 16], cancel.Token); }
                return s;
            });
            Assert.True(await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(4))) == open, "it didn't give up when cancelled");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"took {clock.Elapsed}");
            Assert.True(Volatile.Read(ref requests) >= 2, "the samples were never asked for");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            lock (held) foreach (var c in held) c.Dispose();
        }
    }
}
