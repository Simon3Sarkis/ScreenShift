using System.Windows;
using System.Windows.Controls;
using ScreenShift.Core;
using ScreenShift.Native;

namespace ScreenShift.UI;

/// <summary>Windows 11-style tray flyout. Created once, then hidden/shown.</summary>
public partial class TrayFlyout : Window
{
    public TrayFlyout()
    {
        InitializeComponent();
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    void Reload()
    {
        var s = App.Settings;
        var items = s.Profiles.Select(p => new
        {
            Profile = p,
            p.Name,
            HotkeyText = p.Hotkey?.ToString() ?? "",
            IsActive = p.Id == s.LastProfileId,
            ActiveVisibility = p.Id == s.LastProfileId ? Visibility.Visible : Visibility.Collapsed,
        }).ToList();
        List.ItemsSource = items;
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Bridge.IsChecked = s.CursorBridge;
        var u = App.Current2.AvailableUpdate;
        UpdateRow.Visibility = u != null ? Visibility.Visible : Visibility.Collapsed;
        if (u != null) UpdateRowText.Text = $"Update available: {u.Version}";
    }

    void Update_Click(object sender, RoutedEventArgs e) { Hide(); App.Current2.ShowSettingsPage(); }

    public void ShowAt(POINT anchor)
    {
        Reload();
        Popup.ShowAt(this, anchor, (work, _, w, h) =>
        {
            int x = Math.Clamp(anchor.X - w / 2, work.Left + 12, work.Right - w - 12);
            int y = anchor.Y >= work.Bottom - 4 ? work.Bottom - h - 12      // taskbar at the bottom
                  : anchor.Y <= work.Top + 4 ? work.Top + 12                // taskbar at the top
                  : Math.Clamp(anchor.Y - h / 2, work.Top + 12, work.Bottom - h - 12);
            return new POINT(x, y);
        }, activate: true);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting && !Dispatcher.HasShutdownStarted) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    void Profile_Click(object sender, RoutedEventArgs e)
    {
        var p = (Profile)((Button)sender).Tag;
        Hide();
        _ = App.Current2.ApplyProfile(p);
    }

    void Bridge_Click(object sender, RoutedEventArgs e) => App.Current2.SetBridge(Bridge.IsChecked == true);
    void Blackout_Click(object sender, RoutedEventArgs e) { Hide(); Engine.ToggleBlackout(); }
    void Taskbar_Click(object sender, RoutedEventArgs e) { Hide(); App.Current2.SetTaskbarAutoHide(!Taskbar.AutoHide); }
    void Emergency_Click(object sender, RoutedEventArgs e) { Hide(); App.Current2.Emergency(); }
    void Settings_Click(object sender, RoutedEventArgs e) { Hide(); App.Current2.ShowMain(); }
    void Exit_Click(object sender, RoutedEventArgs e) { Hide(); App.Current2.ExitApp(); }
}
