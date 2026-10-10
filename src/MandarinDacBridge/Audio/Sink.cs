// Sink.cs — a DAC held by the bridge, from the player's side.
//
// A sink takes the DAC when it starts and keeps it until it is disposed —
// playing or not — so no other program can play to it. If another program
// has it, the sink keeps trying and says who (Exclusive, Message,
// HolderPid). Samples arrive as interleaved signed 32-bit little-endian PCM
// at the rate Configure set; the DAC is switched to that rate and nothing is
// converted on the way.
//
// One thread (the renderer's feed) calls Configure, Write and Drain; Flush
// and Pause come from the control side, after the feed has been stopped (or
// for Pause, at any time).
//
// Sharing (a DAC's "share when idle" setting, for Roon Bridge, squeezelite
// or any other player on the same machine): while nothing plays, the sink
// lets go of the DAC and doesn't take it back by itself; Reclaim takes it
// back when the bridge is asked to play.
namespace MandarinDacBridge.Audio;

internal sealed class DeviceException(string message) : Exception(message);

internal interface ISink : IDisposable
{
    bool Exclusive { get; }
    string Message { get; }
    int HolderPid { get; }
    event Action? StatusChanged;
    event Action? Gone;

    // Starts holding the DAC (in the background, retrying while another program has it).
    void Start();
    // Sets the DAC to this rate and channel count; returns what it was set to ("24-bit integer, 2 ch").
    string Configure(int rate, int channels);
    // Takes whole frames; blocks while the DAC's buffer is full (or paused).
    void Write(ReadOnlySpan<byte> pcm, CancellationToken ct);
    // Returns once everything written has been played; the clock then starts again at 0.
    void Drain(CancellationToken ct);
    // Everything held is thrown away; the clock starts again at 0.
    void Flush();
    void Pause(bool on);
    // Frames played since the last Configure, Flush or Drain.
    long Played { get; }
    // Lets go of the DAC (on) and stays off it until Reclaim; off: holds it as always.
    void Share(bool on);
    // Takes the DAC back now, if it is free; true when the sink has it. Configure must follow.
    bool Reclaim();
}

internal static class SinkFactory
{
    public static ISink Create(DacDevice dev, Config config)
    {
        if (config.TestSink) return new ClockSink(busy: config.TestSinkBusy);
        if (OperatingSystem.IsMacOS()) return new CoreAudioSink(dev.Spec);
        if (OperatingSystem.IsLinux()) return new AlsaSink(dev.Spec);
        throw new PlatformNotSupportedException("only macOS and Linux are supported");
    }
}

internal static class Sharing
{
    public const string Idle = "shared: let go while idle, for other players";
}
