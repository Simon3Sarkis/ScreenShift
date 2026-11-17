using System.Runtime.InteropServices;
using ScreenShift.Native;

namespace ScreenShift.Core;

/// <summary>
/// Makes the cursor cross between monitors with different resolution / scaling without getting stuck
/// and without jumping. Windows lines monitors up in raw pixels, so a 4K panel at 150% next to a
/// 1440p panel at 100% only shares 1440 of its 2160 pixel rows, and the rows that are shared are
/// at the wrong height. Both panels are 2560x1440 "logical" pixels, so the correct crossing point is
/// the same relative height. This hook detects an edge crossing and places the cursor at the matching
/// spot on the neighbour.
///
/// Performance: the hot path (cursor stays on the same monitor) is two integer compares, no system
/// calls, no allocations. The hook runs on its own thread so UI work never delays the mouse.
/// </summary>
public static unsafe class CursorBridge
{
    public struct Mon { public int L, T, R, B; public double S; }   // R/B exclusive, S = scale (1.5 = 150%)

    static Mon[] _mons = [];
    static RECT _virtual;
    static int _cur = -1;
    static nint _hook;
    static int _events;

    public static volatile bool Suspended;
    public static volatile int Mode;   // BridgeMode

    public static bool Installed => _hook != 0;
    public static int EventCount => Volatile.Read(ref _events);

    // ---- called on the engine thread only ----

    public static bool Install()
    {
        if (_hook != 0) return true;
        _hook = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, &HookProc, Win32.GetModuleHandleW(null), 0);
        if (_hook == 0) Log.Write("Mouse hook install failed: " + Marshal.GetLastWin32Error());
        return _hook != 0;
    }

    public static void Uninstall()
    {
        if (_hook == 0) return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    public static int UpdateLayout()
    {
        var live = Monitors.Enumerate();
        var arr = new Mon[live.Count];
        var v = new RECT { Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue };
        for (int i = 0; i < arr.Length; i++)
        {
            var b = live[i].Bounds;
            arr[i] = new Mon { L = b.Left, T = b.Top, R = b.Right, B = b.Bottom, S = live[i].Scale };
            v.Left = Math.Min(v.Left, b.Left); v.Top = Math.Min(v.Top, b.Top);
            v.Right = Math.Max(v.Right, b.Right); v.Bottom = Math.Max(v.Bottom, b.Bottom);
        }
        _virtual = v;
        _cur = -1;
        Volatile.Write(ref _mons, arr);
        return arr.Length;
    }

    [UnmanagedCallersOnly]
    static nint HookProc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0 && wParam == (nint)Win32.WM_MOUSEMOVE)
            {
                _events++;
                if (!Suspended)
                {
                    var h = (MSLLHOOKSTRUCT*)lParam;
                    if (TryRemap(h->pt.X, h->pt.Y)) return 1;   // swallow the original move
                }
            }
        }
        catch { /* never let an exception escape into user32 */ }
        return Win32.CallNextHookEx(0, code, wParam, lParam);
    }

    static bool TryRemap(int x, int y)
    {
        var m = _mons;
        int n = m.Length;
        if (n < 2) return false;

        int c = _cur;
        if ((uint)c < (uint)n && In(in m[c], x, y)) return false;   // hot path

        Win32.GetCursorPos(out var p);
        int a = Find(m, p.X, p.Y);
        if (a < 0) { _cur = Find(m, x, y); return false; }
        _cur = a;
        if (In(in m[a], x, y)) return false;

        // Don't fight games / apps that lock the cursor to a region.
        if (Win32.GetClipCursor(out var clip) &&
            (clip.Left > _virtual.Left || clip.Top > _virtual.Top || clip.Right < _virtual.Right || clip.Bottom < _virtual.Bottom))
            return false;

        if (!Map(m, a, x, y, Mode, out int tx, out int ty, out int target)) return false;
        Win32.SetCursorPos(tx, ty);
        _cur = target;
        return true;
    }

    static bool In(in Mon r, int x, int y) => x >= r.L && x < r.R && y >= r.T && y < r.B;

    static int Find(Mon[] m, int x, int y)
    {
        for (int i = 0; i < m.Length; i++) if (In(in m[i], x, y)) return i;
        return -1;
    }

    /// <summary>
    /// Pure mapping function. Given the monitor the cursor is on (a) and the position it is trying to
    /// move to (x, y) outside of it, returns where it should land. Public for the self-test.
    /// </summary>
    public static bool Map(Mon[] m, int a, int x, int y, int mode, out int tx, out int ty, out int target)
    {
        tx = ty = 0; target = -1;
        ref readonly var A = ref m[a];

        // Which edge is being crossed: 0 right, 1 left, 2 bottom, 3 top.
        int side = 0, over = x - (A.R - 1);
        if (A.L - x > over) { side = 1; over = A.L - x; }
        if (y - (A.B - 1) > over) { side = 2; over = y - (A.B - 1); }
        if (A.T - y > over) { side = 3; over = A.T - y; }
        if (over <= 0) return false;

        bool horiz = side < 2;                       // crossing a vertical edge => mapping along Y
        int aLo = horiz ? A.T : A.L, aHi = horiz ? A.B : A.R;
        int pos = Math.Clamp(horiz ? y : x, aLo, aHi - 1);

        // Neighbours touching that edge (with some overlap along it).
        Span<int> nb = stackalloc int[m.Length];
        int count = 0, nLo = int.MaxValue, nHi = int.MinValue;
        for (int i = 0; i < m.Length; i++)
        {
            if (i == a) continue;
            ref readonly var N = ref m[i];
            bool touches = side switch
            {
                0 => Math.Abs(N.L - A.R) <= 2,
                1 => Math.Abs(N.R - A.L) <= 2,
                2 => Math.Abs(N.T - A.B) <= 2,
                _ => Math.Abs(N.B - A.T) <= 2,
            };
            if (!touches) continue;
            int lo = horiz ? N.T : N.L, hi = horiz ? N.B : N.R;
            if (lo >= aHi || hi <= aLo) continue;
            nb[count++] = i;
            nLo = Math.Min(nLo, lo); nHi = Math.Max(nHi, hi);
        }
        if (count == 0) return false;

        // Position along the edge on the far side.
        double t;
        if (mode == (int)BridgeMode.Proportional)
        {
            t = nLo + (pos - aLo + 0.5) * (nHi - nLo) / (aHi - aLo);
        }
        else
        {
            // Scale-aware: equal distance in logical pixels from the chosen anchor.
            int r = nb[0];
            for (int k = 1; k < count; k++)
            {
                ref readonly var N = ref m[nb[k]];
                ref readonly var R = ref m[r];
                int nl = horiz ? N.T : N.L, nh = horiz ? N.B : N.R, rl = horiz ? R.T : R.L, rh = horiz ? R.B : R.R;
                if (mode == (int)BridgeMode.ScaledTop ? nl < rl :
                    mode == (int)BridgeMode.ScaledBottom ? nh > rh :
                    Overlap(nl, nh, aLo, aHi) > Overlap(rl, rh, aLo, aHi)) r = nb[k];
            }
            ref readonly var Ref = ref m[r];
            double ratio = Ref.S / A.S;
            int rLo = horiz ? Ref.T : Ref.L, rHi = horiz ? Ref.B : Ref.R;
            t = mode switch
            {
                (int)BridgeMode.ScaledTop => rLo + (pos - aLo) * ratio,
                (int)BridgeMode.ScaledBottom => rHi - 1 - (aHi - 1 - pos) * ratio,
                _ => (rLo + rHi) / 2.0 + (pos - (aLo + aHi) / 2.0) * ratio,
            };
        }

        // Pick the neighbour that contains t (or the nearest one) and clamp into it.
        int best = nb[0]; double bestDist = double.MaxValue;
        for (int k = 0; k < count; k++)
        {
            ref readonly var N = ref m[nb[k]];
            int lo = horiz ? N.T : N.L, hi = horiz ? N.B : N.R;
            double d = t < lo ? lo - t : t >= hi ? t - (hi - 1) : 0;
            if (d < bestDist) { bestDist = d; best = nb[k]; }
        }
        ref readonly var B = ref m[best];
        int along = (int)Math.Clamp(Math.Round(t), horiz ? B.T : B.L, (horiz ? B.B : B.R) - 1);
        int depth = Math.Min((int)((over - 1) * (B.S / A.S)), 4);   // land just inside the edge

        switch (side)
        {
            case 0: tx = Math.Min(B.L + depth, B.R - 1); ty = along; break;
            case 1: tx = Math.Max(B.R - 1 - depth, B.L); ty = along; break;
            case 2: ty = Math.Min(B.T + depth, B.B - 1); tx = along; break;
            default: ty = Math.Max(B.B - 1 - depth, B.T); tx = along; break;
        }
        target = best;
        return true;
    }

    static int Overlap(int lo, int hi, int lo2, int hi2) => Math.Max(0, Math.Min(hi, hi2) - Math.Max(lo, lo2));
}
