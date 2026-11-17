using System.Runtime.InteropServices;

namespace ScreenShift.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nuint dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct MONITORINFOEXW
{
    public int cbSize;
    public RECT rcMonitor, rcWork;
    public uint dwFlags;
    public fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID, uFlags, uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

public static unsafe class Win32
{
    public const int WH_MOUSE_LL = 14;
    public const uint WM_DESTROY = 0x0002, WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E,
        WM_TIMER = 0x0113, WM_HOTKEY = 0x0312, WM_MOUSEMOVE = 0x0200, WM_CONTEXTMENU = 0x007B,
        WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_APP = 0x8000, WM_USER = 0x0400,
        WM_DEVICECHANGE = 0x0219;
    public const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
    public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const int GWL_EXSTYLE = -20;
    public const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn, nint hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetClipCursor(out RECT r);

    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern nint SendMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] public static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32.dll")] public static extern bool KillTimer(nint hwnd, nuint id);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> cb, nint data);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(nint hmon, ref MONITORINFOEXW info);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint hmon, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetWindowLongPtrW(nint hwnd, int idx);
    [DllImport("user32.dll")] public static extern nint SetWindowLongPtrW(nint hwnd, int idx, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint h);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int idx, uint dpi);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();

    [DllImport("shell32.dll")] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    // ---- black-out overlay / idle detection
    public const uint WS_POPUP = 0x80000000, WS_EX_LAYERED = 0x80000;
    public const uint WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    public const uint SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [StructLayout(LayoutKind.Sequential)] public struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern nint SetCursor(nint cursor);
    [DllImport("gdi32.dll")] public static extern nint GetStockObject(int obj);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint index, out XINPUT_STATE state);

    // ---- taskbar
    [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public uint cbSize; public nint hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public nint lParam; }
    [DllImport("shell32.dll")] public static extern nuint SHAppBarMessage(uint msg, ref APPBARDATA data);

    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(nint proc, nint min, nint max);

    public static int LoWord(nint v) => (short)((long)v & 0xFFFF);
    public static int HiWord(nint v) => (short)(((long)v >> 16) & 0xFFFF);

    /// <summary>Rounded corners on Windows 11 for borderless WPF windows.</summary>
    public static void RoundCorners(nint hwnd, bool small = false)
    {
        int pref = small ? 3 : 2; // DWMWCP_ROUNDSMALL / DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
    }

    public static void AddExStyle(nint hwnd, uint style)
    {
        var cur = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (nint)((long)cur | style));
    }
}
