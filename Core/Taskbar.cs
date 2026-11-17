using ScreenShift.Native;
using static ScreenShift.Native.Win32;

namespace ScreenShift.Core;

/// <summary>Windows' own "Automatically hide the taskbar" setting, flipped through the documented AppBar API.</summary>
public static unsafe class Taskbar
{
    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10, ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

    public static bool AutoHide
    {
        get
        {
            var d = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
            return ((uint)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0;
        }
        set
        {
            var d = new APPBARDATA
            {
                cbSize = (uint)sizeof(APPBARDATA),
                hWnd = FindWindowW("Shell_TrayWnd", null),
                lParam = (nint)((value ? ABS_AUTOHIDE : 0) | ABS_ALWAYSONTOP),
            };
            SHAppBarMessage(ABM_SETSTATE, ref d);
            Persist(value);
        }
    }

    /// <summary>
    /// Explorer applies ABM_SETSTATE right away but only writes it to the registry when it exits, so a crash or
    /// power cut would bring the old setting back. Flip the same auto-hide bit Explorer uses (byte 8, bit 0).
    /// </summary>
    static void Persist(bool autoHide)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", writable: true);
            if (k?.GetValue("Settings") is not byte[] b || b.Length <= 8) return;
            byte nb = autoHide ? (byte)(b[8] | 1) : (byte)(b[8] & ~1);
            if (nb == b[8]) return;
            b[8] = nb;
            k.SetValue("Settings", b, Microsoft.Win32.RegistryValueKind.Binary);
        }
        catch (Exception ex) { Log.Write("Couldn't save the taskbar setting: " + ex.Message); }
    }
}
