// RoonRunning.cs — whether Roon Bridge or Roon Server runs on this machine (not the one the bridge runs
// itself: that is RoonBridgeHost).
using System.Diagnostics;

namespace MandarinDacBridge;

// Roon Bridge (or Roon Server) on this machine: shown on the page, so "share when idle" makes sense.
internal static class RoonBridge
{
    private static readonly string[] Names = ["RoonBridge", "RAATServer", "RoonServer", "RoonAppliance"];
    private static (long At, string Found) cache = (long.MinValue, "");

    // The Roon program running here ("RoonBridge"), or "" (also in Docker, which can't see the host's programs).
    public static string Running(bool fresh = false)
    {
        var c = cache;
        if (!fresh && Environment.TickCount64 - c.At < 10_000) return c.Found;
        var found = "";
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string name;
                    try { name = p.ProcessName; } catch (Exception) { continue; }
                    var n = Names.FirstOrDefault(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
                    if (n != null) { found = n is "RAATServer" ? "RoonBridge" : n; break; }
                }
            }
        }
        catch (Exception) { /* not allowed to look */ }
        cache = (Environment.TickCount64, found);
        return found;
    }
}
