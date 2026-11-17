using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ScreenShift.Native;

namespace ScreenShift.Core;

public sealed record LiveMonitor(nint Handle, string GdiName, string Name, RECT Bounds, RECT Work, double Scale, bool Primary)
{
    public int LogicalWidth => (int)Math.Round(Bounds.Width / Scale);
    public int LogicalHeight => (int)Math.Round(Bounds.Height / Scale);
}

public static unsafe class Monitors
{
    /// <summary>Enumerates the active monitors with their real pixel bounds and scaling factor.</summary>
    public static List<LiveMonitor> Enumerate(bool withNames = false)
    {
        var handles = new List<nint>(8);
        var gc = GCHandle.Alloc(handles);
        try { Win32.EnumDisplayMonitors(0, 0, &Callback, GCHandle.ToIntPtr(gc)); }
        finally { gc.Free(); }

        Dictionary<string, string>? names = withNames ? GdiToFriendly() : null;
        var list = new List<LiveMonitor>(handles.Count);
        foreach (var h in handles)
        {
            var mi = new MONITORINFOEXW { cbSize = sizeof(MONITORINFOEXW) };
            if (!Win32.GetMonitorInfoW(h, ref mi)) continue;
            double scale = Win32.GetDpiForMonitor(h, 0, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;
            string gdi = new string(mi.szDevice);
            string name = names != null && names.TryGetValue(gdi, out var n) ? n : gdi.Replace(@"\\.\", "");
            list.Add(new LiveMonitor(h, gdi, name, mi.rcMonitor, mi.rcWork, scale, (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0));
        }
        return list;
    }

    public static LiveMonitor? FromPoint(POINT p)
    {
        var h = Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST);
        return Enumerate().FirstOrDefault(m => m.Handle == h);
    }

    static Dictionary<string, string> GdiToFriendly()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (paths, _) = DisplayConfigApi.Query(DisplayConfigApi.QDC_ONLY_ACTIVE_PATHS);
            foreach (var p in paths)
            {
                var gdi = DisplayConfigApi.GetSourceGdiName(p.sourceInfo.adapterId, p.sourceInfo.id);
                var name = DisplayConfigApi.GetTargetName(p.targetInfo.adapterId, p.targetInfo.id).Friendly;
                if (gdi.Length > 0 && name.Length > 0) map.TryAdd(gdi, name);
            }
        }
        catch (Exception ex) { Log.Write("Name lookup failed: " + ex.Message); }
        return map;
    }

    [UnmanagedCallersOnly]
    static int Callback(nint hmon, nint hdc, RECT* rc, nint data)
    {
        try { ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(hmon); } catch { }
        return 1;
    }
}
