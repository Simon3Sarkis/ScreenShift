using System.Runtime.InteropServices;

namespace ScreenShift.Core;

/// <summary>Is sound coming out of the default speakers right now? (Windows' own peak meter, no hooks, no admin.)</summary>
public static class AudioMeter
{
    public static float Peak()
    {
        IMMDeviceEnumerator? en = null; IMMDevice? dev = null; object? meterObj = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out dev) != 0 || dev == null) return 0;
            var iid = typeof(IAudioMeterInformation).GUID;
            if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, 0, out meterObj) != 0 || meterObj is not IAudioMeterInformation meter) return 0;
            return meter.GetPeakValue(out float v) == 0 ? v : 0;
        }
        catch { return 0; }
        finally
        {
            if (meterObj != null) Marshal.ReleaseComObject(meterObj);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
