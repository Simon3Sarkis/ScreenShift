using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenShift.Native;

namespace ScreenShift.UI;

/// <summary>Small Windows 11-style on-screen notification (like the volume flyout). Never steals focus.
/// A single window is reused (hidden between messages).</summary>
public static class Osd
{
    public const string Display = "", Check = "", Error = "", Mouse = "", Undo = "";

    static Window? _win;
    static TextBlock? _icon, _text;
    static DispatcherTimer? _timer;

    public static void Show(string message, string glyph, bool sticky = false, double seconds = 1.8)
    {
        bool isError = glyph == Error;
        if (!App.Settings.ShowOsd && !isError && !sticky) return;
        if (!App.Settings.ShowOsd && sticky) return;

        if (_win == null) Create();
        _icon!.Text = glyph;
        _icon.SetResourceReference(TextBlock.ForegroundProperty, isError ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");
        _text!.Text = message;

        _timer!.Stop();
        if (!sticky) { _timer.Interval = TimeSpan.FromSeconds(seconds); _timer.Start(); }

        if (!_win!.IsVisible)
            Popup.ShowAt(_win, Popup.CursorPos(), (work, _, w, h) => new POINT(work.Left + (work.Width - w) / 2, work.Bottom - h - work.Height / 12), activate: false);
    }

    public static void Hide()
    {
        _timer?.Stop();
        _win?.Hide();
    }

    static void Create()
    {
        _icon = new TextBlock { FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _text = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460, TextWrapping = TextWrapping.Wrap };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 12, 20, 12) };
        panel.Children.Add(_icon);
        panel.Children.Add(_text);
        var border = new Border { Child = panel, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");

        _win = new Window
        {
            Content = border, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Topmost = true,
            Focusable = false, ShowActivated = false, Title = "ScreenShift notification",
        };
        _win.SourceInitialized += (_, _) =>
            Win32.AddExStyle(new WindowInteropHelper(_win).Handle, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT);
        _timer ??= new DispatcherTimer(DispatcherPriority.Normal) { };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
    }

    static void OnTick(object? s, EventArgs e) { _timer!.Stop(); _win?.Hide(); }
}
