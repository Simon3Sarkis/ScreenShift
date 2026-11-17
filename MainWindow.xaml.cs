using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenShift.Core;
using ScreenShift.UI;

namespace ScreenShift;

public partial class MainWindow : Window
{
    static AppSettings S => App.Settings;
    readonly Dictionary<int, HotkeyBox> _boxes = [];
    bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();

        BridgeToggle.IsChecked = S.CursorBridge;
        ModeBox.SelectedIndex = (int)S.BridgeMode;
        ConfirmToggle.IsChecked = S.ConfirmAfterSwitch;
        ConfirmSecs.SelectedIndex = S.ConfirmSeconds <= 8 ? 0 : S.ConfirmSeconds <= 15 ? 1 : 2;
        OsdToggle.IsChecked = S.ShowOsd;
        TrayToggle.IsChecked = S.StartMinimized;
        ThemeBox.SelectedIndex = S.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        StartupToggle.IsChecked = App.StartWithWindows;
        AutoUpdateToggle.IsChecked = S.AutoCheckUpdates;
        AboutText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · Settings and log: %APPDATA%\\ScreenShift";

        SetupKey(CycleKey, App.HkCycle, () => S.CycleHotkey, k => S.CycleHotkey = k);
        SetupKey(EmergencyKey, App.HkEmergency, () => S.EmergencyHotkey, k => S.EmergencyHotkey = k);
        SetupKey(BridgeKey, App.HkBridge, () => S.BridgeToggleHotkey, k => S.BridgeToggleHotkey = k);
        SetupKey(BlackoutKey, App.HkBlackout, () => S.BlackoutHotkey, k => S.BlackoutHotkey = k);
        SetupKey(IdleKey, App.HkIdleBlackout, () => S.IdleBlackoutToggleHotkey, k => S.IdleBlackoutToggleHotkey = k);
        SetupKey(TaskbarKey, App.HkTaskbar, () => S.TaskbarToggleHotkey, k => S.TaskbarToggleHotkey = k);
        LoadOled();
        UpdateModeDesc();

        // The window lives for the whole session (hidden when closed), so these subscriptions never leak.
        App.Current2.ProfilesChanged += OnProfilesChanged;
        App.Current2.DisplaysChanged += OnDisplaysChanged;
        App.Current2.UpdateChanged += RefreshUpdateUI;
        App.Current2.OledChanged += () => { _loading = true; LoadOled(); _loading = false; };
        Diagram.SizeChanged += (_, _) => DrawDiagram();

        RefreshAll();
        RefreshUpdateUI();
        _loading = false;
    }

    bool _dirty;

    void OnProfilesChanged() { if (IsVisible) BuildProfiles(); else _dirty = true; }
    void OnDisplaysChanged() { if (IsVisible) { BridgeToggle.IsChecked = S.CursorBridge; RefreshCurrent(); DrawDiagram(); } else _dirty = true; }

    /// <summary>Called before the hidden window is shown again.</summary>
    public void Reload()
    {
        _loading = true;
        BridgeToggle.IsChecked = S.CursorBridge;
        StartupToggle.IsChecked = App.StartWithWindows;
        LoadOled();
        _loading = false;
        RefreshUpdateUI();
        if (_dirty) { _dirty = false; RefreshAll(); }
        else RefreshCurrent();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;   // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    void RefreshAll()
    {
        RefreshCurrent();
        BuildProfiles();
        DrawDiagram();
    }

    // ================================================================ profiles page

    void RefreshCurrent()
    {
        try
        {
            var snap = DisplayProfiles.CaptureSnapshot();
            var live = Monitors.Enumerate();
            CurrentSummary.Text = string.Join("   ·   ", snap.Monitors.OrderBy(m => m.X).Select(m =>
            {
                var lm = live.FirstOrDefault(l => l.Bounds.Left == m.X && l.Bounds.Top == m.Y);
                var scale = lm != null ? $" · {Math.Round(lm.Scale * 100)}%" : "";
                return $"{m.Name} {m.Width}×{m.Height} @ {m.RefreshHz:0.#} Hz{scale}{(m.HdrEnabled ? " · HDR" : "")}";
            }));
            if (string.IsNullOrWhiteSpace(NewName.Text)) NewName.Text = SuggestName(snap.Monitors);
        }
        catch (Exception ex) { CurrentSummary.Text = "Could not read the display layout: " + ex.Message; }
    }

    static string SuggestName(List<MonitorRef> mons)
    {
        var name = mons.Count == 1 ? mons[0].Name + " only" : string.Join(" + ", mons.OrderBy(m => m.X).Select(m => m.Name));
        if (name.Length > 40) name = $"{mons.Count} displays";
        var baseName = name; int n = 2;
        while (S.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName} ({n++})";
        return name;
    }

    void BuildProfiles()
    {
        // Drop old profile hotkey boxes
        foreach (var id in _boxes.Keys.Where(k => k >= App.HkProfileBase).ToList()) _boxes.Remove(id);
        ProfileList.Children.Clear();
        EmptyState.Visibility = S.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < S.Profiles.Count; i++) ProfileList.Children.Add(MakeCard(S.Profiles[i], i));
        MarkConflicts();
    }

    UIElement MakeCard(Profile p, int index)
    {
        bool active = p.Id == S.LastProfileId;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock { Text = active ? "" : "", Style = (Style)FindResource("CardIcon") };
        if (active) icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = p.Name, Style = (Style)FindResource("CardTitle") };
        titleRow.Children.Add(title);
        if (active)
        {
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            var bt = new TextBlock { Text = "Active", FontSize = 11 };
            bt.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            badge.Child = bt;
            titleRow.Children.Add(badge);
        }
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardDesc"),
            Text = string.Join("   ·   ", p.Monitors.OrderBy(m => m.X).Select(m =>
                $"{m.Name} {m.Width}×{m.Height} @ {m.RefreshHz:0.#} Hz{(m.HdrEnabled ? " · HDR" : "")}{(m.Primary ? " · main" : "")}"))
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var key = new HotkeyBox { Margin = new Thickness(0, 0, 8, 0), Width = 170 };
        SetupKey(key, App.HkProfileBase + index, () => p.Hotkey, k => p.Hotkey = k);
        Grid.SetColumn(key, 2);
        grid.Children.Add(key);

        var apply = new Button { Content = "Apply", MinWidth = 80, Height = 32, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        apply.Click += (_, _) => _ = App.Current2.ApplyProfile(p);
        Grid.SetColumn(apply, 3);
        grid.Children.Add(apply);

        var more = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "More options" };
        System.Windows.Automation.AutomationProperties.SetName(more, "More options");
        more.Click +=(_, _) => { var m = BuildMenu(p, index, title, text); m.PlacementTarget = more; m.IsOpen = true; };
        Grid.SetColumn(more, 4);
        grid.Children.Add(more);

        return new Border { Style = (Style)FindResource("Card"), Child = grid };
    }

    ContextMenu BuildMenu(Profile p, int index, TextBlock title, StackPanel host)
    {
        // The "⋯" button draws its dots with the icon font, and a ContextMenu inherits fonts from the
        // button it opens from. Without this, every letter in the menu rendered as an icon (gibberish).
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 14 };
        MenuItem Item(string header, string glyph, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont") } };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
            return mi;
        }

        Item("Rename", "", () => BeginRename(p, title, host));
        Item("Update with current layout", "", () =>
        {
            DisplayProfiles.Fill(p, DisplayProfiles.CaptureSnapshot());
            App.Current2.NotifyProfilesChanged();
            Osd.Show($"\"{p.Name}\" updated", Osd.Check);
        });
        var hdr = Item("Restore HDR on/off", "", () => { p.RestoreHdr = !p.RestoreHdr; App.Current2.SaveSettings(); });
        hdr.IsCheckable = true; hdr.IsChecked = p.RestoreHdr;
        menu.Items.Add(new Separator());
        Item("Move up", "", () => Move(index, -1), index > 0);
        Item("Move down", "", () => Move(index, 1), index < S.Profiles.Count - 1);
        menu.Items.Add(new Separator());
        Item("Delete", "", () =>
        {
            if (MessageBox.Show(this, $"Delete the profile \"{p.Name}\"?", "ScreenShift", MessageBoxButton.YesNo, MessageBoxImage.None, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            S.Profiles.Remove(p);
            App.Current2.NotifyProfilesChanged();
        });
        return menu;
    }

    void Move(int index, int delta)
    {
        var list = S.Profiles;
        int j = index + delta;
        if (j < 0 || j >= list.Count) return;
        (list[index], list[j]) = (list[j], list[index]);
        App.Current2.NotifyProfilesChanged();
    }

    void BeginRename(Profile p, TextBlock title, StackPanel host)
    {
        var row = (StackPanel)host.Children[0];
        var box = new TextBox { Text = p.Name, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        host.Children.RemoveAt(0);
        host.Children.Insert(0, box);
        bool done = false;
        void Commit(bool save)
        {
            if (done) return;
            done = true;
            var name = box.Text.Trim();
            if (save && name.Length > 0 && name != p.Name) { p.Name = name; App.Current2.NotifyProfilesChanged(); }
            else { host.Children.RemoveAt(0); host.Children.Insert(0, row); }
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(true); else if (e.Key == Key.Escape) Commit(false); };
        box.LostKeyboardFocus += (_, _) => Commit(true);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    void SaveNew_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NewName.Text.Trim();
            if (name.Length == 0) name = SuggestName(DisplayProfiles.CaptureSnapshot().Monitors);
            var p = DisplayProfiles.CreateProfile(name);
            S.Profiles.Add(p);
            S.LastProfileId = p.Id;
            NewName.Text = "";
            App.Current2.NotifyProfilesChanged();
            RefreshCurrent();
            Osd.Show($"Saved \"{p.Name}\"", Osd.Check);
        }
        catch (Exception ex)
        {
            Log.Write("Save profile failed: " + ex);
            Osd.Show("Could not save: " + ex.Message, Osd.Error, seconds: 5);
        }
    }

    void NewName_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SaveNew_Click(sender, e); }

    // ================================================================ hotkeys

    void SetupKey(HotkeyBox box, int id, Func<Hotkey?> get, Action<Hotkey?> set)
    {
        box.Hotkey = get();
        _boxes[id] = box;
        box.HotkeyChanged += (b, hk) =>
        {
            if (hk != null && FindOwner(hk, id) is { } owner)
            {
                Osd.Show($"{hk} is already used for {owner}", Osd.Error, seconds: 3);
                b.Hotkey = get();
                return;
            }
            set(hk);
            App.Current2.SaveSettings();
            App.Current2.RegisterHotkeys();
            MarkConflicts();
            if (id >= App.HkProfileBase || id == App.HkCycle) App.Current2.NotifyProfilesChanged();
        };
    }

    static string? FindOwner(Hotkey hk, int exceptId)
    {
        if (exceptId != App.HkCycle && hk.SameAs(S.CycleHotkey)) return "cycling profiles";
        if (exceptId != App.HkEmergency && hk.SameAs(S.EmergencyHotkey)) return "the emergency action";
        if (exceptId != App.HkBridge && hk.SameAs(S.BridgeToggleHotkey)) return "cursor alignment";
        if (exceptId != App.HkBlackout && hk.SameAs(S.BlackoutHotkey)) return "black out now";
        if (exceptId != App.HkIdleBlackout && hk.SameAs(S.IdleBlackoutToggleHotkey)) return "idle black-out";
        if (exceptId != App.HkTaskbar && hk.SameAs(S.TaskbarToggleHotkey)) return "the taskbar";
        for (int i = 0; i < S.Profiles.Count; i++)
            if (exceptId != App.HkProfileBase + i && hk.SameAs(S.Profiles[i].Hotkey)) return $"\"{S.Profiles[i].Name}\"";
        return null;
    }

    void MarkConflicts()
    {
        var failed = Core.Engine.FailedHotkeys;
        foreach (var (id, box) in _boxes) box.Conflict = failed.Contains(id) && box.Hotkey != null;
    }

    // ================================================================ cursor page

    void BridgeToggle_Click(object sender, RoutedEventArgs e) => App.Current2.SetBridge(BridgeToggle.IsChecked == true);

    void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeDesc();
        if (_loading || ModeBox.SelectedIndex < 0) return;
        S.BridgeMode = (BridgeMode)ModeBox.SelectedIndex;
        Core.Engine.SetBridge(S.CursorBridge, S.BridgeMode);
        App.Current2.SaveSettings();
    }

    void UpdateModeDesc()
    {
        if (ModeDesc == null) return;
        ModeDesc.Text = ModeBox.SelectedIndex <= 0
            ? "The whole edge of one screen maps onto the whole edge of the next: top to top, middle to middle, bottom to bottom. No dead zones. Best for screens of the same physical size, like a 27\" 4K at 150% next to a 27\" 1440p at 100%."
            : "Moves the same real distance using each screen's scaling. Best when the screens are different physical sizes. Parts of a longer edge land in the nearest corner.";
    }

    void DrawDiagram()
    {
        Diagram.Children.Clear();
        List<LiveMonitor> mons;
        try { mons = Monitors.Enumerate(withNames: true); } catch { return; }
        if (mons.Count == 0 || Diagram.ActualWidth < 50) return;

        int minX = mons.Min(m => m.Bounds.Left), minY = mons.Min(m => m.Bounds.Top);
        int maxX = mons.Max(m => m.Bounds.Right), maxY = mons.Max(m => m.Bounds.Bottom);
        double w = Diagram.ActualWidth, h = Diagram.ActualHeight;
        double k = Math.Min((w - 8) / (maxX - minX), (h - 8) / (maxY - minY));
        double ox = (w - (maxX - minX) * k) / 2, oy = (h - (maxY - minY) * k) / 2;

        foreach (var m in mons)
        {
            var b = new Border
            {
                Width = Math.Max(10, m.Bounds.Width * k - 6), Height = Math.Max(10, m.Bounds.Height * k - 6),
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(m.Primary ? 2 : 1),
            };
            b.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
            b.SetResourceReference(Border.BorderBrushProperty, m.Primary ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush");
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6) };
            sp.Children.Add(Txt(m.Name, 13, true));
            sp.Children.Add(Txt($"{m.Bounds.Width} × {m.Bounds.Height} at {Math.Round(m.Scale * 100)}%", 11, false));
            sp.Children.Add(Txt($"looks like {m.LogicalWidth} × {m.LogicalHeight}", 11, false, secondary: true));
            b.Child = new Viewbox { Child = sp, StretchDirection = StretchDirection.DownOnly };
            Canvas.SetLeft(b, ox + (m.Bounds.Left - minX) * k + 3);
            Canvas.SetTop(b, oy + (m.Bounds.Top - minY) * k + 3);
            Diagram.Children.Add(b);
        }

        var scales = mons.Select(m => Math.Round(m.Scale * 100)).Distinct().ToList();
        DiagramNote.Text = mons.Count < 2
            ? "Only one display is active, so there is nothing to align right now."
            : scales.Count > 1 || mons.Select(m => m.Bounds.Height).Distinct().Count() > 1
                ? "Windows lines these screens up in raw pixels, so without help the cursor sticks at part of the edge and lands at the wrong height. With alignment on, it lands at the matching spot on the next screen."
                : "These screens share the same scaling and size, so Windows already lines them up. Alignment stays ready for when you switch profiles.";
    }

    static TextBlock Txt(string s, double size, bool bold, bool secondary = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        return t;
    }

    // ================================================================ OLED care page

    void LoadOled()
    {
        IdleToggle.IsChecked = S.IdleBlackout;
        BusyToggle.IsChecked = S.BlackoutSkipWhenBusy;
        int idx = IdleMinutes.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => int.Parse((string)i.Tag) >= S.IdleBlackoutMinutes);
        IdleMinutes.SelectedIndex = idx < 0 ? IdleMinutes.Items.Count - 1 : idx;
        try { TaskbarToggle.IsChecked = Core.Taskbar.AutoHide; } catch { }
        BuildScreenChecks();
    }

    void BuildScreenChecks()
    {
        ScreenChecks.Children.Clear();
        var screens = new List<(string Path, string Label)>();
        try
        {
            var ids = Monitors.Identify();
            foreach (var m in Monitors.Enumerate())
                if (ids.TryGetValue(m.GdiName, out var id) && !screens.Any(x => x.Path.Equals(id.DevicePath, StringComparison.OrdinalIgnoreCase)))
                    screens.Add((id.DevicePath, id.Name));
        }
        catch (Exception ex) { Log.Write("Screen list failed: " + ex.Message); }
        // Chosen screens that are currently off still show up, so switching profiles doesn't lose the choice.
        foreach (var path in S.BlackoutMonitors)
            if (!screens.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                var name = S.Profiles.SelectMany(p => p.Monitors).FirstOrDefault(m => m.DevicePath.Equals(path, StringComparison.OrdinalIgnoreCase))?.Name ?? "Screen";
                screens.Add((path, name + " (off right now)"));
            }
        foreach (var (path, label) in screens)
        {
            var cb = new CheckBox
            {
                Content = label, Margin = new Thickness(0, 0, 24, 0), Tag = path,
                IsChecked = S.BlackoutMonitors.Contains(path, StringComparer.OrdinalIgnoreCase),
            };
            cb.Click += (_, _) =>
            {
                S.BlackoutMonitors = ScreenChecks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
                UpdateScreensDesc();
                App.Current2.ApplyBlackoutSettings();
                App.Current2.SaveSettings();
            };
            ScreenChecks.Children.Add(cb);
        }
        UpdateScreensDesc();
    }

    void UpdateScreensDesc() => ScreensDesc.Text = S.BlackoutMonitors.Count == 0
        ? "None ticked, so every screen is blacked out. Tick your OLED to protect only that one."
        : "Only the ticked screens are blacked out. Screens are recognised by their hardware ID, so this survives profile switches.";

    void Oled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || IdleMinutes.SelectedItem is not ComboBoxItem item) return;
        bool wasOn = S.IdleBlackout;
        S.IdleBlackout = IdleToggle.IsChecked == true;
        S.IdleBlackoutMinutes = int.Parse((string)item.Tag);
        S.BlackoutSkipWhenBusy = BusyToggle.IsChecked == true;
        App.Current2.ApplyBlackoutSettings();
        App.Current2.SaveSettings();
        if (S.IdleBlackout != wasOn) Log.Write($"Idle black-out {(S.IdleBlackout ? "on" : "off")} ({S.IdleBlackoutMinutes} min)");
    }

    void TryBlackout_Click(object sender, RoutedEventArgs e) => Core.Engine.ToggleBlackout();

    void TaskbarToggle_Click(object sender, RoutedEventArgs e) => App.Current2.SetTaskbarAutoHide(TaskbarToggle.IsChecked == true);

    // ================================================================ settings page

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ConfirmAfterSwitch = ConfirmToggle.IsChecked == true;
        S.ConfirmSeconds = int.Parse((string)((ComboBoxItem)ConfirmSecs.SelectedItem).Tag);
        S.ShowOsd = OsdToggle.IsChecked == true;
        S.AutoCheckUpdates = AutoUpdateToggle.IsChecked == true;
        S.StartMinimized = TrayToggle.IsChecked == true;
        var theme = (string)((ComboBoxItem)ThemeBox.SelectedItem).Tag;
        if (theme != S.Theme) { S.Theme = theme; App.Current2.ApplyTheme(); }
        App.Current2.SaveSettings();
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { App.StartWithWindows = StartupToggle.IsChecked == true; }
        catch (Exception ex) { Osd.Show("Could not change startup: " + ex.Message, Osd.Error); }
        StartupToggle.IsChecked = App.StartWithWindows;
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.Dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.Dir}\"") { UseShellExecute = true })?.Dispose();
    }

    void Exit_Click(object sender, RoutedEventArgs e) => App.Current2.ExitApp();

    // ================================================================ updates

    bool _updateBusy;

    public void SelectPage(int index) => Nav.SelectedIndex = index;

    void RefreshUpdateUI()
    {
        if (_updateBusy) return;
        var u = App.Current2.AvailableUpdate;
        var last = S.LastUpdateCheck is { } t ? $"Last checked {t:d MMM, HH:mm}." : "Not checked yet.";
        if (u != null)
        {
            UpdateTitle.Text = $"Version {u.Version} is available";
            UpdateText.Text = $"You have {Updater.Current}. Installing takes a few seconds: ScreenShift restarts by itself and keeps your profiles.";
            UpdateBtn.Content = "Install update";
            UpdateBtn.SetResourceReference(StyleProperty, "AccentButtonStyle");
            UpdateIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            NotesBtn.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateTitle.Text = "ScreenShift is up to date";
            UpdateText.Text = $"Version {Updater.Current}. {last}";
            UpdateBtn.Content = "Check for updates";
            UpdateBtn.ClearValue(StyleProperty);
            UpdateIcon.ClearValue(TextBlock.ForegroundProperty);
            NotesBtn.Visibility = Visibility.Collapsed;
        }
        UpdateBtn.IsEnabled = true;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateBtn.IsEnabled = false;
        string? error;
        try
        {
            if (App.Current2.AvailableUpdate == null)
            {
                UpdateText.Text = "Checking GitHub…";
                error = await App.Current2.CheckForUpdates(manual: true);
            }
            else
            {
                UpdateText.Text = "Downloading…";
                var progress = new Progress<double>(f => UpdateText.Text = $"Downloading… {f:P0}");
                error = await App.Current2.InstallUpdate(progress);
            }
        }
        finally { _updateBusy = false; }
        RefreshUpdateUI();
        if (error != null) UpdateText.Text = error;
    }

    void Notes_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current2.AvailableUpdate is { } u) Updater.OpenInBrowser(u.PageUrl);
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageProfiles == null) return;
        PageProfiles.Visibility = Nav.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageCursor.Visibility = Nav.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageOled.Visibility = Nav.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = Nav.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (Nav.SelectedIndex == 2) { _loading = true; LoadOled(); _loading = false; }
        if (Nav.SelectedIndex == 1) Dispatcher.BeginInvoke(DrawDiagram, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
