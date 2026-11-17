using System.Runtime.InteropServices;
using ScreenShift.Native;
using static ScreenShift.Native.Win32;

namespace ScreenShift.Core;

/// <summary>
/// Background thread that owns: a hidden window (display-change + hotkey + tray messages),
/// the low-level mouse hook, and the tray icon. Keeping these off the UI thread means the
/// mouse never stutters while the settings window is rendering or a display switch is running.
/// </summary>
public static unsafe class Engine
{
    public const string WindowClass = "ScreenShift.Engine.v1";
    public const uint MsgShowMain = WM_APP + 5;
    const uint MsgSetHotkeys = WM_APP + 1, MsgBridge = WM_APP + 2, MsgRefresh = WM_APP + 3, MsgTray = WM_APP + 10, MsgTrayTip = WM_APP + 11;
    const nuint TimerWatchdog = 1, TimerRefresh = 2;

    static Thread? _thread;
    static nint _hwnd;
    static uint _taskbarCreated;
    static nint _trayIcon;
    static string _trayTip = "ScreenShift";

    static bool _bridgeWanted;
    static int _monitorCount;
    static POINT _wdPos; static int _wdEvents; static long _lastDisplayChange;

    static readonly object HotkeyGate = new();
    static List<(int Id, Hotkey Key)> _pendingHotkeys = [];
    static readonly List<int> _registered = [];
    public static HashSet<int> FailedHotkeys { get; private set; } = [];

    // Events raised on the ENGINE thread. Subscribers must marshal to the UI thread.
    public static event Action<int>? HotkeyPressed;
    public static event Action<POINT, bool>? TrayActivated;   // anchor, isDoubleClick
    public static event Action? DisplaysChanged;
    public static event Action? ShowMainRequested;

    public static void Start()
    {
        var ready = new ManualResetEventSlim();   // not disposed: the engine thread may set it after a timeout
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "ScreenShift engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(5000);
    }

    public static void Stop()
    {
        if (_hwnd != 0) SendMessageW(_hwnd, WM_APP + 99, 0, 0);
        _thread?.Join(2000);
    }

    public static void SetHotkeys(List<(int Id, Hotkey Key)> keys)
    {
        lock (HotkeyGate) _pendingHotkeys = keys;
        if (_hwnd != 0) SendMessageW(_hwnd, MsgSetHotkeys, 0, 0);
    }

    public static void SetBridge(bool enabled, BridgeMode mode)
    {
        CursorBridge.Mode = (int)mode;
        if (_hwnd != 0) SendMessageW(_hwnd, MsgBridge, enabled ? 1 : 0, 0);
    }

    public static void RefreshLayout() { if (_hwnd != 0) PostMessageW(_hwnd, MsgRefresh, 0, 0); }

    public static void SetTrayTip(string tip)
    {
        _trayTip = tip.Length > 120 ? tip[..120] : tip;
        if (_hwnd != 0) PostMessageW(_hwnd, MsgTrayTip, 0, 0);
    }

    static void Run(ManualResetEventSlim ready)
    {
        try
        {
            var inst = GetModuleHandleW(null);
            fixed (char* cls = WindowClass)
            {
                var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = inst, lpszClassName = cls };
                RegisterClassExW(ref wc);
            }
            // Hidden top-level window (not message-only) so it receives WM_DISPLAYCHANGE broadcasts.
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, WindowClass, "ScreenShift", 0, 0, 0, 0, 0, 0, 0, inst, 0);
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _monitorCount = CursorBridge.UpdateLayout();
            AddTray();
        }
        catch (Exception ex) { Log.Write("Engine start failed: " + ex); }
        finally { ready.Set(); }

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        try { if (Handle(hwnd, msg, w, l, out var r)) return r; }
        catch (Exception ex) { Log.Write($"Engine message {msg:X} failed: {ex}"); }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    static bool Handle(nint hwnd, uint msg, nint w, nint l, out nint result)
    {
        result = 0;
        switch (msg)
        {
            case WM_HOTKEY:
                HotkeyPressed?.Invoke((int)w);
                return true;

            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE:
            case MsgRefresh:
                if (msg == WM_DISPLAYCHANGE) { CursorBridge.Suspended = true; _lastDisplayChange = Environment.TickCount64; }   // layout is in flux; resume after the debounce
                SetTimer(hwnd, TimerRefresh, 400, 0);          // debounce bursts of change messages
                return msg == MsgRefresh;

            case WM_TIMER when w == (nint)TimerRefresh:
                KillTimer(hwnd, TimerRefresh);
                _monitorCount = CursorBridge.UpdateLayout();
                CursorBridge.Suspended = false;
                UpdateHookState();
                DisplaysChanged?.Invoke();
                return true;

            case WM_TIMER when w == (nint)TimerWatchdog:
                // Windows silently removes a low-level hook if it ever times out. Detect & reinstall.
                GetCursorPos(out var p);
                int ev = CursorBridge.EventCount;
                // Skip right after display changes: Windows moves the cursor itself then, without mouse input.
                bool settling = CursorBridge.Suspended || Environment.TickCount64 - _lastDisplayChange < 10_000;
                if (CursorBridge.Installed && !settling && (p.X != _wdPos.X || p.Y != _wdPos.Y) && ev == _wdEvents)
                {
                    Log.Write("Mouse hook stopped receiving events; reinstalling.");
                    CursorBridge.Uninstall();
                    CursorBridge.Install();
                }
                _wdPos = p; _wdEvents = CursorBridge.EventCount;
                return true;

            case MsgSetHotkeys:
                ApplyHotkeys();
                return true;

            case MsgBridge:
                _bridgeWanted = w != 0;
                UpdateHookState();
                return true;

            case MsgTrayTip:
                UpdateTrayTip();
                return true;

            case MsgTray:
                int ev2 = LoWord(l);
                var anchor = new POINT(LoWord(w), HiWord(w));
                if (ev2 is (int)NIN_SELECT or (int)NIN_KEYSELECT or (int)WM_CONTEXTMENU)
                    TrayActivated?.Invoke(anchor, false);
                else if (ev2 == (int)WM_LBUTTONDBLCLK)
                    TrayActivated?.Invoke(anchor, true);
                return true;

            case MsgShowMain:
                ShowMainRequested?.Invoke();
                return true;

            case WM_APP + 99:
                foreach (var id in _registered) UnregisterHotKey(hwnd, id);
                _registered.Clear();
                KillTimer(hwnd, TimerWatchdog);
                CursorBridge.Uninstall();
                RemoveTray();
                DestroyWindow(hwnd);
                return true;

            case WM_DESTROY:
                _hwnd = 0;
                PostQuitMessage(0);
                return true;
        }

        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddTray();   // Explorer restarted: put the icon back
            return true;
        }
        return false;
    }

    static void UpdateHookState()
    {
        bool want = _bridgeWanted && _monitorCount >= 2;
        if (want && !CursorBridge.Installed)
        {
            CursorBridge.Install();
            GetCursorPos(out _wdPos); _wdEvents = CursorBridge.EventCount;
            SetTimer(_hwnd, TimerWatchdog, 3000, 0);
        }
        else if (!want && CursorBridge.Installed)
        {
            CursorBridge.Uninstall();
            KillTimer(_hwnd, TimerWatchdog);
        }
    }

    static void ApplyHotkeys()
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        List<(int Id, Hotkey Key)> keys;
        lock (HotkeyGate) keys = _pendingHotkeys;
        var failed = new HashSet<int>();
        foreach (var (id, k) in keys)
        {
            if (k.Key == 0) continue;
            if (RegisterHotKey(_hwnd, id, k.Modifiers | MOD_NOREPEAT, k.Key)) _registered.Add(id);
            else failed.Add(id);
        }
        FailedHotkeys = failed;
    }

    // ---------------- tray icon ----------------

    static void AddTray()
    {
        if (_trayIcon == 0)
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
            uint dpi = GetDpiForSystem();
            int size = GetSystemMetricsForDpi(49 /*SM_CXSMICON*/, dpi);
            _trayIcon = LoadImageW(0, ico, 1 /*IMAGE_ICON*/, size, size, 0x10 /*LR_LOADFROMFILE*/);
        }
        var d = NewTrayData();
        d.uFlags = 0x1 | 0x2 | 0x4 | 0x80; // MESSAGE | ICON | TIP | SHOWTIP
        Shell_NotifyIconW(2 /*NIM_DELETE*/, ref d);   // in case of a stale icon
        Shell_NotifyIconW(0 /*NIM_ADD*/, ref d);
        d.uVersion = 4;
        Shell_NotifyIconW(4 /*NIM_SETVERSION*/, ref d);
    }

    static void UpdateTrayTip()
    {
        var d = NewTrayData();
        d.uFlags = 0x4 | 0x80;
        Shell_NotifyIconW(1 /*NIM_MODIFY*/, ref d);
    }

    static void RemoveTray()
    {
        var d = NewTrayData();
        Shell_NotifyIconW(2, ref d);
        if (_trayIcon != 0) { DestroyIcon(_trayIcon); _trayIcon = 0; }
    }

    static NOTIFYICONDATAW NewTrayData()
    {
        var d = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _hwnd, uID = 1, uCallbackMessage = MsgTray, hIcon = _trayIcon };
        var tip = _trayTip.AsSpan(0, Math.Min(_trayTip.Length, 127));
        tip.CopyTo(new Span<char>(d.szTip, 128));
        return d;
    }
}
