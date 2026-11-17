using System.ComponentModel;
using ScreenShift.Native;
using static ScreenShift.Native.DisplayConfigApi;

namespace ScreenShift.Core;

public enum ApplyStatus { Applied, AppliedWithFallback, AlreadyActive, Failed }

public sealed record ApplyResult(ApplyStatus Status, string Message)
{
    public bool Changed => Status is ApplyStatus.Applied or ApplyStatus.AppliedWithFallback;
}

/// <summary>Raw snapshot of the live configuration, used to revert.</summary>
public sealed record DisplaySnapshot(DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes, List<MonitorRef> Monitors);

/// <summary>
/// Captures and applies display layouts through the Windows CCD API (SetDisplayConfig).
/// Safety measures compared to naive switchers:
///  - adapter LUIDs (which change every reboot / driver update) are remapped by device path
///  - target IDs are remapped by monitor device path if the connector changed
///  - refuses to switch if a monitor of the profile is not connected (prevents "all screens black")
///  - validates before applying, falls back to letting Windows pick the timings if the stored mode is rejected
///  - verifies the result afterwards, and HDR state is restored per monitor
/// </summary>
public static class DisplayProfiles
{
    const uint ApplyFlags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES;

    public static DisplaySnapshot CaptureSnapshot()
    {
        var (paths, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        return new DisplaySnapshot(paths, modes, Describe(paths, modes));
    }

    public static Profile CreateProfile(string name)
    {
        var snap = CaptureSnapshot();
        var p = new Profile { Name = name };
        Fill(p, snap);
        return p;
    }

    public static void Fill(Profile p, DisplaySnapshot snap)
    {
        p.Paths = Convert.ToBase64String(ToBytes(snap.Paths));
        p.Modes = Convert.ToBase64String(ToBytes(snap.Modes));
        p.Monitors = snap.Monitors;
        p.Saved = DateTime.Now;
        p.Adapters = snap.Paths.SelectMany(x => new[] { x.sourceInfo.adapterId, x.targetInfo.adapterId })
            .Concat(snap.Modes.Select(m => m.adapterId))
            .Distinct()
            .Select(l => new AdapterRef { Luid = l.Value, DevicePath = GetAdapterPath(l) })
            .ToList();
    }

    public static List<MonitorRef> Describe(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var list = new List<MonitorRef>();
        foreach (var path in paths)
        {
            if ((path.flags & PATH_ACTIVE) == 0) continue;
            var t = path.targetInfo;
            var (friendly, devPath) = GetTargetName(t.adapterId, t.id);
            var m = new MonitorRef
            {
                AdapterLuid = t.adapterId.Value,
                TargetId = t.id,
                Name = string.IsNullOrWhiteSpace(friendly) ? $"Display {list.Count + 1}" : friendly,
                DevicePath = devPath,
                RefreshHz = t.refreshRate.Denominator == 0 ? 0 : Math.Round((double)t.refreshRate.Numerator / t.refreshRate.Denominator, 2),
            };
            uint si = path.sourceInfo.modeInfoIdx;
            if (si < modes.Length && modes[si].infoType == MODE_SOURCE)
            {
                var sm = modes[si].sourceMode;
                m.X = sm.position.X; m.Y = sm.position.Y; m.Width = (int)sm.width; m.Height = (int)sm.height;
            }
            (m.HdrSupported, m.HdrEnabled) = GetHdr(t.adapterId, t.id);
            list.Add(m);
        }
        return list;
    }

    public static ApplyResult Apply(Profile profile)
    {
        DISPLAYCONFIG_PATH_INFO[] paths;
        DISPLAYCONFIG_MODE_INFO[] modes;
        try
        {
            paths = FromBytes<DISPLAYCONFIG_PATH_INFO>(Convert.FromBase64String(profile.Paths));
            modes = FromBytes<DISPLAYCONFIG_MODE_INFO>(Convert.FromBase64String(profile.Modes));
        }
        catch (Exception ex) { return new(ApplyStatus.Failed, "Profile data is damaged: " + ex.Message); }
        if (paths.Length == 0) return new(ApplyStatus.Failed, "This profile has no displays.");

        var monitors = profile.Monitors.Select(m => new MonitorRef
        {
            AdapterLuid = m.AdapterLuid, TargetId = m.TargetId, Name = m.Name, DevicePath = m.DevicePath,
            HdrSupported = m.HdrSupported, HdrEnabled = m.HdrEnabled
        }).ToList();

        var (allPaths, _) = Query(QDC_ALL_PATHS);

        // 1) Remap adapter LUIDs (they are regenerated on every boot).
        var currentAdapters = allPaths.SelectMany(p => new[] { p.sourceInfo.adapterId, p.targetInfo.adapterId }).Distinct().ToList();
        var byPath = new Dictionary<string, LUID>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in currentAdapters)
        {
            var dp = GetAdapterPath(l);
            if (dp.Length > 0) byPath.TryAdd(dp, l);
        }
        var luidMap = new Dictionary<ulong, LUID>();
        foreach (var a in profile.Adapters)
        {
            if (a.DevicePath.Length > 0 && byPath.TryGetValue(a.DevicePath, out var now)) luidMap[a.Luid] = now;
            else if (currentAdapters.Any(c => c.Value == a.Luid)) luidMap[a.Luid] = LUID.From(a.Luid);
        }
        LUID Map(LUID l) => luidMap.TryGetValue(l.Value, out var n) ? n : l;
        for (int i = 0; i < paths.Length; i++)
        {
            paths[i].sourceInfo.adapterId = Map(paths[i].sourceInfo.adapterId);
            paths[i].targetInfo.adapterId = Map(paths[i].targetInfo.adapterId);
        }
        for (int i = 0; i < modes.Length; i++) modes[i].adapterId = Map(modes[i].adapterId);
        foreach (var m in monitors) m.AdapterLuid = Map(LUID.From(m.AdapterLuid)).Value;

        // 2) Make sure every monitor of the profile is connected; remap connector IDs if needed.
        var available = new Dictionary<(ulong, uint), string>();
        foreach (var p in allPaths)
        {
            if (p.targetInfo.targetAvailable == 0) continue;
            var key = (p.targetInfo.adapterId.Value, p.targetInfo.id);
            if (!available.ContainsKey(key)) available[key] = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id).DevicePath;
        }
        var missing = new List<string>();
        for (int i = 0; i < paths.Length; i++)
        {
            var t = paths[i].targetInfo;
            var mon = monitors.FirstOrDefault(m => m.AdapterLuid == t.adapterId.Value && m.TargetId == t.id);
            if (available.ContainsKey((t.adapterId.Value, t.id))) continue;
            if (mon != null && mon.DevicePath.Length > 0)
            {
                var hit = available.FirstOrDefault(kv => string.Equals(kv.Value, mon.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (hit.Value != null && hit.Key.Item1 == t.adapterId.Value)
                {
                    Log.Write($"Remapped {mon.Name}: target {t.id} -> {hit.Key.Item2}");
                    paths[i].targetInfo.id = hit.Key.Item2;
                    uint ti = t.modeInfoIdx;
                    if (ti < modes.Length && modes[ti].infoType == MODE_TARGET) modes[ti].id = hit.Key.Item2;
                    mon.TargetId = hit.Key.Item2;
                    continue;
                }
            }
            missing.Add(mon?.Name ?? $"display #{t.id}");
        }
        if (missing.Count > 0)
            return new(ApplyStatus.Failed, $"{string.Join(", ", missing)} is not connected or is switched off. Nothing was changed.");

        // 3) Skip if already active (avoids pointless mode-sets, which are what blank OLEDs).
        var (curPaths, curModes) = Query(QDC_ONLY_ACTIVE_PATHS);
        if (Signature(paths, modes) == Signature(curPaths, curModes))
        {
            bool hdrChanged = profile.RestoreHdr && ApplyHdr(monitors);
            return new(ApplyStatus.AlreadyActive, hdrChanged ? "Layout already active, HDR state restored." : "This layout is already active.");
        }

        // 4) Validate, then apply.
        var status = ApplyStatus.Applied;
        int err = Set(paths, modes, SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
        if (err == 0) err = Set(paths, modes, ApplyFlags);
        if (err != 0)
        {
            Log.Write($"Exact apply failed ({err}: {new Win32Exception(err).Message}); trying topology-only fallback.");
            var topo = (DISPLAYCONFIG_PATH_INFO[])paths.Clone();
            for (int i = 0; i < topo.Length; i++)
            {
                topo[i].sourceInfo.modeInfoIdx = MODE_IDX_INVALID;
                topo[i].targetInfo.modeInfoIdx = MODE_IDX_INVALID;
            }
            err = Set(topo, null, SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
            if (err != 0)
                return new(ApplyStatus.Failed, $"Windows rejected this layout ({new Win32Exception(err).Message}). Nothing was changed.");
            status = ApplyStatus.AppliedWithFallback;
        }

        // 5) Verify every expected display is now active.
        var (afterPaths, _) = Query(QDC_ONLY_ACTIVE_PATHS);
        var active = afterPaths.Select(p => (p.targetInfo.adapterId.Value, p.targetInfo.id)).ToHashSet();
        var notOn = paths.Where(p => !active.Contains((p.targetInfo.adapterId.Value, p.targetInfo.id)))
                         .Select(p => monitors.FirstOrDefault(m => m.AdapterLuid == p.targetInfo.adapterId.Value && m.TargetId == p.targetInfo.id)?.Name ?? "a display")
                         .ToList();
        if (notOn.Count > 0)
            return new(ApplyStatus.Failed, $"Windows did not turn on {string.Join(", ", notOn)}.");

        if (profile.RestoreHdr) ApplyHdr(monitors);

        return new(status, status == ApplyStatus.AppliedWithFallback
            ? "Applied. Windows picked the resolution/refresh rate because the saved one was rejected - re-save the profile."
            : "Applied.");
    }

    public static ApplyResult Restore(DisplaySnapshot snap)
    {
        int err = Set(snap.Paths, snap.Modes, ApplyFlags);
        if (err != 0) err = Set(null, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        if (err == 0) ApplyHdr(snap.Monitors);
        return err == 0 ? new(ApplyStatus.Applied, "Previous layout restored.") : new(ApplyStatus.Failed, new Win32Exception(err).Message);
    }

    /// <summary>Emergency: turn on all connected displays in extend mode, using Windows' last known-good modes.</summary>
    public static ApplyResult ExtendAll()
    {
        int err = Set(null, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        return err == 0 ? new(ApplyStatus.Applied, "All displays turned on (extend).") : new(ApplyStatus.Failed, new Win32Exception(err).Message);
    }

    /// <summary>Turns on only the given connected display, letting Windows choose its mode (from its own database).</summary>
    public static ApplyResult ActivateOnly(uint targetId)
    {
        var (all, _) = Query(QDC_ALL_PATHS);
        int idx = Array.FindIndex(all, p => p.targetInfo.id == targetId && p.targetInfo.targetAvailable != 0);
        if (idx < 0) return new(ApplyStatus.Failed, "That display is not connected.");
        var path = all[idx];
        path.flags |= PATH_ACTIVE;
        path.sourceInfo.modeInfoIdx = MODE_IDX_INVALID;
        path.targetInfo.modeInfoIdx = MODE_IDX_INVALID;
        var one = new[] { path };
        int err = Set(one, null, SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
        if (err != 0)
        {
            Log.Write($"Topology-supplied activate failed ({err}); trying supplied config.");
            err = Set(one, null, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        }
        return err == 0 ? new(ApplyStatus.Applied, "Activated.") : new(ApplyStatus.Failed, new Win32Exception(err).Message);
    }

    static bool ApplyHdr(List<MonitorRef> monitors)
    {
        bool changed = false;
        foreach (var m in monitors)
        {
            if (!m.HdrSupported) continue;
            var l = LUID.From(m.AdapterLuid);
            var (sup, en) = GetHdr(l, m.TargetId);
            if (sup && en != m.HdrEnabled)
            {
                if (SetHdr(l, m.TargetId, m.HdrEnabled)) changed = true;
                else Log.Write($"Could not set HDR on {m.Name}");
            }
        }
        return changed;
    }

    static string Signature(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var parts = new List<string>();
        foreach (var p in paths)
        {
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            string src = "?";
            uint si = p.sourceInfo.modeInfoIdx;
            if (si < modes.Length && modes[si].infoType == MODE_SOURCE)
            {
                var s = modes[si].sourceMode;
                src = $"{s.width}x{s.height}@{s.position.X},{s.position.Y}";
            }
            var r = p.targetInfo.refreshRate;
            double hz = r.Denominator == 0 ? 0 : Math.Round((double)r.Numerator / r.Denominator, 1);
            parts.Add($"{p.targetInfo.adapterId.Value}:{p.targetInfo.id}|{src}|{hz}|{p.targetInfo.rotation}");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }
}
