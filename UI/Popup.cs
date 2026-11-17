using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenShift.Core;
using ScreenShift.Native;

namespace ScreenShift.UI;

/// <summary>Positions borderless popups in physical pixels on the right monitor, without flicker (DWM cloak).</summary>
public static class Popup
{
    public delegate POINT Placer(RECT work, RECT bounds, int width, int height);

    public static async void ShowAt(Window w, POINT anchor, Placer place, bool activate)
    {
        w.ShowActivated = activate;
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int cloak = 1;
        Win32.DwmSetWindowAttribute(hwnd, 13 /*DWMWA_CLOAK*/, ref cloak, sizeof(int));
        Win32.RoundCorners(hwnd);

        var mon = Monitors.FromPoint(anchor) ?? Monitors.Enumerate().First();
        Win32.SetWindowPos(hwnd, 0, mon.Work.Left + 20, mon.Work.Top + 20, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        w.Show();

        // Let WPF finish layout (and any DPI-change resize) before measuring the real pixel size.
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        w.UpdateLayout();
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (!w.IsVisible) return;

        Win32.GetWindowRect(hwnd, out var r);
        var p = place(mon.Work, mon.Bounds, r.Width, r.Height);
        Win32.SetWindowPos(hwnd, 0, p.X, p.Y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

        cloak = 0;
        Win32.DwmSetWindowAttribute(hwnd, 13, ref cloak, sizeof(int));
        if (activate)
        {
            Win32.SetForegroundWindow(hwnd);
            w.Activate();
        }
    }

    public static POINT CursorPos() { Win32.GetCursorPos(out var p); return p; }

    public static POINT PrimaryCenter()
    {
        var m = Monitors.Enumerate().FirstOrDefault(x => x.Primary) ?? Monitors.Enumerate().First();
        return new POINT((m.Bounds.Left + m.Bounds.Right) / 2, (m.Bounds.Top + m.Bounds.Bottom) / 2);
    }

    public static POINT Center(RECT work, RECT _, int w, int h) =>
        new(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2);
}
