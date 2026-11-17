using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenShift.Core;
using ScreenShift.Native;

namespace ScreenShift.UI;

/// <summary>Click, then press a key combination. Backspace/Delete clears, Esc cancels.</summary>
public sealed class HotkeyBox : TextBox
{
    Hotkey? _hotkey;
    bool _conflict;

    public event Action<HotkeyBox, Hotkey?>? HotkeyChanged;

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));   // use the Windows 11 TextBox style
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Width = 200;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip = "Click and press a shortcut. Backspace clears it.";
        InputMethod.SetIsInputMethodEnabled(this, false);
        Render();
    }

    public Hotkey? Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; Render(); }
    }

    public bool Conflict
    {
        get => _conflict;
        set
        {
            _conflict = value;
            if (value) SetResourceReference(BorderBrushProperty, "SystemFillColorCriticalBrush");
            else ClearValue(BorderBrushProperty);
            ToolTip = value ? "Another app already uses this shortcut. Pick a different one." : "Click and press a shortcut. Backspace clears it.";
        }
    }

    void Render() => Text = IsKeyboardFocused ? "Press a shortcut…" : _hotkey?.ToString() ?? "None";

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        App.Current2.SuspendHotkeys();
        Render();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        App.Current2.RegisterHotkeys();
        Render();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        var mods = Keyboard.Modifiers;

        if (key == Key.Tab && mods == ModifierKeys.None) return;   // keep keyboard navigation
        e.Handled = true;

        if (key == Key.Escape) { MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); return; }
        if ((key is Key.Back or Key.Delete) && mods == ModifierKeys.None)
        {
            _hotkey = null;
            HotkeyChanged?.Invoke(this, null);
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        uint m = 0;
        if (mods.HasFlag(ModifierKeys.Control)) m |= Win32.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= Win32.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= Win32.MOD_SHIFT;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) m |= Win32.MOD_WIN;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            var parts = new List<string>();
            if ((m & Win32.MOD_WIN) != 0) parts.Add("Win");
            if ((m & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((m & Win32.MOD_ALT) != 0) parts.Add("Alt");
            if ((m & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
            Text = parts.Count == 0 ? "Press a shortcut…" : string.Join(" + ", parts) + " + …";
            return;
        }

        bool fKey = key is >= Key.F1 and <= Key.F24;
        if (m == 0 && !fKey) { Text = "Add Ctrl, Alt, Shift or Win"; return; }

        _hotkey = new Hotkey { Modifiers = m, Key = (uint)KeyInterop.VirtualKeyFromKey(key) };
        HotkeyChanged?.Invoke(this, _hotkey);
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
