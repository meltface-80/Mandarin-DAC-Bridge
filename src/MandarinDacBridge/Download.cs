// Download.cs — a program the owner asked for, fetched from its publisher (Spotify Soloist, Caldera
// Headless, Roon Bridge: the page's Download).
//
// Sources.Http has no overall timeout: a track streams for as long as it plays. A download through it that
// stalled (a publisher that answers and then sends nothing, a connection lost without a reset) waited for
// ever, and the page's Download did nothing more until the bridge was restarted. Here a download gives up
// when nothing has come for a minute, with a TimeoutException that says so; one that keeps coming, however
// slowly, isn't cut short. (What is done with it afterwards, unpacking and checking, is bounded by the
// fetcher.)
//
// A program fetched runs as the bridge (as root, in Docker), and its publisher offers no checksum to hold
// it to. So each one is saved whole to a file first (ToFile), logged with where it came from, its size
// and its SHA-256, and only then unpacked: what was run, or refused, can be compared later with what the
// publisher, or another install, got. Nothing a publisher makes is 1 GiB; a body that goes on past that
// is refused, so one that never ends can't hold the Download either.
using System.Security.Cryptography;

namespace MandarinDacBridge;

internal static class Download
{
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(1);
    public const long MaxBytes = 1L << 30;

    // The body of url saved to path, whole or not at all (a part is deleted), and noted.
    public static async Task ToFile(string url, string path, CancellationToken ct, TimeSpan? idle = null, Action<string>? note = null, long max = MaxBytes)
    {
        try
        {
            await using var body = await Open(url, ct, idle, note, max);
            await using var file = File.Create(path);
            await body.CopyToAsync(file, ct);
        }
        catch (Exception)
        {
            try { File.Delete(path); } catch (Exception) { /* none made, or held: the next download overwrites it */ }
            throw;
        }
    }

    // The body of url, read as it comes. Its answer, and then each read, must come within idle. Read to its
    // end, it is noted (the log, unless given another) with its size and SHA-256.
    public static async Task<Stream> Open(string url, CancellationToken ct, TimeSpan? idle = null, Action<string>? note = null, long max = MaxBytes)
    {
        var limit = idle ?? IdleFor;
        var host = new Uri(url).Host;
        HttpResponseMessage res;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(limit);
            try { res = await Sources.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, wait.Token); }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                // Our limit, or the client's own: it couldn't connect within ten seconds.
                throw wait.IsCancellationRequested
                    ? new TimeoutException($"{host} didn't answer within {Seconds(limit)} seconds: try again later", e)
                    : new TimeoutException($"couldn't reach {host}: check this machine's connection", e);
            }
        }
        try
        {
            res.EnsureSuccessStatusCode();
            // Named by where it came from in the end (a publisher's download link redirects); noted with its path
            // too, without its query (a signed link's is a credential).
            var at = res.RequestMessage?.RequestUri;
            return new Watched(res, await res.Content.ReadAsStreamAsync(ct), at?.Host ?? host, at != null ? at.Host + at.AbsolutePath : host,
                               limit, note ?? Log.Write, max);
        }
        catch (Exception)
        {
            res.Dispose();
            throw;
        }
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    // A response's body whose every read has to bring something within the limit, hashed as it is read.
    private sealed class Watched(HttpResponseMessage res, Stream body, string host, string file, TimeSpan idle, Action<string> note, long max) : Stream
    {
        private readonly IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long read;
        private bool noted;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(idle);
            int n;
            try { n = await body.ReadAsync(buffer, wait.Token); }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{host} sent nothing for {Seconds(idle)} seconds: try again later", e);
            }
            if (n > 0)
            {
                sha.AppendData(buffer.Span[..n]);
                read += n;
                if (read > max) throw new InvalidDataException($"{host} sent more than {max} bytes: that isn't the program");
            }
            else if (buffer.Length > 0 && !noted)
            {
                noted = true;
                var line = $"downloaded {file}: {read} bytes, SHA-256 {Convert.ToHexStringLower(sha.GetHashAndReset())}";
                try { note(line); }
                catch (Exception) { /* the log failing isn't the download failing: what came is whole */ }
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { body.Dispose(); res.Dispose(); sha.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
