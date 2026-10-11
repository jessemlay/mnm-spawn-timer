using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

public enum AlertKind { Drop, NamedDown, SpawnDue, TimerUp }

public sealed class AlertMessage
{
    public string Tag { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public Brush Accent { get; init; } = Brushes.White;
    public Brush Background { get; init; } = Brushes.Black;
    public DateTime ExpiresUtc { get; init; }
}

/// <summary>Stack of short-lived alert banners (item drops, named mobs, timers up).</summary>
public partial class AlertOverlay : OverlayWindow
{
    private const int MaxMessages = 4;
    private readonly ObservableCollection<AlertMessage> _messages = new();
    private OverlaySettings? _settings;

    public AlertOverlay()
    {
        InitializeComponent();
        MessageList.ItemsSource = _messages;
    }

    public void ApplyAppearance(OverlaySettings settings)
    {
        _settings = settings;
        Zoom.ScaleX = Zoom.ScaleY = settings.Scale;
    }

    public void Push(AlertKind kind, string title, string detail, int seconds)
    {
        var (tag, accent) = kind switch
        {
            AlertKind.Drop => ("ITEM DROP", Color.FromRgb(0xC7, 0x8B, 0xFF)),
            AlertKind.NamedDown => ("NAMED DOWN", Color.FromRgb(0xFF, 0x9F, 0x43)),
            AlertKind.SpawnDue => ("NAMED SPAWN DUE", Color.FromRgb(0xFF, 0x5C, 0x5C)),
            _ => ("TIMER UP", Color.FromRgb(0x7F, 0xD4, 0xFF)),
        };
        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();

        _messages.Insert(0, new AlertMessage
        {
            Tag = tag,
            Title = title,
            Detail = detail,
            Accent = accentBrush,
            Background = PanelBrush(_settings?.BackgroundOpacity ?? 75),
            ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds)),
        });
        while (_messages.Count > MaxMessages) _messages.RemoveAt(_messages.Count - 1);
    }

    public void Tick(DateTime nowUtc)
    {
        foreach (var expired in _messages.Where(m => m.ExpiresUtc <= nowUtc).ToList()) _messages.Remove(expired);
    }

    protected override void OnEditModeChanged(bool editMode)
    {
        Placeholder.Visibility = editMode ? Visibility.Visible : Visibility.Collapsed;
        Frame.BorderBrush = editMode ? new SolidColorBrush(Color.FromRgb(0x7F, 0xD4, 0xFF)) : Brushes.Transparent;
    }
}
