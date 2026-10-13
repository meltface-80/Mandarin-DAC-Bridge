// Download.cs — a program the owner asked for, fetched from its publisher (Spotify Soloist, Caldera
// Headless, Roon Bridge: the page's Download).
//
// Sources.Http has no overall timeout: a track streams for as long as it plays. A download through it that
// stalled (a publisher that answers and then sends nothing, a connection lost without a reset) waited for
// ever, and the page's Download did nothing more until the bridge was restarted. Here a download gives up
// when nothing has come for a minute, with a TimeoutException that says so; one that keeps coming, however
// slowly, isn't cut short. (What is done with it afterwards, unpacking and checking, is bounded by the
// fetcher.)
namespace MandarinDacBridge;

internal static class Download
{
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(1);

    // The body of url, read as it comes. Its answer, and then each read, must come within idle.
    public static async Task<Stream> Open(string url, CancellationToken ct, TimeSpan? idle = null)
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
            // Named by where it came from in the end (a publisher's download link redirects).
            var from = res.RequestMessage?.RequestUri?.Host ?? host;
            return new Watched(res, await res.Content.ReadAsStreamAsync(ct), from, limit);
        }
        catch (Exception)
        {
            res.Dispose();
            throw;
        }
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    // A response's body whose every read has to bring something within the limit.
    private sealed class Watched(HttpResponseMessage res, Stream body, string host, TimeSpan idle) : Stream
    {
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
            try { return await body.ReadAsync(buffer, wait.Token); }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{host} sent nothing for {Seconds(idle)} seconds: try again later", e);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { body.Dispose(); res.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
