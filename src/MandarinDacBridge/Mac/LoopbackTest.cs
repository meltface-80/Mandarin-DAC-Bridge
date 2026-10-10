// LoopbackTest.cs — `mandarin-dac-bridge --loopback-test` (macOS): checks
// that the "DAC Bridge" output gives back, on its input, exactly what was
// played to it. A known 24-bit pattern (a count on the left, scrambled
// values over the whole range on the right) goes out at each of a few rates;
// what comes back is turned into integers as MacSource does, and must match
// sample for sample.
using System.Runtime.InteropServices;
using MandarinDacBridge.Native;

namespace MandarinDacBridge.Mac;

internal static unsafe class LoopbackTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public long Next, Limit;   // pattern frames written, and how many to write
        public int* Got;           // what came back, stereo
        public long Cap, N;
    }

    private static int Left(long n) => (int)((n + 1) & 0x7FFFFF);
    private static int Right(long n) => (int)(((uint)(n * 2654435761L) >> 8) << 8) >> 8;   // 24-bit, signed

    [UnmanagedCallersOnly]
    private static int Io(uint device, void* now, AudioBufferList* input, void* inputTime, AudioBufferList* output, void* outputTime, void* client)
    {
        var s = (State*)client;
        if (output != null && output->NumberBuffers > 0 && (&output->First)->Data != null && (&output->First)->NumberChannels > 0)
        {
            var b = &output->First;
            int ch = (int)b->NumberChannels;
            long frames = b->DataByteSize / (sizeof(float) * ch);
            var d = (float*)b->Data;
            for (long f = 0; f < frames; f++)
            {
                long n = s->Next + f;
                bool on = n < s->Limit;
                for (int c = 0; c < ch; c++)
                    d[f * ch + c] = !on || c > 1 ? 0f : (float)((c == 0 ? Left(n) : Right(n)) / 8388608.0);
            }
            s->Next += frames;
        }
        if (input != null && input->NumberBuffers > 0 && (&input->First)->Data != null && (&input->First)->NumberChannels > 0)
        {
            var b = &input->First;
            int ch = (int)b->NumberChannels;
            long frames = b->DataByteSize / (sizeof(float) * ch);
            var src = (float*)b->Data;
            for (long f = 0; f < frames && s->N < s->Cap; f++, s->N++)
            {
                s->Got[s->N * 2] = MacSource.ToInt(src[f * ch]);
                s->Got[s->N * 2 + 1] = MacSource.ToInt(ch > 1 ? src[f * ch + 1] : src[f * ch]);
            }
        }
        return 0;
    }

    public static int Run()
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("The loopback test is for macOS."); return 2; }
        var dev = CoreAudio.FindDevice(MacSource.LoopbackUid);
        if (dev == CoreAudio.Unknown)
        {
            Console.WriteLine("No DAC Bridge output: install it first (tools/mac/driver.sh).");
            return 2;
        }
        CoreAudio.SetScalar(dev, 1);
        bool ok = true;
        foreach (int rate in new[] { 44100, 96000, 192000 })
            ok &= Once(dev, rate);
        Console.WriteLine(ok ? "Bit-exact at every rate." : "Not bit-exact (see above).");
        return ok ? 0 : 1;
    }

    private static bool Once(uint dev, int rate)
    {
        CoreAudio.SetRate(dev, rate);
        for (int i = 0; i < 40 && (int)CoreAudio.NominalRate(dev) != rate; i++) Thread.Sleep(50);
        if ((int)CoreAudio.NominalRate(dev) != rate) { Console.WriteLine($"{Devices.KHz(rate)}: the device stayed at {Devices.KHz((int)CoreAudio.NominalRate(dev))}"); return false; }

        var s = (State*)NativeMemory.AllocZeroed((nuint)sizeof(State));
        s->Limit = rate * 3 / 2;
        s->Cap = rate * 3;
        s->Got = (int*)NativeMemory.AllocZeroed((nuint)(s->Cap * 2 * sizeof(int)));
        try
        {
            int err = CoreAudio.CreateIOProcId(dev, &Io, s, out var proc);
            if (err != 0 || proc == IntPtr.Zero) { Console.WriteLine($"{Devices.KHz(rate)}: couldn't open the device ({err})"); return false; }
            err = CoreAudio.DeviceStart(dev, proc);
            if (err == 0)
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until && Volatile.Read(ref s->N) < s->Cap) Thread.Sleep(20);
                CoreAudio.DeviceStop(dev, proc);
            }
            CoreAudio.DestroyIOProcId(dev, proc);
            if (err != 0) { Console.WriteLine($"{Devices.KHz(rate)}: couldn't start the device ({err})"); return false; }
            return Check(rate, s);
        }
        finally
        {
            NativeMemory.Free(s->Got);
            NativeMemory.Free(s);
        }
    }

    private static bool Check(int rate, State* s)
    {
        long first = -1;
        for (long i = 0; i < s->N && first < 0; i++) if (s->Got[i * 2] != 0) first = i;
        if (first < 0)
        {
            Console.WriteLine($"{Devices.KHz(rate)}: nothing came back ({s->N} frames of silence); allow Microphone access for this program");
            return false;
        }
        long n0 = (s->Got[first * 2] >> 8) - 1, matched = 0, wrong = 0;
        string firstWrong = "";
        for (long i = first; i < s->N; i++)
        {
            long n = n0 + (i - first);
            if (n >= s->Limit) break;
            int l = Left(n) << 8, r = Right(n) << 8;
            if (s->Got[i * 2] == l && s->Got[i * 2 + 1] == r) { matched++; continue; }
            if (wrong++ == 0) firstWrong = $"frame {n}: sent {Left(n)}, {Right(n)}; got {s->Got[i * 2] >> 8}, {s->Got[i * 2 + 1] >> 8}";
        }
        bool ok = wrong == 0 && matched >= rate;
        Console.WriteLine($"{Devices.KHz(rate)}: {matched} frames identical, {wrong} different{(n0 > 0 ? $", the first {n0} not heard" : "")}{(firstWrong != "" ? " (" + firstWrong + ")" : "")}");
        return ok;
    }
}
