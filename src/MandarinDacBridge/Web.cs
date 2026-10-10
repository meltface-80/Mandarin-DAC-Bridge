// Web.cs — one HTTP server on port 55500 for both faces of the bridge:
//
//   /                       the page: a tile per DAC, tap for its capabilities
//   /api/dacs               what the page shows (JSON)
//   /api/dacs/<id>/settings POST { enabled, dsd, name }
//   /api/dacs/<id>/release  POST: let another controller take the DAC now
//   /api/services           GET, POST { squeezelite, lmsServer, spotify, soloistKey }: the ways in besides UPnP
//   /api/services/soloist   POST: download Spotify Soloist from Spotify
//   /api/services/caldera   POST: download Caldera Headless from Caldera
//   /api/services/plex      POST: a code to sign in to Plex at plex.tv/link (for Caldera)
//   /api/services/qobuz     POST: install QobuzProxy (Qobuz Connect) into a private Python environment
//   /api/dacs/<id>/art      the cover of what Caldera plays (fetched with the Plex token, kept here)
//   /now                    the now-playing screen, for a display beside the DAC
//   /api/health
//   /upnp/<id>/…            each DAC's UPnP device: description, SCPDs,
//                           control (SOAP) and events (GENA)
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MandarinDacBridge.Upnp;
using Microsoft.AspNetCore.Http;

namespace MandarinDacBridge;

internal static partial class Web
{
    private const string XmlType = "text/xml; charset=\"utf-8\"";

    private static readonly Dictionary<string, string> Types = new()
    {
        [".html"] = "text/html; charset=utf-8", [".png"] = "image/png", [".woff2"] = "font/woff2", [".txt"] = "text/plain; charset=utf-8"
    };

    private static Task Send(HttpContext ctx, int status, string type, string text)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        var b = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentLength = b.Length;
        return ctx.Response.Body.WriteAsync(b).AsTask();
    }

    private static Task Json<T>(HttpContext ctx, int status, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Send(ctx, status, "application/json; charset=utf-8", JsonSerializer.Serialize(value, type));
    }

    private static async Task<string> Body(HttpContext ctx, int limit = 1 << 20)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > limit) throw new InvalidDataException("too large");
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static async Task Handle(HttpContext ctx, Manager manager, Events events)
    {
        var p = ctx.Request.Path.Value ?? "/";
        var method = ctx.Request.Method;
        Match m;

        // ---------------------------------------------------------------- UPnP
        if ((m = DescriptionPath().Match(p)).Success)
        {
            var b = manager.Get(m.Groups[1].Value);
            if (b == null) { await Send(ctx, 404, "text/plain", "no such DAC"); return; }
            await Send(ctx, 200, XmlType, Upnp.Description.Document(b.Id, b.Udn, b.FriendlyName(), b.Dev.Manufacturer, b.Dev.Name, Config.Version));
            return;
        }
        if ((m = ServicePath().Match(p)).Success)
        {
            var (id, service, what) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            var b = manager.Get(id);
            if (b == null || !Scpd.Services.TryGetValue(service, out var svc)) { await Send(ctx, 404, "text/plain", "not found"); return; }
            if (what == "scpd.xml") { await Send(ctx, 200, XmlType, Scpd.Document(service)!); return; }
            if (what == "event")
            {
                if (method is not ("SUBSCRIBE" or "UNSUBSCRIBE")) { await Send(ctx, 405, "text/plain", "SUBSCRIBE only"); return; }
                var h = ctx.Request.Headers;
                var (status, sid, timeout) = events.Request(method, id, service, h["SID"].ToString(), h["CALLBACK"].ToString(), h["NT"].ToString(), h["TIMEOUT"].ToString());
                ctx.Response.StatusCode = status;
                if (sid != null) { ctx.Response.Headers["SID"] = sid; ctx.Response.Headers["TIMEOUT"] = $"Second-{timeout}"; }
                ctx.Response.ContentLength = 0;
                return;
            }
            if (method != "POST") { await Send(ctx, 405, "text/plain", "POST only"); return; }
            var (action, args) = Xml.ParseSoap(await Body(ctx), ctx.Request.Headers["SOAPACTION"].ToString());
            ctx.Response.Headers["EXT"] = "";
            try
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                var output = await Control.Handle(b, service, action, args, ip, ctx.Request.Headers.UserAgent.ToString());
                await Send(ctx, 200, XmlType, Control.Response(svc.Type, action, output));
            }
            catch (UpnpException e) { await Send(ctx, 500, XmlType, Control.Fault(e.Code, e.Message)); }
            catch (Exception e)
            {
                Log.Write($"{service}.{action}: {e}");
                await Send(ctx, 500, XmlType, Control.Fault(501, "Action failed: " + e.Message));
            }
            return;
        }

        // ---------------------------------------------------------------- API
        if (p == "/api/health") { await Json(ctx, 200, new Health(true, Config.Version, manager.Count), BridgeJson.Default.Health); return; }
        if (p == "/api/dacs" && method == "GET") { await Json(ctx, 200, manager.View(), BridgeJson.Default.BridgeView); return; }
        if (p == "/api/services")
        {
            if (method == "GET") { await Json(ctx, 200, manager.Services(), BridgeJson.Default.ServicesView); return; }
            if (method != "POST") { await Send(ctx, 405, "text/plain", "GET or POST"); return; }
            ServiceSettings? sp;
            try { sp = JsonSerializer.Deserialize(await Body(ctx, 4096), BridgeJson.Default.ServiceSettings); }
            catch (Exception) { await Send(ctx, 400, "application/json", "{\"error\":\"bad JSON\"}"); return; }
            await Json(ctx, 200, manager.SetServices(sp ?? new ServiceSettings()), BridgeJson.Default.ServicesView);
            return;
        }
        if (p == "/api/services/soloist" && method == "POST")
        {
            _ = manager.DownloadSoloist();
            await Json(ctx, 202, manager.Services(), BridgeJson.Default.ServicesView);
            return;
        }
        if (p == "/api/services/caldera" && method == "POST")
        {
            _ = manager.DownloadCaldera();
            await Json(ctx, 202, manager.Services(), BridgeJson.Default.ServicesView);
            return;
        }
        if (p == "/api/services/qobuz" && method == "POST")
        {
            _ = manager.InstallQobuz();
            await Json(ctx, 202, manager.Services(), BridgeJson.Default.ServicesView);
            return;
        }
        if (p == "/api/services/plex" && method == "POST")
        {
            await manager.LinkPlex();
            await Json(ctx, 200, manager.Services(), BridgeJson.Default.ServicesView);
            return;
        }
        if ((m = ArtPath().Match(p)).Success && method == "GET")
        {
            if (manager.CalderaArt(m.Groups[1].Value) is not { } art) { await Send(ctx, 404, "text/plain", "no cover"); return; }
            try
            {
                using var cover = await Sources.Http.GetAsync(art, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if (!cover.IsSuccessStatusCode) { await Send(ctx, 502, "text/plain", "no cover"); return; }
                ctx.Response.ContentType = cover.Content.Headers.ContentType?.MediaType is { } t && t.StartsWith("image/") ? t : "image/jpeg";
                ctx.Response.Headers.CacheControl = "max-age=3600";
                await cover.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            catch (Exception) { /* the page asks again */ }
            return;
        }
        if ((m = ApiPath().Match(p)).Success && method == "POST")
        {
            var id = m.Groups[1].Value;
            if (!manager.Has(id)) { await Send(ctx, 404, "application/json", "{\"error\":\"no such DAC\"}"); return; }
            if (m.Groups[2].Value == "release") { manager.Release(id); await Send(ctx, 200, "application/json", "{\"ok\":true}"); return; }
            SettingsPatch? patch;
            try { patch = JsonSerializer.Deserialize(await Body(ctx, 4096), BridgeJson.Default.SettingsPatch); }
            catch (Exception) { await Send(ctx, 400, "application/json", "{\"error\":\"bad JSON\"}"); return; }
            await Json(ctx, 200, manager.SetSettings(id, patch ?? new SettingsPatch()), BridgeJson.Default.DacSettings);
            return;
        }

        // ---------------------------------------------------------------- the page
        if (method is not ("GET" or "HEAD")) { await Send(ctx, 405, "text/plain", "not allowed"); return; }
        var rel = p == "/" ? "index.html" : p is "/now" or "/now/" ? "now.html" : p.TrimStart('/');
        if (rel.Contains("..") || !SafePath().IsMatch(rel)) { await Send(ctx, 404, "text/plain", "not found"); return; }
        using var res = typeof(Web).Assembly.GetManifestResourceStream("Page/" + rel);
        if (res == null) { await Send(ctx, 404, "text/plain", "not found"); return; }
        ctx.Response.ContentType = Types.GetValueOrDefault(Path.GetExtension(rel), "application/octet-stream");
        ctx.Response.Headers.CacheControl = rel.EndsWith(".html") ? "no-cache" : "max-age=86400";
        ctx.Response.ContentLength = res.Length;
        if (method == "GET") await res.CopyToAsync(ctx.Response.Body);
    }

    [GeneratedRegex(@"^/upnp/([\w-]+)/description\.xml$")] private static partial Regex DescriptionPath();
    [GeneratedRegex(@"^/upnp/([\w-]+)/(\w+)/(scpd\.xml|control|event)$")] private static partial Regex ServicePath();
    [GeneratedRegex(@"^/api/dacs/([\w-]+)/(settings|release)$")] private static partial Regex ApiPath();
    [GeneratedRegex(@"^[\w./-]+$")] private static partial Regex SafePath();
    [GeneratedRegex(@"^/api/dacs/([\w-]+)/art$")] private static partial Regex ArtPath();
}
