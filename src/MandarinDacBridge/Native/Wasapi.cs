// Wasapi.cs — the few Windows Core Audio (MMDevice + WASAPI) interfaces the
// bridge uses, called directly through source-generated COM interop (no
// reflection: Native AOT). Method order is the vtable order of
// mmdeviceapi.h, propsys.h and audioclient.h.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace MandarinDacBridge.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid fmtid, uint pid)
{
    public Guid FmtId = fmtid;
    public uint Pid = pid;
}

// PROPVARIANT: the type, and the first pointer-sized member of the union (enough for strings and integers).
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Vt;
    public ushort R1, R2, R3;
    public nint P;
    public nint P2;
}

// WAVEFORMATEXTENSIBLE (packed: 40 bytes).
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort CbSize;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;

    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");

    // Integer PCM: a container of 16, 24 or 32 bits, with 16, 24 or 32 of them used.
    public static WaveFormatExtensible Of(int rate, int channels, int container, int valid) => new()
    {
        FormatTag = 0xFFFE, Channels = (ushort)channels, SamplesPerSec = (uint)rate,
        BlockAlign = (ushort)(channels * container / 8), AvgBytesPerSec = (uint)(rate * channels * container / 8),
        BitsPerSample = (ushort)container, CbSize = 22, ValidBitsPerSample = (ushort)valid,
        ChannelMask = channels switch { 1 => 0x4, 2 => 0x3, 4 => 0x33, 6 => 0x3F, 8 => 0x63F, _ => 0 }, SubFormat = Pcm
    };
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice(string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, nint activationParams, out nint iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
    [PreserveSig] int GetId(out nint id);
    [PreserveSig] int GetState(out uint state);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, nint format, nint closestMatch);
    [PreserveSig] int GetMixFormat(out nint format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint handle);
    [PreserveSig] int GetService(ref Guid iid, out nint service);
}

[GeneratedComInterface]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out nint data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

[GeneratedComInterface]
[Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7")]
internal partial interface IAudioClock
{
    [PreserveSig] int GetFrequency(out ulong frequency);
    [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
    [PreserveSig] int GetCharacteristics(out uint characteristics);
}

internal static partial class Wasapi
{
    public const int Render = 0;                    // eRender
    public const uint StateActive = 1;              // DEVICE_STATE_ACTIVE
    public const uint ClsCtxAll = 0x17;             // CLSCTX_ALL
    public const int ShareExclusive = 1;            // AUDCLNT_SHAREMODE_EXCLUSIVE
    public const uint FlagEventCallback = 0x40000;  // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    public const uint BufferSilent = 2;             // AUDCLNT_BUFFERFLAGS_SILENT

    public const int UnsupportedFormat = unchecked((int)0x88890008);
    public const int DeviceInUse = unchecked((int)0x8889000A);
    public const int ExclusiveNotAllowed = unchecked((int)0x8889000E);
    public const int BufferSizeNotAligned = unchecked((int)0x88890019);
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    public static readonly Guid ClsidEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IidAudioClock = new("CD63314F-3FBA-4a1b-812C-EF96358728E7");

    private static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    public static readonly PropertyKey FriendlyName = new(DeviceProps, 14);           // "Speakers (Topping D90)"
    public static readonly PropertyKey DeviceDesc = new(DeviceProps, 2);              // "Speakers"
    public static readonly PropertyKey EnumeratorName = new(DeviceProps, 24);         // "USB"
    public static readonly PropertyKey InterfaceName = new(new("026e516e-b814-414b-83cd-856d6fef4822"), 2);   // "Topping D90"
    public static readonly PropertyKey InstanceId = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);   // "SWD\MMDEVAPI\…" or "USB\VID_…"

    private static readonly StrategyBasedComWrappers Wrappers = new();

    [LibraryImport("ole32")] private static partial int CoInitializeEx(nint reserved, uint coInit);
    [LibraryImport("ole32")] private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint clsCtx, ref Guid iid, out nint obj);
    [LibraryImport("ole32")] public static partial void CoTaskMemFree(nint p);
    [LibraryImport("ole32")] public static partial int PropVariantClear(ref PropVariant pv);
    [LibraryImport("avrt", StringMarshalling = StringMarshalling.Utf16)] private static partial nint AvSetMmThreadCharacteristicsW(string task, ref uint index);

    // COM on this thread (multi-threaded: the audio interfaces are free-threaded). Harmless if already.
    public static void Init() => CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);

    public static T Wrap<T>(nint p) where T : class
    {
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(p); }
    }

    public static IMMDeviceEnumerator Enumerator()
    {
        Init();
        var clsid = ClsidEnumerator;
        var iid = IidEnumerator;
        Check(CoCreateInstance(ref clsid, 0, ClsCtxAll, ref iid, out var p), "the audio device list");
        return Wrap<IMMDeviceEnumerator>(p);
    }

    public static IAudioClient Activate(IMMDevice device)
    {
        var iid = IidAudioClient;
        Check(device.Activate(ref iid, ClsCtxAll, 0, out var p), "the DAC");
        return Wrap<IAudioClient>(p);
    }

    public static T Service<T>(IAudioClient client, Guid iid) where T : class
    {
        Check(client.GetService(ref iid, out var p), typeof(T).Name);
        return Wrap<T>(p);
    }

    public static string Id(IMMDevice d)
    {
        if (d.GetId(out var p) != 0 || p == 0) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { CoTaskMemFree(p); }
    }

    public static string Text(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) != 0) return "";
        try { return v.Vt == 31 /* VT_LPWSTR */ && v.P != 0 ? Marshal.PtrToStringUni(v.P) ?? "" : ""; }
        finally { PropVariantClear(ref v); }
    }

    // The thread that feeds the DAC, at the priority Windows gives pro audio.
    public static void ProAudioThread()
    {
        try { uint i = 0; AvSetMmThreadCharacteristicsW("Pro Audio", ref i); } catch (Exception) { /* best effort */ }
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new Audio.DeviceException($"{what}: {Describe(hr)}");
    }

    public static string Describe(int hr) => hr switch
    {
        DeviceInUse => "another program is using the DAC in exclusive mode",
        ExclusiveNotAllowed => "Windows doesn't allow exclusive mode for this DAC (Sound settings → the DAC → Properties → Advanced → Allow applications to take exclusive control)",
        UnsupportedFormat => "the DAC doesn't take that format",
        DeviceInvalidated => "the DAC is gone",
        _ => $"error 0x{hr:X8}"
    };
}
