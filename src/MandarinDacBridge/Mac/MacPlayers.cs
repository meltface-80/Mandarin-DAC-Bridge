// MacPlayers.cs — the Music app and the Spotify app on this Mac, from the
// outside, by AppleScript (osascript): what plays, where it is, its cover,
// and the buttons (pause, play, back to the start of a track). Neither app is
// ever launched by the bridge: when it isn't running, there's nothing to ask.
//
// The way of it — the track's own rate from the Music app (which names it a
// moment after a track starts, sometimes low at first), Spotify at 44.1 kHz,
// a pause around a change of rate — follows Arco (MusicWatcher.swift,
// SpotifyWatcher.swift, TrackClock.swift; github.com/renebouwmeester/arco).
using System.Diagnostics;
using System.Globalization;

namespace MandarinDacBridge.Mac;

internal sealed record MacTrack(string App, string State, string Title, string Artist, string Album, double Duration, double Position, string Id);

internal static class MacPlayers
{
    public const string Music = "Music";
    public const string Spotify = "Spotify";

    public static bool Running(string app)
    {
        try
        {
            var ps = Process.GetProcessesByName(app);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
        catch (Exception) { return false; }
    }

    // osascript, with a time limit (an app that doesn't answer mustn't hold the bridge up).
    public static string? Run(string script, int ms = 4000)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var o = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(ms)) { try { p.Kill(); } catch (Exception) { /* gone */ } return null; }
            return p.ExitCode == 0 ? o.Result.TrimEnd('\n', '\r') : null;
        }
        catch (Exception) { return null; }
    }

    // What the app is playing: state, title, artist, album, duration, position (seconds).
    public static MacTrack? Now(string app)
    {
        if (!Running(app)) return null;
        var reply = Run($"""
            tell application "{app}"
                set s to player state as string
                if s is "stopped" then return s
                set t to current track
                return s & linefeed & (name of t) & linefeed & (artist of t) & linefeed & (album of t) & linefeed & ((duration of t) as string) & linefeed & ((player position) as string) & linefeed & (id of t as string)
            end tell
            """);
        return reply == null ? null : Parse(app, reply);
    }

    public static MacTrack Parse(string app, string reply)
    {
        var l = reply.Replace("\r", "").Split('\n');
        var state = l[0].Trim();
        if (state is not ("playing" or "paused") || l.Length < 6) return new MacTrack(app, state is "playing" or "paused" ? state : "stopped", "", "", "", 0, 0, "");
        static double D(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        double duration = D(l[4]);
        if (app == Spotify) duration /= 1000;    // Spotify says milliseconds
        return new MacTrack(app, state, l[1], l[2], l[3], duration, D(l[5]), l.Length > 6 ? l[6].Trim() : $"{l[1]}|{l[2]}|{l[3]}");
    }

    // The Music app's rate for the current track (0 until it knows).
    public static int MusicRate()
    {
        var r = Run($"tell application \"{Music}\" to get sample rate of current track");
        return double.TryParse(r?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)Math.Round(v) : 0;
    }

    // The track's rate as the Music app settles on it: read every quarter second, for up to three seconds;
    // a low reading (44.1, 48) is trusted only at the end, or when the track isn't known to be hi-res.
    public static int ReadMusicRate(Func<bool> stillCurrent, int remembered, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        int last = 0;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested && stillCurrent())
        {
            int r = MusicRate();
            if (r > 0)
            {
                last = r;
                if (r > 48000 || remembered <= 48000) return r;
            }
            ct.WaitHandle.WaitOne(250);
        }
        return Math.Max(last, remembered);
    }

    public static void Pause(string app) => Run($"tell application \"{app}\" to pause");
    public static void Play(string app) => Run($"tell application \"{app}\" to play");
    public static void ToStart(string app) => Run($"tell application \"{app}\" to set player position to 0");

    // The cover: Spotify's URL, or the Music app's artwork written to a file (null when there is none).
    public static string? Cover(string app, string file)
    {
        if (app == Spotify)
            return Run($"tell application \"{Spotify}\" to get artwork url of current track") is { } u && u.StartsWith("http") ? u : null;
        var path = file.Replace("\"", "");
        var ok = Run($"""
            tell application "{Music}"
                if (count of artworks of current track) is 0 then return "none"
                set d to raw data of artwork 1 of current track
            end tell
            set f to open for access (POSIX file "{path}") with write permission
            set eof f to 0
            write d to f
            close access f
            return "ok"
            """, 6000);
        return ok == "ok" && File.Exists(file) ? file : null;
    }
}
