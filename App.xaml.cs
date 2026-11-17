using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenShift.Core;
using ScreenShift.Native;
using ScreenShift.UI;

namespace ScreenShift;

public partial class App : Application
{
    public const int HkCycle = 1, HkBridge = 2, HkEmergency = 3, HkProfileBase = 100;

    public static AppSettings Settings { get; private set; } = new();
    public static App Current2 => (App)Current;

    Mutex? _single;
    MainWindow? _main;
    TrayFlyout? _flyout;
    int _busy;
    bool _settingsLoaded;
    long _flyoutClosedAt;

    public event Action? ProfilesChanged;
    public event Action? DisplaysChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest")) { SelfTest.Run(); Shutdown(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--make-solo-profile") { MakeSoloProfile(uint.Parse(e.Args[1]), e.Args[2]); Shutdown(); return; }
        if (e.Args.Contains("--update-dryrun")) { _ = UpdateDryRun(); return; }
        if (e.Args.Length >= 3 && e.Args[0] == "--apply-update" && int.TryParse(e.Args[2], out int oldPid))
        {
            Updater.ApplyUpdate(e.Args[1], oldPid);   // runs from the staged copy, then starts the updated app
            Shutdown();
            return;
        }

        _single = new Mutex(true, @"Local\ScreenShift.SingleInstance", out bool first);
        if (!first)
        {
            var other = Win32.FindWindowW(Engine.WindowClass, null);
            if (other != 0) Win32.PostMessageW(other, Engine.MsgShowMain, 0, 0);
            _single.Dispose(); _single = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) => { Log.Write("UI error: " + a.Exception); Osd.Show("Something went wrong (logged)", Osd.Error); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("Fatal: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Write("Task error: " + a.Exception); a.SetObserved(); };

        base.OnStartup(e);
        Settings = SettingsStore.Load();
        _settingsLoaded = true;
        ApplyTheme();

        Engine.HotkeyPressed += id => Dispatcher.BeginInvoke(() => OnHotkey(id));
        Engine.TrayActivated += (pt, dbl) => Dispatcher.BeginInvoke(() => { if (dbl) ShowMain(); else ToggleFlyout(pt); });
        Engine.DisplaysChanged += () => Dispatcher.BeginInvoke(() => DisplaysChanged?.Invoke());
        Engine.ShowMainRequested += () => Dispatcher.BeginInvoke(ShowMain);
        Engine.Start();
        Engine.SetBridge(Settings.CursorBridge, Settings.BridgeMode);
        RegisterHotkeys();
        UpdateTrayTip();

        bool tray = e.Args.Contains("--tray") || e.Args.Contains("--updated") || (Settings.StartMinimized && Settings.Profiles.Count > 0);
        if (!tray) ShowMain();
        else TrimMemorySoon();
        Log.Write($"Started {Updater.Current}");

        if (e.Args.Contains("--updated")) Osd.Show($"ScreenShift updated to {Updater.Current}", Osd.Check, seconds: 3);
        if (e.Args.Contains("--update-failed")) Osd.Show("The update couldn't be installed, nothing was changed (see log)", Osd.Error, seconds: 5);
        Task.Run(Updater.CleanupOldFiles);
        StartUpdateTimer();
    }

    // ------------------------------------------------------------ updates

    /// <summary>Diagnostic: check + download + verify the latest release without installing. Writes update-dryrun.txt.</summary>
    async Task UpdateDryRun()
    {
        var sb = new System.Text.StringBuilder($"Current version: {Updater.Current}\n");
        try
        {
            var u = await Updater.CheckAsync();
            if (u == null) sb.AppendLine("Up to date (or no downloadable release).");
            else
            {
                sb.AppendLine($"Found {u.Tag} ({u.Version}), asset {u.AssetSize} bytes, sha256 {(u.Sha256 ?? "not provided")}");
                var stage = await Updater.DownloadAndStageAsync(u, null);
                sb.AppendLine($"Downloaded, checksum and version verified. Staged files: {string.Join(", ", Directory.GetFiles(stage).Select(Path.GetFileName))}");
                Directory.Delete(Path.GetDirectoryName(stage)!, true);
            }
            sb.AppendLine($"Install folder writable: {Updater.CanUpdateInPlace()}");
        }
        catch (Exception ex) { sb.AppendLine("FAILED: " + ex.Message); }
        Directory.CreateDirectory(SettingsStore.Dir);
        File.WriteAllText(Path.Combine(SettingsStore.Dir, "update-dryrun.txt"), sb.ToString());
        Shutdown();
    }

    public UpdateInfo? AvailableUpdate { get; private set; }
    public event Action? UpdateChanged;
    DispatcherTimer? _updateTimer;
    bool _checking;

    void StartUpdateTimer()
    {
        // First look 1 minute after start, then hourly ticks that only call GitHub once every 24 h.
        _updateTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, async (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(1);
            if (!Settings.AutoCheckUpdates) return;
            if (Settings.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(24) && AvailableUpdate == null) return;
            await CheckForUpdates(manual: false);
        }, Dispatcher);
    }

    /// <summary>Asks GitHub for a newer release. Returns an error message, or null on success.</summary>
    public async Task<string?> CheckForUpdates(bool manual)
    {
        if (_checking) return null;
        _checking = true;
        try
        {
            AvailableUpdate = await Updater.CheckAsync();
            Settings.LastUpdateCheck = DateTime.Now;
            SaveSettings();
            if (AvailableUpdate is { } u && !manual && Settings.NotifiedVersion != u.Version.ToString())
            {
                Settings.NotifiedVersion = u.Version.ToString();
                SaveSettings();
                Osd.Show($"ScreenShift {u.Version} is available. Open Settings to install it.", Osd.Display, seconds: 5);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            return manual ? "Couldn't reach GitHub. Check your internet connection." : null;
        }
        finally
        {
            _checking = false;
            UpdateChanged?.Invoke();
            UpdateTrayTip();
        }
    }

    /// <summary>Downloads, verifies and installs the available update, then restarts. Returns an error message on failure.</summary>
    public async Task<string?> InstallUpdate(IProgress<double> progress)
    {
        if (AvailableUpdate is not { } u) return "No update available.";
        if (!Updater.CanUpdateInPlace())
        {
            Updater.OpenInBrowser(u.PageUrl);
            return "ScreenShift's folder is read-only, so the download page was opened instead.";
        }
        try
        {
            var stage = await Task.Run(() => Updater.DownloadAndStageAsync(u, progress));
            SaveSettings();
            Updater.LaunchApply(stage);
            ExitApp();
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Update download failed: " + ex);
            return "The update couldn't be downloaded: " + ex.Message;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Engine.Stop();
        if (_settingsLoaded) SettingsStore.Save(Settings);   // never write defaults over real profiles (e.g. after --selftest)
        _single?.ReleaseMutex();
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>One-shot: switch to a single display, capture it as a profile, always switch back.</summary>
    static void MakeSoloProfile(uint targetId, string name)
    {
        Settings = SettingsStore.Load();
        var before = DisplayProfiles.CaptureSnapshot();
        try
        {
            var r = DisplayProfiles.ActivateOnly(targetId);
            Log.Write($"Solo activate {targetId}: {r.Status} {r.Message}");
            if (r.Status == ApplyStatus.Failed) return;
            Thread.Sleep(4000);   // let the panel sync and Windows settle
            var snap = DisplayProfiles.CaptureSnapshot();
            if (snap.Monitors.Count == 1 && snap.Monitors[0].TargetId == targetId)
            {
                var p = new Profile { Name = name };
                DisplayProfiles.Fill(p, snap);
                Settings.Profiles.RemoveAll(x => x.Name == name);
                Settings.Profiles.Add(p);
                SettingsStore.Save(Settings);
                var m = snap.Monitors[0];
                Log.Write($"Saved profile '{name}': {m.Name} {m.Width}x{m.Height} {m.RefreshHz}Hz HDR={m.HdrEnabled}");
            }
            else Log.Write("Solo activate: unexpected result, active = " + string.Join(", ", snap.Monitors.Select(x => x.Name)));
        }
        catch (Exception ex) { Log.Write("Solo profile failed: " + ex); }
        finally
        {
            var back = DisplayProfiles.Restore(before);
            Log.Write($"Restore after solo: {back.Status} {back.Message}");
            if (back.Status == ApplyStatus.Failed) DisplayProfiles.ExtendAll();
        }
    }

    // ------------------------------------------------------------ windows

    // Windows are created once and then hidden/shown. WPF leaks a few OS event handles every time a
    // window is destroyed, so reusing them keeps the handle count flat no matter how often you open them.
    public static bool IsExiting { get; private set; }

    public void ShowMain()
    {
        _flyout?.Hide();
        if (_main == null)
        {
            _main = new MainWindow();
            _main.IsVisibleChanged += (_, _) => { if (!_main.IsVisible) TrimMemorySoon(); };
        }
        else _main.Reload();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowSettingsPage()
    {
        ShowMain();
        _main?.SelectPage(2);
    }

    void ToggleFlyout(POINT anchor)
    {
        if (_flyout?.IsVisible == true) { _flyout.Hide(); return; }
        // Clicking the tray icon while the flyout is open first deactivates (hides) it; don't reopen.
        if (Environment.TickCount64 - _flyoutClosedAt < 350) return;
        if (_flyout == null)
        {
            _flyout = new TrayFlyout();
            _flyout.IsVisibleChanged += (_, _) =>
            {
                if (_flyout.IsVisible) return;
                _flyoutClosedAt = Environment.TickCount64;
                TrimMemorySoon();
            };
        }
        _flyout.ShowAt(anchor);
    }

    public void ExitApp()
    {
        IsExiting = true;
        Shutdown();
    }

    public void ApplyTheme()
    {
#pragma warning disable WPF0001
        ThemeMode = Settings.Theme switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
    }

    DispatcherTimer? _trimTimer;
    /// <summary>After windows close, release WPF visuals and give memory back to Windows.</summary>
    void TrimMemorySoon()
    {
        _trimTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _trimTimer!.Stop();
            if (_main?.IsVisible == true || _flyout?.IsVisible == true) return;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();   // releases OS handles held by finalizable WPF objects
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            Win32.SetProcessWorkingSetSize(Win32.GetCurrentProcess(), -1, -1);
        }, Dispatcher);
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    // ------------------------------------------------------------ settings

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        UpdateTrayTip();
    }

    public void NotifyProfilesChanged()
    {
        SaveSettings();
        RegisterHotkeys();
        ProfilesChanged?.Invoke();
    }

    public void RegisterHotkeys()
    {
        var list = new List<(int, Hotkey)>();
        if (Settings.CycleHotkey != null) list.Add((HkCycle, Settings.CycleHotkey));
        if (Settings.BridgeToggleHotkey != null) list.Add((HkBridge, Settings.BridgeToggleHotkey));
        if (Settings.EmergencyHotkey != null) list.Add((HkEmergency, Settings.EmergencyHotkey));
        for (int i = 0; i < Settings.Profiles.Count; i++)
            if (Settings.Profiles[i].Hotkey is { } hk) list.Add((HkProfileBase + i, hk));
        Engine.SetHotkeys(list);
    }

    /// <summary>Temporarily disables global hotkeys while the user records a new one.</summary>
    public void SuspendHotkeys() => Engine.SetHotkeys([]);

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return k?.GetValue("ScreenShift") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) k.SetValue("ScreenShift", $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue("ScreenShift", false);
        }
    }

    void UpdateTrayTip()
    {
        var last = Settings.Profiles.FirstOrDefault(p => p.Id == Settings.LastProfileId);
        Engine.SetTrayTip("ScreenShift" + (last != null ? $" - {last.Name}" : "") + (Settings.CursorBridge ? "" : " (cursor alignment off)")
            + (AvailableUpdate != null ? $"\nUpdate available: {AvailableUpdate.Version}" : ""));
    }

    // ------------------------------------------------------------ actions

    void OnHotkey(int id)
    {
        switch (id)
        {
            case HkCycle: CycleProfile(); break;
            case HkBridge: SetBridge(!Settings.CursorBridge); break;
            case HkEmergency: Emergency(); break;
            default:
                int i = id - HkProfileBase;
                if (i >= 0 && i < Settings.Profiles.Count) _ = ApplyProfile(Settings.Profiles[i]);
                break;
        }
    }

    /// <summary>Turns on every connected display (extend). The way out of a black screen.</summary>
    public void Emergency() => _ = RunSwitch("all displays", DisplayProfiles.ExtendAll, confirm: false);

    public void SetBridge(bool on)
    {
        Settings.CursorBridge = on;
        Engine.SetBridge(on, Settings.BridgeMode);
        SaveSettings();
        Osd.Show(on ? "Cursor alignment on" : "Cursor alignment off", Osd.Mouse);
        DisplaysChanged?.Invoke();
    }

    void CycleProfile()
    {
        var ps = Settings.Profiles;
        if (ps.Count == 0) { Osd.Show("No profiles saved yet", Osd.Error); return; }
        int i = ps.FindIndex(p => p.Id == Settings.LastProfileId);
        _ = ApplyProfile(ps[(i + 1) % ps.Count]);
    }

    public Task ApplyProfile(Profile p) => RunSwitch(p.Name, () => DisplayProfiles.Apply(p), confirm: Settings.ConfirmAfterSwitch, onSuccess: () =>
    {
        Settings.LastProfileId = p.Id;
        SaveSettings();
        ProfilesChanged?.Invoke();
    });

    async Task RunSwitch(string name, Func<ApplyResult> action, bool confirm, Action? onSuccess = null)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // ignore key-mashing while a switch runs
        try
        {
            Osd.Show($"Switching to {name}…", Osd.Display, sticky: true);
            DisplaySnapshot? before = null;
            ApplyResult result;
            try
            {
                CursorBridge.Suspended = true;
                result = await Task.Run(() =>
                {
                    before = DisplayProfiles.CaptureSnapshot();
                    return action();
                });
            }
            catch (Exception ex) { result = new(ApplyStatus.Failed, ex.Message); }
            finally { Engine.RefreshLayout(); }

            Log.Write($"Switch '{name}': {result.Status} - {result.Message}");
            if (result.Status == ApplyStatus.Failed) { Osd.Show(result.Message, Osd.Error, seconds: 5); return; }
            if (!result.Changed) { Osd.Show(result.Message, Osd.Check); onSuccess?.Invoke(); return; }

            if (confirm && before != null)
            {
                Osd.Hide();
                bool keep = await ConfirmWindow.Ask(name, Math.Clamp(Settings.ConfirmSeconds, 5, 60));
                if (!keep)
                {
                    var r = await Task.Run(() => DisplayProfiles.Restore(before));
                    Engine.RefreshLayout();
                    Log.Write("Reverted: " + r.Message);
                    Osd.Show(r.Status == ApplyStatus.Failed ? "Revert failed: " + r.Message : "Reverted to previous layout", r.Status == ApplyStatus.Failed ? Osd.Error : Osd.Undo);
                    return;
                }
            }
            onSuccess?.Invoke();
            Osd.Show(result.Status == ApplyStatus.AppliedWithFallback ? $"{name} (Windows picked the timings)" : name, Osd.Check);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
