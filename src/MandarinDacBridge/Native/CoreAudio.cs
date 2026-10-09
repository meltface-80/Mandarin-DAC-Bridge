// CoreAudio.cs — the Mac's audio devices: listing them, hog mode (exclusive
// access), the physical format (which sets the DAC's rate and depth) and the
// IOProc the HAL calls for samples.
using System.Runtime.InteropServices;
using System.Text;

namespace MandarinDacBridge.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyAddress(uint selector, uint scope, uint element = 0)
{
    public uint Selector = selector;
    public uint Scope = scope;
    public uint Element = element;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioStreamBasicDescription
{
    public double SampleRate;
    public uint FormatId;
    public uint FormatFlags;
    public uint BytesPerPacket;
    public uint FramesPerPacket;
    public uint BytesPerFrame;
    public uint ChannelsPerFrame;
    public uint BitsPerChannel;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioValueRange
{
    public double Minimum;
    public double Maximum;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioStreamRangedDescription
{
    public AudioStreamBasicDescription Format;
    public AudioValueRange SampleRateRange;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioBuffer
{
    public uint NumberChannels;
    public uint DataByteSize;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioBufferList
{
    public uint NumberBuffers;
    public AudioBuffer First;   // NumberBuffers of these, one after another
}

internal static unsafe partial class CoreAudio
{
    private const string Lib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint SystemObject = 1;
    public const uint Unknown = 0;

    public static uint Code(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    public static readonly uint ScopeGlobal = Code("glob");
    public static readonly uint ScopeOutput = Code("outp");

    public static readonly uint HardwareDevices = Code("dev#");
    public static readonly uint ObjectName = Code("lnam");
    public static readonly uint ObjectManufacturer = Code("lmak");
    public static readonly uint DeviceUid = Code("uid ");
    public static readonly uint DeviceTransportType = Code("tran");
    public static readonly uint DeviceIsAlive = Code("livn");
    public static readonly uint DeviceStreamConfiguration = Code("slay");
    public static readonly uint DeviceStreams = Code("stm#");
    public static readonly uint DeviceHogMode = Code("oink");
    public static readonly uint DeviceNominalSampleRate = Code("nsrt");
    public static readonly uint DeviceAvailableNominalSampleRates = Code("nsr#");
    public static readonly uint DeviceVolumeScalar = Code("volm");
    public static readonly uint StreamAvailablePhysicalFormats = Code("pft?");
    public static readonly uint StreamPhysicalFormat = Code("pft ");
    public static readonly uint StreamVirtualFormat = Code("sfmt");

    public static readonly uint FormatLinearPcm = Code("lpcm");
    public const uint FlagIsFloat = 1, FlagIsPacked = 8, FlagIsNonInterleaved = 32;

    public static readonly uint TransportUsb = Code("usb ");

    [LibraryImport(Lib, EntryPoint = "AudioObjectGetPropertyData")]
    public static partial int GetPropertyData(uint obj, in PropertyAddress address, uint qualifierSize, void* qualifier, ref uint size, void* data);

    [LibraryImport(Lib, EntryPoint = "AudioObjectSetPropertyData")]
    public static partial int SetPropertyData(uint obj, in PropertyAddress address, uint qualifierSize, void* qualifier, uint size, void* data);

    [LibraryImport(Lib, EntryPoint = "AudioObjectGetPropertyDataSize")]
    public static partial int GetPropertyDataSize(uint obj, in PropertyAddress address, uint qualifierSize, void* qualifier, out uint size);

    [LibraryImport(Lib, EntryPoint = "AudioObjectHasProperty")]
    public static partial byte HasProperty(uint obj, in PropertyAddress address);

    [LibraryImport(Lib, EntryPoint = "AudioDeviceCreateIOProcID")]
    public static partial int CreateIOProcId(uint device,
        delegate* unmanaged<uint, void*, AudioBufferList*, void*, AudioBufferList*, void*, void*, int> proc, void* clientData, out IntPtr procId);

    [LibraryImport(Lib, EntryPoint = "AudioDeviceDestroyIOProcID")]
    public static partial int DestroyIOProcId(uint device, IntPtr procId);

    [LibraryImport(Lib, EntryPoint = "AudioDeviceStart")]
    public static partial int DeviceStart(uint device, IntPtr procId);

    [LibraryImport(Lib, EntryPoint = "AudioDeviceStop")]
    public static partial int DeviceStop(uint device, IntPtr procId);

    [LibraryImport(CF, EntryPoint = "CFStringGetCString")]
    private static partial byte CFStringGetCString(IntPtr s, byte* buffer, nint size, uint encoding);

    [LibraryImport(CF, EntryPoint = "CFRelease")]
    private static partial void CFRelease(IntPtr obj);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "proc_pidpath")]
    private static partial int ProcPidPath(int pid, byte* buffer, uint size);

    // ---------------------------------------------------------------- reading

    public static T? Get<T>(uint obj, uint selector, uint scope, uint element = 0) where T : unmanaged
    {
        T v = default;
        uint size = (uint)sizeof(T);
        var a = new PropertyAddress(selector, scope, element);
        return GetPropertyData(obj, in a, 0, null, ref size, &v) == 0 ? v : null;
    }

    public static int Set<T>(uint obj, uint selector, uint scope, T value) where T : unmanaged
    {
        var a = new PropertyAddress(selector, scope);
        return SetPropertyData(obj, in a, 0, null, (uint)sizeof(T), &value);
    }

    // A property that is an array of T.
    public static T[] GetArray<T>(uint obj, uint selector, uint scope) where T : unmanaged
    {
        var a = new PropertyAddress(selector, scope);
        if (HasProperty(obj, in a) == 0) return [];
        if (GetPropertyDataSize(obj, in a, 0, null, out var size) != 0 || size < sizeof(T)) return [];
        var arr = new T[size / sizeof(T)];
        fixed (T* p = arr)
        {
            if (GetPropertyData(obj, in a, 0, null, ref size, p) != 0) return [];
        }
        return arr;
    }

    public static string GetString(uint obj, uint selector)
    {
        IntPtr s = IntPtr.Zero;
        uint size = (uint)sizeof(IntPtr);
        var a = new PropertyAddress(selector, ScopeGlobal);
        if (GetPropertyData(obj, in a, 0, null, ref size, &s) != 0 || s == IntPtr.Zero) return "";
        try
        {
            var buf = stackalloc byte[1024];
            return CFStringGetCString(s, buf, 1024, 0x08000100 /* UTF-8 */) != 0 ? Marshal.PtrToStringUTF8((IntPtr)buf) ?? "" : "";
        }
        finally { CFRelease(s); }
    }

    public static uint[] Devices() => GetArray<uint>(SystemObject, HardwareDevices, ScopeGlobal);

    public static uint FindDevice(string uid)
    {
        foreach (var d in Devices()) if (GetString(d, DeviceUid) == uid) return d;
        return Unknown;
    }

    public static int OutputChannels(uint dev)
    {
        var a = new PropertyAddress(DeviceStreamConfiguration, ScopeOutput);
        if (GetPropertyDataSize(dev, in a, 0, null, out var size) != 0 || size < 8) return 0;
        var mem = NativeMemory.AllocZeroed(size);
        try
        {
            if (GetPropertyData(dev, in a, 0, null, ref size, mem) != 0) return 0;
            var list = (AudioBufferList*)mem;
            var buffers = &list->First;
            int n = 0;
            for (int i = 0; i < list->NumberBuffers; i++) n += (int)buffers[i].NumberChannels;
            return n;
        }
        finally { NativeMemory.Free(mem); }
    }

    public static uint FirstOutputStream(uint dev)
    {
        var s = GetArray<uint>(dev, DeviceStreams, ScopeOutput);
        return s.Length > 0 ? s[0] : Unknown;
    }

    public static int HogOwner(uint dev) => Get<int>(dev, DeviceHogMode, ScopeGlobal) ?? -1;

    public static double NominalRate(uint dev) => Get<double>(dev, DeviceNominalSampleRate, ScopeGlobal) ?? 0;

    public static string FourCc(uint v)
    {
        var s = new StringBuilder(4);
        for (int i = 3; i >= 0; i--)
        {
            var c = (char)((v >> (i * 8)) & 0xFF);
            s.Append(c is >= ' ' and <= '~' ? c : '?');
        }
        return s.ToString().TrimEnd();
    }

    public static string ProcessPath(int pid)
    {
        if (pid <= 0) return "";
        try
        {
            var buf = stackalloc byte[4096];
            int n = ProcPidPath(pid, buf, 4096);
            return n > 0 ? Encoding.UTF8.GetString(buf, n) : "";
        }
        catch (Exception) { return ""; }
    }
}
