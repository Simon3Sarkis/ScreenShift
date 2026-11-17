using ScreenShift.Native;
using static ScreenShift.Native.Win32;

namespace ScreenShift.Core;

/// <summary>
/// OLED care: covers chosen screens with pure black (OLED pixels fully off) after an idle timeout or on demand.
/// Runs on the engine thread. Uses plain Win32 popup windows, no hooks: activity is detected with
/// GetLastInputInfo (mouse + keyboard) and XInput packet numbers (game controllers).
/// The display signal is never touched, so there is no re-sync, no HDR flicker and no black-screen risk.
/// </summary>
public static unsafe class Blackout
{
    const string ClassName = "ScreenShift.Blackout.v1";
    public const nuint TimerIdle = 3, TimerActive = 4;
    const int IdlePollMs = 1000, ActivePollMs = 100, FadeMs = 900, GraceMs = 900;

    // Configuration: written from the UI thread, read on the engine thread.
    static volatile bool _idleEnabled, _skipWhenBusy = true;
    static volatile int _timeoutMs = 5 * 60_000;
    static volatile string[] _monitors = [];   // device paths; empty = every screen

    static nint _engine, _inst;
    static readonly List<nint> _windows = [];
    static long _lastSoundAt = long.MinValue / 2, _shownAt, _graceUntil, _baseline, _triggeredFor = -1, _loggedBusyFor = -1;
    static bool _fading;
    static int _ticks;
    static readonly XINPUT_STATE[] _padStates = new XINPUT_STATE[4];
    static readonly bool[] _padConnected = new bool[4];
    static long _padActivity, _padScanAt;
    static bool _xinputMissing;

    public static bool IsActive => _windows.Count > 0;

    public static void Configure(bool idleEnabled, int minutes, IEnumerable<string> monitors, bool skipWhenBusy)
    {
        _idleEnabled = idleEnabled;
        _timeoutMs = Math.Clamp(minutes, 1, 240) * 60_000;
        _monitors = monitors.ToArray();
        _skipWhenBusy = skipWhenBusy;
    }

    /// <summary>Engine thread: register the window class.</summary>
    public static void Init(nint engineHwnd)
    {
        _engine = engineHwnd;
        _inst = GetModuleHandleW(null);
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = _inst, lpszClassName = cls,
                hbrBackground = GetStockObject(4 /*BLACK_BRUSH*/),
            };
            RegisterClassExW(ref wc);
        }
    }

    /// <summary>Engine thread: (re)start or stop the idle timer after a settings change.</summary>
    public static void ApplyConfig()
    {
        if (_idleEnabled) { _triggeredFor = -1; SetTimer(_engine, TimerIdle, IdlePollMs, 0); }
        else KillTimer(_engine, TimerIdle);
    }

    public static void Toggle()
    {
        if (IsActive) Hide("hotkey");
        else Show(fade: false, reason: "hotkey");
    }

    public static void OnTimer(nuint id)
    {
        if (id == TimerIdle) IdleTick();
        else if (id == TimerActive) ActiveTick();
    }

    static void IdleTick()
    {
        if (IsActive || !_idleEnabled) return;
        long act = Activity();
        long idle = Environment.TickCount64 - act;
        // Listen for sound during the last 10 s before the timeout and while waiting, once a second.
        if (_skipWhenBusy && idle >= _timeoutMs - 10_000 && AudioMeter.Peak() > 0.0005f) _lastSoundAt = Environment.TickCount64;
        if (idle < _timeoutMs || act == _triggeredFor) return;
        if (_skipWhenBusy && BusyReason() is { } why)
        {
            // Keep checking: when the video or game ends while you're still away, it blacks out then.
            if (act != _loggedBusyFor) { _loggedBusyFor = act; Log.Write($"Idle black-out waiting: {why}"); }
            return;
        }
        _triggeredFor = act;   // one black-out per idle period
        Show(fade: true, reason: $"idle {idle / 60_000} min");
    }

    static void ActiveTick()
    {
        long now = Environment.TickCount64;
        long act = Activity();
        if (now < _graceUntil) _baseline = act;          // ignore the keys still being released after the hotkey
        else if (act != _baseline) { Hide("input"); return; }

        if (_fading)
        {
            int a = (int)Math.Clamp((now - _shownAt) * 255 / FadeMs, 0, 255);
            foreach (var w in _windows) SetLayeredWindowAttributes(w, 0, (byte)a, 2 /*LWA_ALPHA*/);
            if (a >= 255) _fading = false;
        }
        if (++_ticks % 10 == 0)   // once a second: stay above toasts and other topmost windows
            foreach (var w in _windows) SetWindowPos(w, -1 /*HWND_TOPMOST*/, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void Show(bool fade, string reason)
    {
        if (IsActive) return;
        var wanted = _monitors;
        var ids = wanted.Length > 0 ? Monitors.Identify() : null;
        foreach (var m in Monitors.Enumerate())
        {
            if (ids != null && !(ids.TryGetValue(m.GdiName, out var id) && wanted.Contains(id.DevicePath, StringComparer.OrdinalIgnoreCase))) continue;
            var r = m.Bounds;
            var h = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED, ClassName, "ScreenShift black-out",
                WS_POPUP, r.Left, r.Top, r.Width, r.Height, 0, 0, _inst, 0);
            if (h == 0) continue;
            SetLayeredWindowAttributes(h, 0, fade ? (byte)0 : (byte)255, 2);
            ShowWindow(h, 4 /*SW_SHOWNOACTIVATE*/);
            _windows.Add(h);
        }
        if (_windows.Count == 0) { Log.Write("Black-out: none of the chosen screens is active"); return; }

        _shownAt = Environment.TickCount64;
        _fading = fade;
        _graceUntil = fade ? 0 : _shownAt + GraceMs;
        _baseline = Activity();
        _ticks = 0;
        SetTimer(_engine, TimerActive, ActivePollMs, 0);
        Log.Write($"Black-out on ({reason}), {_windows.Count} screen(s)");
    }

    public static void Hide(string reason)
    {
        if (!IsActive) return;
        KillTimer(_engine, TimerActive);
        foreach (var w in _windows) DestroyWindow(w);
        _windows.Clear();
        Log.Write($"Black-out off ({reason}) after {(Environment.TickCount64 - _shownAt) / 1000} s");
    }

    /// <summary>Tick (TickCount64 clock) of the latest mouse, keyboard or controller input.</summary>
    static long Activity()
    {
        long now = Environment.TickCount64;
        var li = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        long act = GetLastInputInfo(ref li) ? now - (uint)((uint)now - li.dwTime) : now;
        PollPads(now);
        return Math.Max(act, _padActivity);
    }

    static void PollPads(long now)
    {
        if (_xinputMissing) return;
        // Asking about an empty controller slot is slow-ish, so rescan empty slots only every 3 s.
        bool scan = now >= _padScanAt;
        if (scan) _padScanAt = now + 3000;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (!_padConnected[i] && !scan) continue;
                bool ok = XInputGetState(i, out var s) == 0;
                if (ok && _padConnected[i] && s.dwPacketNumber != _padStates[i].dwPacketNumber && Moved(_padStates[i], s)) _padActivity = now;
                _padConnected[i] = ok;
                if (ok) _padStates[i] = s;
            }
        }
        catch (DllNotFoundException) { _xinputMissing = true; }
        catch (EntryPointNotFoundException) { _xinputMissing = true; }
    }

    /// <summary>A real press or stick movement, not the small noise a drifting stick reports constantly.</summary>
    static bool Moved(in XINPUT_STATE a, in XINPUT_STATE b) =>
        a.wButtons != b.wButtons
        || Math.Abs(a.bLeftTrigger - b.bLeftTrigger) > 30 || Math.Abs(a.bRightTrigger - b.bRightTrigger) > 30
        || Math.Abs(a.sThumbLX - b.sThumbLX) > 4000 || Math.Abs(a.sThumbLY - b.sThumbLY) > 4000
        || Math.Abs(a.sThumbRX - b.sThumbRX) > 4000 || Math.Abs(a.sThumbRY - b.sThumbRY) > 4000;

    /// <summary>Why black-out should wait: fullscreen app/game, presentation mode, or sound playing (video, call, game).</summary>
    static string? BusyReason()
    {
        if (SHQueryUserNotificationState(out int q) == 0 && q is 2 or 3 or 4)
            return q == 3 ? "fullscreen game" : q == 4 ? "presentation mode" : "fullscreen app";
        if (Environment.TickCount64 - _lastSoundAt < 10_000) return "sound is playing";
        return null;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint w, nint l)
    {
        try
        {
            switch (msg)
            {
                case WM_SETCURSOR: SetCursor(0); return 1;                 // no cursor over a blacked-out screen
                case WM_MOUSEACTIVATE: return 3;                            // MA_NOACTIVATE: never steal focus
                case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:    // click swallowed, wakes
                    if (Environment.TickCount64 >= _graceUntil) Hide("click");
                    return 0;
            }
        }
        catch (Exception ex) { Log.Write("Black-out window error: " + ex.Message); }
        return DefWindowProcW(hwnd, msg, w, l);
    }
}
