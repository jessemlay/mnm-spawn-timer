using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

/// <summary>Click, then press a key combination to record it. Backspace or Delete clears.</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HotkeyBox)d).ShowValue()));

    /// <summary>Raised with true while any HotkeyBox has focus, so global hotkeys can pause.</summary>
    public static event Action<bool>? CapturingChanged;

    private static readonly Brush CaptureBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xD6));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press a key combination. Backspace or Delete clears it.";
        ShowValue();
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key == Key.Tab && mods == ModifierKeys.None) return; // keep Tab for navigation

        e.Handled = true;
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None:
                return;
            case Key.Back or Key.Delete when mods == ModifierKeys.None:
                Value = "";
                return;
            case Key.Escape:
                Keyboard.ClearFocus();
                return;
            default:
                Value = new Hotkey(mods, key).ToString();
                return;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Background = CaptureBrush;
        Text = "Press a key combination…";
        CapturingChanged?.Invoke(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ClearValue(BackgroundProperty);
        ShowValue();
        CapturingChanged?.Invoke(false);
    }

    private void ShowValue()
    {
        var hotkey = Hotkey.Parse(Value);
        Text = hotkey.IsEmpty ? "(none)" : hotkey.ToString();
        if (IsKeyboardFocused) Text += "   (press another combo to change)";
    }
}
