using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpawnTimer.Core;
using SpawnTimer.Native;

namespace SpawnTimer.UI;

/// <summary>
/// Borderless, transparent, always-on-top window. Locked: click-through and never focused, so the game keeps
/// mouse and keyboard. Edit mode: drops WS_EX_TRANSPARENT so it can be dragged.
/// Shows over games in borderless/windowed-fullscreen; exclusive fullscreen cannot be overlaid by any app.
/// </summary>
public class OverlayWindow : Window
{
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd;
    private bool _editMode;
    private OverlaySettings? _settings;

    protected OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => NativeMethods.SetTopMost(_hwnd);

        MouseLeftButtonDown += OnMouseDown;
        LocationChanged += (_, _) =>
        {
            if (_settings == null || !IsLoaded) return;
            _settings.Left = Left;
            _settings.Top = Top;
        };
    }

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            ApplyExStyle();
            OnEditModeChanged(value);
        }
    }

    /// <summary>Attach position settings; places the window at the saved spot or the given default.</summary>
    public void BindPosition(OverlaySettings settings, double defaultLeft, double defaultTop)
    {
        _settings = settings;
        double left = settings.Left ?? defaultLeft, top = settings.Top ?? defaultTop;

        // Reset if the saved spot is off every screen (e.g. a monitor was unplugged).
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        if (left < vl - 50 || top < vt - 50 ||
            left > vl + SystemParameters.VirtualScreenWidth - 50 ||
            top > vt + SystemParameters.VirtualScreenHeight - 50)
        {
            left = defaultLeft;
            top = defaultTop;
        }
        Left = left;
        Top = top;
    }

    protected static Brush PanelBrush(double opacityPercent)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacityPercent, 0, 100) * 2.55);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 10, 12, 16));
        brush.Freeze();
        return brush;
    }

    protected virtual void OnEditModeChanged(bool editMode) { }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyExStyle();
        NativeMethods.SetTopMost(_hwnd);
        _topmostTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        base.OnClosed(e);
    }

    private void ApplyExStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        var style = NativeMethods.GetExStyle(_hwnd)
                    | NativeMethods.WS_EX_LAYERED
                    | NativeMethods.WS_EX_TOOLWINDOW
                    | NativeMethods.WS_EX_NOACTIVATE;
        style = _editMode ? style & ~NativeMethods.WS_EX_TRANSPARENT : style | NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetExStyle(_hwnd, style);
        IsHitTestVisible = _editMode;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Mouse was released before the drag began.
        }
    }
}
