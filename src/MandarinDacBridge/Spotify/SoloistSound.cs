// SoloistSound.cs — Spotify's sound: the private PulseAudio each DAC's Soloist plays into (no sound card,
// only a pipe sink), and the pipe read at the DAC's pace and handed to the renderer.
// A part of SoloistPlayer (SoloistPlayer.cs), sharing its state and its lock.
using System.Diagnostics;
using System.Text;

namespace MandarinDacBridge.Spotify;

internal sealed partial class SoloistPlayer
{
    private Dictionary<string, string> PrivateEnvironment() => new()
    {
        // Only the private server: no PipeWire to be found, PulseAudio at our socket.
        ["XDG_RUNTIME_DIR"] = runDir,
        ["PIPEWIRE_RUNTIME_DIR"] = runDir,
        ["PULSE_RUNTIME_PATH"] = Path.GetDirectoryName(socket)!,
        ["PULSE_STATE_PATH"] = Path.Combine(runDir, "state"),
        ["PULSE_SERVER"] = "unix:" + socket
    };

    private void StartPulse(string exe)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "-n", "--daemonize=no", "--exit-idle-time=-1", "--use-pid-file=no", "--disable-shm=yes", "--log-target=stderr",
                     $"--load=module-native-protocol-unix socket={socket} auth-anonymous=1",
                     $"--load=module-pipe-sink file={fifo} sink_name=bridge format=s32le rate={Rate} channels=2"
                 })
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in PrivateEnvironment()) psi.Environment[k] = v;
        var errors = new StringBuilder();
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("PulseAudio didn't start"); }
        catch (System.ComponentModel.Win32Exception e) { throw new NotReady($"can't run {exe}: {e.Message}"); }
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } l && l.StartsWith("E:")) lock (errors) errors.AppendLine(l); };
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        lock (gate) pulse = p;
        for (int i = 0; i < 100 && !File.Exists(socket); i++)
        {
            if (p.HasExited) break;
            Wait(50);
        }
        if (!File.Exists(socket))
        {
            string e;
            lock (errors) e = errors.ToString().Trim().Split('\n').LastOrDefault() ?? "";
            throw new InvalidOperationException("Soloist's sound server didn't start" + (e != "" ? ": " + e : ""));
        }
    }

    // What the sound server writes: to the renderer while it plays it, else let go at the speed of a DAC.
    private void ReadSound()
    {
        FileStream pipe;
        try { pipe = new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0); }
        catch (Exception e) { Log("sound pipe: " + e.Message); return; }
        using (pipe)
        {
            var buf = new byte[1 << 14];
            int have = 0;
            var pace = Stopwatch.StartNew();
            long paced = 0;
            while (!readerStop && !stop.IsCancellationRequested)
            {
                int n;
                try { n = pipe.Read(buf, have, buf.Length - have); }
                catch (Exception) { return; }
                if (n <= 0 || readerStop) return;
                have += n;
                int usable = have - have % FrameBytes;
                if (usable == 0) continue;
                var chunk = buf[..usable];
                Array.Copy(buf, usable, buf, 0, have - usable);
                have -= usable;

                LiveSource? s;
                lock (gate) s = attached;
                if (s != null)
                {
                    if (s.Push(chunk, stop.Token)) { pace.Restart(); paced = 0; continue; }
                    Detach(s);
                }
                lock (gate)
                {
                    preroll.Enqueue(chunk);
                    while (preroll.Count > 8) preroll.Dequeue();
                }
                paced += chunk.Length / FrameBytes;
                var ahead = paced * 1000 / Rate - pace.ElapsedMilliseconds;
                if (ahead > 0) stop.Token.WaitHandle.WaitOne((int)ahead);
                if (pace.ElapsedMilliseconds > 2000) { pace.Restart(); paced = 0; }
            }
        }
    }

    private static bool HasSound(byte[] chunk)
    {
        foreach (var b in chunk) if (b != 0) return true;
        return false;
    }

    // The renderer opens the stream: the one being filled.
    private IPcmSource Open(CancellationToken ct)
    {
        lock (gate) return attached is { Closed: false } s ? s : throw new InvalidOperationException("Spotify isn't playing");
    }

    private void Detach(LiveSource? which)
    {
        lock (gate)
        {
            if (which != null && attached != which) return;
            attached?.Dispose();
            attached = null;
        }
    }
}
