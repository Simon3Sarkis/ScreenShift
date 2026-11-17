using System.Windows;
using System.Windows.Threading;

namespace ScreenShift.UI;

/// <summary>"Keep these display settings?" with auto-revert, like Windows' own dialog.
/// If the new layout leaves you with a black screen, doing nothing reverts it.
/// One instance is reused for the whole session.</summary>
public partial class ConfirmWindow : Window
{
    static ConfirmWindow? _instance;
    TaskCompletionSource<bool>? _tcs;
    readonly DispatcherTimer _timer;
    int _left;

    ConfirmWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            if (--_left <= 0) Finish(false); else UpdateText();
        }, Dispatcher);
        _timer.Stop();
    }

    public static Task<bool> Ask(string profile, int seconds)
    {
        var w = _instance ??= new ConfirmWindow();
        w._tcs?.TrySetResult(false);
        w._tcs = new TaskCompletionSource<bool>();
        w._left = seconds;
        w.Body.Text = $"Switched to \"{profile}\". Press Enter to keep it, or Esc to go back.";
        w.UpdateText();
        w._timer.Start();
        Popup.ShowAt(w, Popup.PrimaryCenter(), Popup.Center, activate: true);
        w.Dispatcher.BeginInvoke(() => w.KeepBtn.Focus(), DispatcherPriority.Input);
        return w._tcs.Task;
    }

    void UpdateText() => Countdown.Text = $"Reverting to the previous layout in {_left} second{(_left == 1 ? "" : "s")}.";

    void Finish(bool keep)
    {
        _timer.Stop();
        _tcs?.TrySetResult(keep);
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.IsExiting && !Dispatcher.HasShutdownStarted) { e.Cancel = true; Finish(false); }   // Alt+F4 = revert
        base.OnClosing(e);
    }

    void Keep_Click(object sender, RoutedEventArgs e) => Finish(true);
    void Revert_Click(object sender, RoutedEventArgs e) => Finish(false);
}
