using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

public partial class TimerOverlay : OverlayWindow
{
    private readonly ObservableCollection<SpawnTimerState> _timers;
    private OverlaySettings? _settings;

    public TimerOverlay(ObservableCollection<SpawnTimerState> timers)
    {
        InitializeComponent();
        _timers = timers;
        TimerList.ItemsSource = timers;
        timers.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    public void ApplyAppearance(OverlaySettings settings)
    {
        _settings = settings;
        Zoom.ScaleX = Zoom.ScaleY = settings.Scale;
        UpdateBackground();
    }

    protected override void OnEditModeChanged(bool editMode)
    {
        EditHint.Visibility = editMode ? Visibility.Visible : Visibility.Collapsed;
        Frame.BorderBrush = editMode ? new SolidColorBrush(Color.FromRgb(0x7F, 0xD4, 0xFF)) : Brushes.Transparent;
        UpdateBackground();
    }

    private void UpdateBackground()
    {
        // Always keep a little background while editing so there is something to grab.
        var opacity = _settings?.BackgroundOpacity ?? 55;
        Frame.Background = PanelBrush(EditMode ? System.Math.Max(opacity, 40) : opacity);
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = _timers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
