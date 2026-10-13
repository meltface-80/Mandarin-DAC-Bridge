// SoloistSocket.cs — Soloist's WebSocket: what Spotify plays, pauses and stops, and its volume (the DAC's
// own, where it has one; held at 100% where it hasn't).
// A part of SoloistPlayer (SoloistPlayer.cs), sharing its state and its lock.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MandarinDacBridge.Spotify;

internal sealed partial class SoloistPlayer
{
    private void Listen()
    {
        var portFile = Path.Combine(dataDir, "ws.port");
        var until = Environment.TickCount64 + 20_000;
        while (!File.Exists(portFile))
        {
            if (soloist is not { HasExited: false } || stop.IsCancellationRequested) return;
            if (Environment.TickCount64 > until) throw new InvalidOperationException("Soloist didn't open its WebSocket" + (soloistError != "" ? ": " + soloistError : ""));
            Wait(100);
        }
        Wait(100);
        var port = File.ReadAllText(portFile).Trim();
        var addrFile = Path.Combine(dataDir, "ws.addr");
        var addr = File.Exists(addrFile) ? File.ReadAllText(addrFile).Trim() : "127.0.0.1";
        if (addr is "" or "0.0.0.0") addr = "127.0.0.1";
        if (addr.Contains(':') && !addr.StartsWith('[')) addr = $"[{addr}]";
        var w = new ClientWebSocket();
        lock (gate) ws = w;
        w.ConnectAsync(new Uri($"ws://{addr}:{port}"), stop.Token).Wait(TimeSpan.FromSeconds(10));
        if (w.State != WebSocketState.Open) throw new InvalidOperationException("couldn't reach Soloist's WebSocket");
        SetStatus("ready · choose it in Spotify");
        lastError = "";

        var buf = new byte[1 << 16];
        using var msg = new MemoryStream();
        while (!stop.IsCancellationRequested && w.State == WebSocketState.Open)
        {
            WebSocketReceiveResult got;
            try { got = w.ReceiveAsync(buf, stop.Token).GetAwaiter().GetResult(); }
            catch (Exception) { return; }
            if (got.MessageType == WebSocketMessageType.Close) return;
            msg.Write(buf, 0, got.Count);
            if (!got.EndOfMessage) continue;
            try
            {
                using var doc = JsonDocument.Parse(msg.ToArray());
                OnMessage(doc.RootElement);
            }
            catch (JsonException) { /* not JSON: skip it */ }
            catch (Exception e) { if (!stop.IsCancellationRequested) Log("event: " + e.Message); }
            msg.SetLength(0);
        }
    }

    private async Task Command(string json)
    {
        ClientWebSocket? w;
        lock (gate) w = ws;
        if (w is not { State: WebSocketState.Open }) return;
        await sendGate.WaitAsync();
        try { await w.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, stop.Token); }
        catch (Exception) { /* closing */ }
        finally { sendGate.Release(); }
    }

    private void OnMessage(JsonElement m)
    {
        switch (Str(m, "type"))
        {
            case "auth_state":
                Keep("[bridge] " + m.GetRawText());
                if (Bool(m, "logged_in") != signedIn || Bool(m, "is_active") != active)
                {
                    signedIn = Bool(m, "logged_in");
                    active = Bool(m, "is_active");
                    Log(signedIn == true ? $"Soloist is signed in{(active == true ? ", and the Spotify app has chosen it" : "")}" : "Soloist is waiting for the Spotify app to choose it");
                }
                if (!Bool(m, "logged_in")) SetStatus("ready · choose it in Spotify");
                else if (!Bool(m, "is_active") && !Ours()) SetStatus("connected · choose it in Spotify to play here");
                break;
            case "playback_state":
                Keep("[bridge] " + Clip(m.GetRawText()));
                if (m.TryGetProperty("item", out var item)) SetItem(item);
                if (m.TryGetProperty("position", out var pos)) SetPosition(pos);
                if (!bridge.VolumeOn) KeepVolume(m);
                Apply(Str(m, "status"));
                break;
            case "track_changed":
                Keep("[bridge] " + Clip(m.GetRawText()));
                if (m.TryGetProperty("item", out var it)) SetItem(it);
                break;
            case "playback_changed":
                Keep("[bridge] playback " + Str(m, "status"));
                Apply(Str(m, "status"));
                break;
            case "position_sync":
                if (m.TryGetProperty("position", out var p)) SetPosition(p);
                break;
            case "volume_changed":
                if (bridge.VolumeOn) TakeVolume(m); else KeepVolume(m);
                break;
            case "error":
                Keep("[bridge] " + m.GetRawText());
                Log("Soloist: " + Str(m, "message"));
                break;
        }
    }

    private void Apply(string status)
    {
        var r = bridge.Renderer;
        switch (status)
        {
            case "playing":
                Play();
                break;
            case "paused":
                if (Ours() && r.IsActive) Try(r.Pause);
                if (Ours()) SetStatus("paused");
                break;
            case "idle":
                if (Ours() && (r.IsActive || r.Transport == "PAUSED_PLAYBACK")) Try(r.Stop);
                if (Ours() || Status is "playing" or "paused") SetStatus("ready · choose it in Spotify");
                break;
        }
    }

    // A DAC without a volume of its own: Spotify's stays at 100% (use the amplifier's), bit-perfect.
    private void KeepVolume(JsonElement m)
    {
        if (m.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 100)
            _ = Command("{\"type\":\"command\",\"command\":\"set_volume\",\"volume\":100}");
    }

    private int told = -1;     // the level last passed to Spotify (its echo is not a change)

    // Spotify's slider moved: the DAC follows (unless another app has the DAC).
    private void TakeVolume(JsonElement m)
    {
        if (!m.TryGetProperty("volume", out var v) || v.ValueKind != JsonValueKind.Number) return;
        int level = Math.Clamp(v.GetInt32(), 0, 100);
        if (level == told || level == bridge.Level) return;
        if (!Ours() && bridge.Arbiter.Holding(bridge.Renderer.IsActive)) { TellVolume(bridge.Level ?? 100); return; }
        bridge.SetLevel(level);
    }

    // The DAC's volume moved (the page, another app, the machine): Spotify's slider follows.
    private void TellVolume(int level)
    {
        if (!bridge.VolumeOn || level == told) return;
        told = level;
        _ = Command($"{{\"type\":\"command\",\"command\":\"set_volume\",\"volume\":{level}}}");
    }
}
