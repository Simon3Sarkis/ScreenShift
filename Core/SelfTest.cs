using System.Text;

namespace ScreenShift.Core;

/// <summary>Run "ScreenShift.exe --selftest": checks cursor mapping maths and reads the live display config. Writes selftest.txt.</summary>
public static class SelfTest
{
    public static void Run()
    {
        var sb = new StringBuilder();
        int fails = 0;
        void Check(string name, CursorBridge.Mon[] m, int a, int x, int y, int mode, int ex, int ey, int tol = 2)
        {
            bool ok = CursorBridge.Map(m, a, x, y, mode, out int tx, out int ty, out _);
            bool pass = ok && Math.Abs(tx - ex) <= tol && Math.Abs(ty - ey) <= tol;
            if (!pass) fails++;
            sb.AppendLine($"{(pass ? "PASS" : "FAIL")}  {name}: -> ({tx},{ty}) expected ({ex},{ey})");
        }

        // 27" 4K @150% on the left, 27" 1440p @100% on the right, tops aligned.
        var lr = new[] { new CursorBridge.Mon { L = 0, T = 0, R = 3840, B = 2160, S = 1.5 }, new CursorBridge.Mon { L = 3840, T = 0, R = 6400, B = 1440, S = 1.0 } };
        Check("4K top -> 1440p top", lr, 0, 3840, 0, 0, 3840, 0);
        Check("4K middle -> 1440p middle", lr, 0, 3841, 1080, 0, 3841, 720);
        Check("4K bottom (dead zone in Windows) -> 1440p bottom", lr, 0, 3842, 2159, 0, 3842, 1439);
        Check("1440p bottom -> 4K bottom", lr, 1, 3839, 1439, 0, 3838, 2159);
        Check("1440p middle -> 4K middle", lr, 1, 3839, 720, 0, 3838, 1081);
        Check("scaled-top mode, same physical size", lr, 0, 3840, 1500, 1, 3840, 1000);

        // 1440p centred above the 4K
        var tb = new[] { new CursorBridge.Mon { L = 640, T = -1440, R = 3200, B = 0, S = 1.0 }, new CursorBridge.Mon { L = 0, T = 0, R = 3840, B = 2160, S = 1.5 } };
        Check("4K top-left corner -> 1440p left", tb, 1, 0, -1, 0, 640, -1);
        Check("4K top-right -> 1440p right", tb, 1, 3839, -1, 0, 3199, -1);
        Check("1440p centre -> 4K centre", tb, 0, 1920, 0, 0, 1920, 0);

        // Outer edge: no neighbour, must not remap
        bool none = CursorBridge.Map(lr, 0, -1, 500, 0, out _, out _, out _);
        if (none) fails++;
        sb.AppendLine($"{(none ? "FAIL" : "PASS")}  outer edge is left alone");

        sb.AppendLine();
        sb.AppendLine("Live monitors:");
        try
        {
            foreach (var m in Monitors.Enumerate(true))
                sb.AppendLine($"  {m.Name} [{m.GdiName}] {m.Bounds.Left},{m.Bounds.Top} {m.Bounds.Width}x{m.Bounds.Height} scale {m.Scale:0.##} primary={m.Primary}");
            sb.AppendLine("Display config:");
            foreach (var m in DisplayProfiles.CaptureSnapshot().Monitors)
                sb.AppendLine($"  {m.Name} target {m.TargetId} {m.Width}x{m.Height}@{m.X},{m.Y} {m.RefreshHz}Hz HDR supported={m.HdrSupported} on={m.HdrEnabled}");
            sb.AppendLine("Connected displays (active or not):");
            var (all, _) = Native.DisplayConfigApi.Query(Native.DisplayConfigApi.QDC_ALL_PATHS);
            foreach (var g in all.Where(p => p.targetInfo.targetAvailable != 0)
                                 .GroupBy(p => (p.targetInfo.adapterId.Value, p.targetInfo.id)))
            {
                var t = g.First().targetInfo;
                var (name, _) = Native.DisplayConfigApi.GetTargetName(t.adapterId, t.id);
                bool active = g.Any(p => (p.flags & Native.DisplayConfigApi.PATH_ACTIVE) != 0);
                sb.AppendLine($"  {(name.Length > 0 ? name : "(no name)")} target {t.id} tech {t.outputTechnology} active={active}");
            }
        }
        catch (Exception ex) { sb.AppendLine("  error: " + ex); fails++; }

        sb.AppendLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        Directory.CreateDirectory(SettingsStore.Dir);
        File.WriteAllText(Path.Combine(SettingsStore.Dir, "selftest.txt"), sb.ToString());
    }
}
