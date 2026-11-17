using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScreenShift.Native;

[StructLayout(LayoutKind.Sequential)]
public struct LUID : IEquatable<LUID>
{
    public uint LowPart; public int HighPart;
    public readonly ulong Value => ((ulong)(uint)HighPart << 32) | LowPart;
    public static LUID From(ulong v) => new() { LowPart = (uint)v, HighPart = (int)(v >> 32) };
    public readonly bool Equals(LUID o) => LowPart == o.LowPart && HighPart == o.HighPart;
    public override readonly bool Equals(object? o) => o is LUID l && Equals(l);
    public override readonly int GetHashCode() => Value.GetHashCode();
}

[StructLayout(LayoutKind.Sequential)] public struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
[StructLayout(LayoutKind.Sequential)] public struct DISPLAYCONFIG_2DREGION { public uint cx, cy; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate; public DISPLAYCONFIG_RATIONAL hSyncFreq, vSyncFreq;
    public DISPLAYCONFIG_2DREGION activeSize, totalSize; public uint videoStandard, scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SOURCE_MODE { public uint width, height, pixelFormat; public POINT position; }

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DISPLAYCONFIG_MODE_INFO
{
    [FieldOffset(0)] public uint infoType;
    [FieldOffset(4)] public uint id;
    [FieldOffset(8)] public LUID adapterId;
    [FieldOffset(16)] public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
    public fixed char monitorFriendlyDeviceName[64];
    public fixed char monitorDevicePath[128];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public fixed char viewGdiDeviceName[32]; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAYCONFIG_ADAPTER_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public fixed char adapterDevicePath[128]; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_ADVANCED_COLOR_INFO { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint value, colorEncoding, bitsPerColorChannel; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_ADVANCED_COLOR_INFO_2 { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint value, colorEncoding, bitsPerColorChannel, activeColorMode; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SET_STATE { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint value; }

public static unsafe class DisplayConfigApi
{
    public const uint QDC_ALL_PATHS = 0x1, QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_TOPOLOGY_SUPPLIED = 0x10, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20,
        SDC_VALIDATE = 0x40, SDC_APPLY = 0x80, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400,
        SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;
    public const uint PATH_ACTIVE = 0x1, MODE_IDX_INVALID = 0xFFFFFFFF;
    public const uint MODE_SOURCE = 1, MODE_TARGET = 2;
    const uint GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2, GET_ADAPTER_NAME = 4,
        GET_ADVANCED_COLOR_INFO = 9, SET_ADVANCED_COLOR_STATE = 10, GET_ADVANCED_COLOR_INFO_2 = 15, SET_HDR_STATE = 16;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, nint topology);
    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, DISPLAYCONFIG_PATH_INFO[]? paths, uint numModes, DISPLAYCONFIG_MODE_INFO[]? modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(void* packet);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(void* packet);

    public static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Query(uint flags)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int err = GetDisplayConfigBufferSizes(flags, out uint np, out uint nm);
            if (err != 0) throw new Win32Exception(err);
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            err = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, 0);
            if (err == 0)
            {
                if (paths.Length != np) Array.Resize(ref paths, (int)np);
                if (modes.Length != nm) Array.Resize(ref modes, (int)nm);
                return (paths, modes);
            }
            if (err != ERROR_INSUFFICIENT_BUFFER) throw new Win32Exception(err);
        }
        throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER, "Display configuration kept changing while reading it.");
    }

    public static int Set(DISPLAYCONFIG_PATH_INFO[]? paths, DISPLAYCONFIG_MODE_INFO[]? modes, uint flags)
        => SetDisplayConfig((uint)(paths?.Length ?? 0), paths is { Length: > 0 } ? paths : null,
                            (uint)(modes?.Length ?? 0), modes is { Length: > 0 } ? modes : null, flags);

    public static (string Friendly, string DevicePath) GetTargetName(LUID adapter, uint targetId)
    {
        var n = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        n.header = new() { type = GET_TARGET_NAME, size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME), adapterId = adapter, id = targetId };
        if (DisplayConfigGetDeviceInfo(&n) != 0) return ("", "");
        return (new string(n.monitorFriendlyDeviceName), new string(n.monitorDevicePath));
    }

    public static string GetSourceGdiName(LUID adapter, uint sourceId)
    {
        var n = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        n.header = new() { type = GET_SOURCE_NAME, size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME), adapterId = adapter, id = sourceId };
        return DisplayConfigGetDeviceInfo(&n) == 0 ? new string(n.viewGdiDeviceName) : "";
    }

    public static string GetAdapterPath(LUID adapter)
    {
        var n = new DISPLAYCONFIG_ADAPTER_NAME();
        n.header = new() { type = GET_ADAPTER_NAME, size = (uint)sizeof(DISPLAYCONFIG_ADAPTER_NAME), adapterId = adapter };
        return DisplayConfigGetDeviceInfo(&n) == 0 ? new string(n.adapterDevicePath) : "";
    }

    /// <summary>Returns (supported, enabled) for HDR. Uses the Windows 11 24H2+ API when available.</summary>
    public static (bool Supported, bool Enabled) GetHdr(LUID adapter, uint targetId)
    {
        var i2 = new DISPLAYCONFIG_ADVANCED_COLOR_INFO_2();
        i2.header = new() { type = GET_ADVANCED_COLOR_INFO_2, size = (uint)sizeof(DISPLAYCONFIG_ADVANCED_COLOR_INFO_2), adapterId = adapter, id = targetId };
        if (DisplayConfigGetDeviceInfo(&i2) == 0)
            return ((i2.value & (1u << 4)) != 0, (i2.value & (1u << 5)) != 0); // highDynamicRangeSupported / UserEnabled

        var i = new DISPLAYCONFIG_ADVANCED_COLOR_INFO();
        i.header = new() { type = GET_ADVANCED_COLOR_INFO, size = (uint)sizeof(DISPLAYCONFIG_ADVANCED_COLOR_INFO), adapterId = adapter, id = targetId };
        if (DisplayConfigGetDeviceInfo(&i) == 0)
            return ((i.value & 1) != 0, (i.value & 2) != 0);
        return (false, false);
    }

    public static bool SetHdr(LUID adapter, uint targetId, bool enable)
    {
        var s = new DISPLAYCONFIG_SET_STATE();
        s.header = new() { type = SET_HDR_STATE, size = (uint)sizeof(DISPLAYCONFIG_SET_STATE), adapterId = adapter, id = targetId };
        s.value = enable ? 1u : 0u;
        if (DisplayConfigSetDeviceInfo(&s) == 0) return true;
        s.header.type = SET_ADVANCED_COLOR_STATE;
        return DisplayConfigSetDeviceInfo(&s) == 0;
    }

    public static byte[] ToBytes<T>(T[] arr) where T : unmanaged => MemoryMarshal.AsBytes(arr.AsSpan()).ToArray();

    public static T[] FromBytes<T>(byte[] data) where T : unmanaged
    {
        if (data.Length % sizeof(T) != 0) throw new InvalidDataException("Corrupted display data in profile.");
        return MemoryMarshal.Cast<byte, T>(data).ToArray();
    }
}
