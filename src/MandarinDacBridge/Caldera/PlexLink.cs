// PlexLink.cs — signing in to Plex from the bridge's page, the way a TV app
// does: the bridge asks plex.tv for a four-character code, the page shows it,
// the person enters it at plex.tv/link, and the bridge collects the token.
// The token is kept in settings.json (owner-only) and given to Caldera; it is
// never sent back to the page.
using System.Text.Json;

namespace MandarinDacBridge.Caldera;

internal sealed class PlexLink(string clientId, Action<string> gotToken)
{
    private readonly object gate = new();
    private CancellationTokenSource? polling;

    public string Code { get; private set; } = "";
    public string Message { get; private set; } = "";

    private static HttpRequestMessage Request(HttpMethod method, string url, string clientId)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.TryAddWithoutValidation("Accept", "application/json");
        r.Headers.TryAddWithoutValidation("X-Plex-Product", "Mandarin DAC Bridge");
        r.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientId);
        return r;
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
            using var res = await Sources.Http.SendAsync(Request(HttpMethod.Post, "https://plex.tv/api/v2/pins", clientId), cts.Token);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            var id = doc.RootElement.GetProperty("id").GetInt64();
            Code = doc.RootElement.GetProperty("code").GetString() ?? "";
            Message = "";
            _ = Poll(id, cts);
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
                using var res = await Sources.Http.SendAsync(Request(HttpMethod.Get, $"https://plex.tv/api/v2/pins/{id}", clientId), cts.Token);
                if (!res.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
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
