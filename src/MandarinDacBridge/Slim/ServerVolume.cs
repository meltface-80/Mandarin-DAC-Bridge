// ServerVolume.cs — a Squeezebox server's volume and the DAC's own, kept as one (the owner's choice).
//
// LMS sends its stored volume as a player connects and again with every stream it starts, so applying
// what it sends would move the DAC to whatever LMS last had, louder too. Instead, once connected, the
// bridge tells LMS the DAC's level (JSON-RPC "mixer volume"), and tells it again whenever the DAC moves
// elsewhere: LMS's slider shows the DAC's level, and a move of its slider sets the DAC to the number
// it shows. LMS answers each level it is told with that level (its echo): those are waited for and
// never taken as a move, so a late echo can't put the DAC back where it was. A server without
// JSON-RPC (Roon's Squeezebox support) can't be told: where its slider is when it connects is left
// alone, and only its changes after that move the DAC, by as much as they move the slider. Nothing a
// server sends on connecting is put on the DAC, and a change refused (another app has the DAC) is not
// kept for later.
//
//   connected ─▶ deciding (nothing taken) ─▶ told: nothing taken until its first echo, then its moves
//                                         └▶ not told: its changes, by as much
namespace MandarinDacBridge.Slim;

internal sealed class ServerVolume
{
    // LMS's volume (0-100) as it sends it to a squeezeplay-type player (Slim::Player::SqueezePlay):
    // -74 dB over 100 steps in two straight lines meeting at step 25 (-37 dB), in 16.16 fixed point
    // with 8 bits of accuracy between -30 and 0 dB (Slim::Player::Squeezebox2::dBToFixed). Volume 0 is
    // silence (gain 0).
    private static readonly uint[] Gains = Enumerable.Range(0, 101).Select(v => GainOf(v)).ToArray();

    internal static uint GainOf(int volume)
    {
        if (volume <= 0) return 0;
        double db = volume > 25 ? 37.0 / 75 * (volume - 100) : 37.0 / 25 * volume - 74;
        double mult = Math.Pow(10, db / 20);
        return db >= -30 ? (uint)(Math.Floor(mult * 256 + 0.5) * 256) : (uint)Math.Floor(mult * 65536 + 0.5);
    }

    // The LMS volume a gain stands for: the step whose gain is nearest. LMS's 8-bit rounding gives two steps
    // the same gain in three places (41 and 42, 44 and 45, 47 and 48): the same sound, so of those the one
    // nearest the DAC's level (near) is taken, and LMS's echo of the level it was told reads as no change.
    public static int VolumeOf(uint gain, int near = 0)
    {
        int best = 0;
        for (int v = 1; v <= 100; v++)
        {
            double d = Math.Abs((double)Gains[v] - gain), b = Math.Abs((double)Gains[best] - gain);
            if (d < b || (d == b && Math.Abs(v - near) < Math.Abs(best - near))) best = v;
        }
        return best;
    }

    private enum State { Deciding, Told, NotTold }

    private readonly object gate = new();
    private readonly List<int> waiting = [];  // levels told and not yet echoed, oldest first
    private State state = State.Deciding;
    private bool echoed;                       // its first echo has come: what isn't an echo is its slider
    private int? lastSeen;                     // the server's last volume, whatever was done with it

    public void Connected() { lock (gate) { state = State.Deciding; waiting.Clear(); echoed = false; lastSeen = null; } }

    public bool Deciding { get { lock (gate) return state == State.Deciding; } }

    public bool IsTold { get { lock (gate) return state == State.Told; } }

    // About to tell it a level: its echo is waited for (a few at most; older ones are given up). LMS
    // sends nothing back when it is told the level it already has, so then there is nothing to wait for.
    public void Telling(int level)
    {
        lock (gate)
        {
            if (lastSeen == level) { echoed = true; return; }
            waiting.Add(level);
            if (waiting.Count > 8) waiting.RemoveAt(0);
        }
    }

    // The first telling on this connection worked: from its first echo on, it is followed.
    public void Told() { lock (gate) state = State.Told; }

    // It can't be told: where its slider is now is where its changes are counted from.
    public void NotTold() { lock (gate) { state = State.NotTold; waiting.Clear(); } }

    // An echo of something it was told: that one and every older one are done with.
    private bool Echo(int volume)
    {
        int i = waiting.IndexOf(volume);
        if (i < 0) return false;
        waiting.RemoveRange(0, i + 1);
        echoed = true;
        return true;
    }

    // The level the DAC should go to for a volume the server sent, or null to leave it where it is.
    // refused: another app has the DAC, so nothing moves; true back means the server's slider moved and
    // should be put back to the DAC's level (told it again), so the refused change isn't taken later.
    public int? Take(int volume, int dacLevel, bool refused, out bool putBack)
    {
        lock (gate)
        {
            putBack = false;
            var was = lastSeen;
            lastSeen = volume;
            if (Echo(volume)) return null;
            switch (state)
            {
                case State.Told when echoed && volume != dacLevel:
                    if (refused) { putBack = true; return null; }
                    return volume;
                case State.NotTold when !refused && was is { } from && volume != from:
                    return Math.Clamp(dacLevel + volume - from, 0, 100);
                default:
                    // Deciding, waiting for its first echo (a late copy of what it had), no change,
                    // or a change refused without JSON-RPC: absorbed, so it isn't counted later.
                    return null;
            }
        }
    }

    // The DAC moved (the page, another app, its own knob): the level to tell the server, or null.
    public int? ToTell(int dacLevel)
    {
        lock (gate) return state == State.Told && dacLevel != lastSeen && !waiting.Contains(dacLevel) ? dacLevel : null;
    }
}
