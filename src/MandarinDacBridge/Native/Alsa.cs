// Alsa.cs — libasound, for holding a USB DAC on Linux and playing to it.
using System.Runtime.InteropServices;

namespace MandarinDacBridge.Native;

internal static partial class Alsa
{
    private const string Lib = "libasound.so.2";

    public const int StreamPlayback = 0;
    public const int NonBlock = 1;
    public const int AccessRwInterleaved = 3;

    public const int FormatS16Le = 2;
    public const int FormatS24Le = 6;
    public const int FormatS32Le = 10;
    public const int FormatS24_3Le = 32;

    public const int StateOpen = 0, StateSetup = 1, StatePrepared = 2, StateRunning = 3, StateXrun = 4,
        StateDraining = 5, StatePaused = 6, StateSuspended = 7, StateDisconnected = 8;

    public const int EAGAIN = -11, EBUSY = -16, EPIPE = -32, ESTRPIPE = -86, ENODEV = -19, ENOENT = -2, ENXIO = -6, EBADFD = -77, EINTR = -4;

    [LibraryImport(Lib, EntryPoint = "snd_pcm_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(out IntPtr pcm, string name, int stream, int mode);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_close")]
    public static partial int Close(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_nonblock")]
    public static partial int SetNonBlock(IntPtr pcm, int nonblock);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_malloc")]
    public static partial int HwParamsMalloc(out IntPtr hw);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_free")]
    public static partial void HwParamsFree(IntPtr hw);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_any")]
    public static partial int HwParamsAny(IntPtr pcm, IntPtr hw);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_access")]
    public static partial int HwSetAccess(IntPtr pcm, IntPtr hw, int access);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_rate_resample")]
    public static partial int HwSetRateResample(IntPtr pcm, IntPtr hw, uint enable);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_test_format")]
    public static partial int HwTestFormat(IntPtr pcm, IntPtr hw, int format);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_format")]
    public static partial int HwSetFormat(IntPtr pcm, IntPtr hw, int format);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_get_channels_min")]
    public static partial int HwGetChannelsMin(IntPtr hw, out uint channels);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_get_channels_max")]
    public static partial int HwGetChannelsMax(IntPtr hw, out uint channels);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_channels")]
    public static partial int HwSetChannels(IntPtr pcm, IntPtr hw, uint channels);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_rate")]
    public static partial int HwSetRate(IntPtr pcm, IntPtr hw, uint rate, int dir);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_period_time_near")]
    public static partial int HwSetPeriodTimeNear(IntPtr pcm, IntPtr hw, ref uint us, IntPtr dir);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_set_buffer_time_near")]
    public static partial int HwSetBufferTimeNear(IntPtr pcm, IntPtr hw, ref uint us, IntPtr dir);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params")]
    public static partial int HwParamsApply(IntPtr pcm, IntPtr hw);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_params_can_pause")]
    public static partial int HwCanPause(IntPtr hw);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_hw_free")]
    public static partial int HwFree(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_format_physical_width")]
    public static partial int FormatPhysicalWidth(int format);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_writei")]
    public static unsafe partial nint WriteI(IntPtr pcm, byte* buffer, nuint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_wait")]
    public static partial int Wait(IntPtr pcm, int timeoutMs);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_recover")]
    public static partial int Recover(IntPtr pcm, int err, int silent);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_delay")]
    public static partial int Delay(IntPtr pcm, out nint frames);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_state")]
    public static partial int State(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_start")]
    public static partial int Start(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_drop")]
    public static partial int Drop(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_prepare")]
    public static partial int Prepare(IntPtr pcm);

    [LibraryImport(Lib, EntryPoint = "snd_pcm_pause")]
    public static partial int Pause(IntPtr pcm, int enable);

    [LibraryImport(Lib, EntryPoint = "snd_strerror")]
    private static partial IntPtr StrErrorPtr(int err);

    public static string StrError(int err)
    {
        try { return Marshal.PtrToStringUTF8(StrErrorPtr(err)) ?? ("error " + err); }
        catch (Exception) { return "error " + err; }
    }

    public static string FormatName(int f) => f switch
    {
        FormatS32Le => "S32_LE", FormatS24Le => "S24_LE", FormatS24_3Le => "S24_3LE", FormatS16Le => "S16_LE", _ => "format " + f
    };

    public static bool IsGone(int err) => err is ENODEV or EBADFD or ENOENT or ENXIO;
}

internal static partial class Libc
{
    private const int SchedFifo = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam { public int Priority; }

    [LibraryImport("libc", EntryPoint = "sched_setscheduler", SetLastError = true)]
    private static partial int SetScheduler(int pid, int policy, ref SchedParam param);

    [LibraryImport("libc", EntryPoint = "mkfifo", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MkFifo(string path, uint mode);

    // A named pipe (for Spotify Soloist's private sound server to write into).
    public static bool MakeFifo(string path) => MkFifo(path, 0x180 /* 0600 */) == 0;

    // The calling thread at a real-time priority where the system allows it
    // (root, or a container with SYS_NICE). Best effort.
    public static bool FavourThisThread()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var p = new SchedParam { Priority = 40 };
            return SetScheduler(0, SchedFifo, ref p) == 0;
        }
        catch (Exception) { return false; }
    }
}
