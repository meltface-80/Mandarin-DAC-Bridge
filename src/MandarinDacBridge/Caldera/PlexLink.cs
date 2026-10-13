// PlexLink.cs — signing in to Plex from the bridge's page, the way a TV app
// does: the bridge asks plex.tv for a four-character code, the page shows it,
// the person enters it at plex.tv/link, and the bridge collects the token.
// The token is kept in settings.json (owner-only) and given to Caldera; it is
// never sent back to the page.
using System.Text.Json;

namespace MandarinDacBridge.Caldera;

internal sealed class PlexLink(string clientId, Action<string> gotToken)
{
    // plex.tv (the tests answer for it).
    internal static string PlexTv = "https://plex.tv";

    // How long one question to plex.tv is waited on: Sources.Http itself waits for ever (it streams tracks).
    internal static TimeSpan Ask = TimeSpan.FromSeconds(15);

    private readonly object gate = new();
    private CancellationTokenSource? polling;

    public string Code { get; private set; } = "";
    public string Message { get; private set; } = "";

    internal static HttpRequestMessage Request(HttpMethod method, string url, string clientId)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("Accept", "application/json");
        r.Headers.TryAddWithoutValidation("X-Plex-Product", "Mandarin DAC Bridge");
        r.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientId);
        return r;
    }

    // Who a token belongs to (their Plex username, else their email), or "" if plex.tv won't say.
    public static async Task<string> Account(string token, string clientId)
    {
        try
        {
            using var req = Request(HttpMethod.Get, $"{PlexTv}/api/v2/user", clientId);
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            using var cts = new CancellationTokenSource(Ask);
            using var res = await Sources.Http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return "";
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            foreach (var k in new[] { "username", "title", "email" })
                if (doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
        }
        catch (Exception) { /* offline, plex.tv slow or changed: the name stays unknown until the bridge next starts or signs in */ }
        return "";
    }

    // Forgets a code being waited on (signing out).
    public void Cancel()
    {
        lock (gate) polling?.Cancel();
        Code = "";
        Message = "";
    }

    // Asks for a code, then waits (up to 15 minutes) for it to be entered at plex.tv/link.
    public async Task Start()
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            polling?.Cancel();
            polling = cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        }
        try
        {
            using var one = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            one.CancelAfter(Ask);
            using var res = await Sources.Http.SendAsync(Request(HttpMethod.Post, $"{PlexTv}/api/v2/pins", clientId), one.Token);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(one.Token));
            var id = doc.RootElement.GetProperty("id").GetInt64();
            Code = doc.RootElement.GetProperty("code").GetString() ?? "";
            Message = "";
            _ = Poll(id, cts);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { /* signed out, or a newer code asked for: that says what's what */ }
        catch (OperationCanceledException)
        {
            Code = "";
            Message = "plex.tv didn't answer: try again";
        }
        catch (Exception e)
        {
            Code = "";
            Message = "plex.tv: " + e.Message;
        }
    }

    private async Task Poll(long id, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(2000, cts.Token);
                using var one = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                one.CancelAfter(Ask);
                string body;
                try
                {
                    using var res = await Sources.Http.SendAsync(Request(HttpMethod.Get, $"{PlexTv}/api/v2/pins/{id}", clientId), one.Token);
                    if (!res.IsSuccessStatusCode) continue;
                    body = await res.Content.ReadAsStringAsync(one.Token);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested) { continue; }   // that one went unanswered: ask again
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("authToken", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } token)
                {
                    Code = "";
                    Message = "";
                    gotToken(token);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { if (Code != "") { Code = ""; Message = "the code ran out: ask for a new one"; } }
        catch (Exception e) { Code = ""; Message = "plex.tv: " + e.Message; }
    }
}
