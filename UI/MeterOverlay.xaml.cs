using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

/// <summary>Configurable session meter built from the game's Ledger (loot, coin, levels).</summary>
public partial class MeterOverlay : OverlayWindow
{
    private static readonly Brush LabelBrush = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ValueBrush = Brushes.White;
    private static readonly Brush SectionBrush = Frozen(Color.FromRgb(0x7F, 0xD4, 0xFF));
    private static readonly Brush DimBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private OverlaySettings? _settings;

    public MeterOverlay()
    {
        InitializeComponent();
    }

    public void ApplyAppearance(OverlaySettings settings)
    {
        _settings = settings;
        Zoom.ScaleX = Zoom.ScaleY = settings.Scale;
        UpdateBackground();
    }

    public void Refresh(SessionStats stats, MeterSettings meter, bool ledgerEnabled, string ledgerStatus)
    {
        Rows.Children.Clear();
        HeaderRight.Text = meter.ShowSessionTime ? SessionLabel(stats) : "";

        if (!ledgerEnabled)
        {
            AddNote("Game data is off. Turn it on in Settings ▸ Game data.");
            return;
        }
        if (stats.IsEnded && !stats.HasData)
        {
            AddNote("Waiting for Monsters & Memories to start…");
            return;
        }
        if (stats.IsEnded) AddNote("Game closed · last session's totals");
        else if (!stats.HasData)
        {
            AddNote(ledgerStatus);
            AddNote("Waiting for loot…");
        }

        // Rates follow your recent pace and fade to 0 when you stop (RateWindowMinutes; 0 = session average).
        // The first few minutes swing wildly, so wait before showing one; an ended session has no live rate.
        var now = DateTimeOffset.Now;
        var window = TimeSpan.FromMinutes(Math.Max(0, meter.RateWindowMinutes));
        var warmingUp = stats.Elapsed < TimeSpan.FromMinutes(5);
        string RateText(string live) => stats.IsEnded ? "(ended)" : warmingUp ? "(…/hr)" : live;

        if (meter.ShowZone && stats.Zone.Length > 0) AddRow("Zone", PrettyZone(stats.Zone));
        if (meter.ShowKills)
        {
            var killRate = RateText($"({stats.CorpsesPerHour(window, now):0}/hr)");
            AddRow("Corpses looted", meter.ShowKillRate ? $"{stats.Kills}   {killRate}" : $"{stats.Kills}");
        }
        if (meter.ShowCoin)
        {
            var rate = RateText($"({CoinFormat.Format((long)Math.Round(stats.CoinPerHour(window, now)))}/hr)");
            var label = stats.PartySize > 1 ? $"Coin looted ({stats.PartySize}-way)" : "Coin looted";
            AddRow(label, meter.ShowCoinRate ? $"{CoinFormat.Format(stats.LootCoinCp)}   {rate}" : CoinFormat.Format(stats.LootCoinCp));
        }
        if (meter.ShowItems) AddRow("Items looted", stats.ItemsLooted.ToString(CultureInfo.InvariantCulture));
        if (meter.ShowLevels && (stats.LevelsGained > 0 || stats.Level.HasValue))
            AddRow("Levels gained", stats.Level.HasValue ? $"{stats.LevelsGained}  (now {stats.Level})" : $"{stats.LevelsGained}");

        if (meter.ShowTopMobs && meter.TopMobsCount > 0)
        {
            var top = stats.TopMobs(meter.TopMobsCount).ToList();
            if (top.Count > 0)
            {
                AddSection("TOP MOBS");
                foreach (var (name, count) in top) AddRow(name, $"×{count}");
            }
        }

        if (meter.ShowRecentLoot && meter.RecentLootCount > 0)
        {
            // Loot drops off after RecentLootMinutes so the list doesn't linger once you stop (0 = keep it).
            var maxAge = TimeSpan.FromMinutes(Math.Max(0, meter.RecentLootMinutes));
            var recent = stats.RecentLoot
                .Where(l => maxAge == TimeSpan.Zero || now - l.Time <= maxAge)
                .Take(meter.RecentLootCount)
                .ToList();
            if (recent.Count > 0)
            {
                AddSection("RECENT LOOT");
                foreach (var loot in recent)
                {
                    var qty = loot.Quantity > 1 ? $" ×{loot.Quantity}" : "";
                    var who = loot.IsMine ? "" : $" · {loot.Actor}";
                    AddLine($"{loot.Item}{qty}", $"{loot.Corpse}{who} · {TimeFormat.Ago(now - loot.Time)}");
                }
            }
        }
    }

    /// <summary>Raised when the Restart session button is clicked.</summary>
    public event Action? RestartRequested;

    private void RestartSession_Click(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();

    protected override void OnEditModeChanged(bool editMode)
    {
        Controls.Visibility = editMode ? Visibility.Visible : Visibility.Collapsed;
        Header.Text = "SESSION";
        Frame.BorderBrush = editMode ? SectionBrush : Brushes.Transparent;
        UpdateBackground();
    }

    private void UpdateBackground()
    {
        var opacity = _settings?.BackgroundOpacity ?? 55;
        Frame.Background = PanelBrush(EditMode ? Math.Max(opacity, 40) : opacity);
    }

    private void AddRow(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var v = new TextBlock { Text = value, Foreground = ValueBrush, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        row.Children.Add(new TextBlock { Text = label, Foreground = LabelBrush, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        Rows.Children.Add(row);
    }

    private void AddLine(string main, string detail)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 2) };
        panel.Children.Add(new TextBlock { Text = main, Foreground = ValueBrush, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = detail, Foreground = DimBrush, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        Rows.Children.Add(panel);
    }

    private void AddSection(string title) =>
        Rows.Children.Add(new TextBlock { Text = title, Foreground = SectionBrush, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 2) });

    private void AddNote(string text) =>
        Rows.Children.Add(new TextBlock { Text = text, Foreground = DimBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });

    /// <summary>"since 12:11 PM · 33m" (or "since Sat 7:56 AM · 2d 4h" for an older session).</summary>
    private static string SessionLabel(SessionStats stats)
    {
        var start = stats.EndTime?.LocalDateTime ?? stats.Start.LocalDateTime;
        // "t" is only the short-time pattern on its own; inside "ddd t" it would mean the A/P designator.
        var when = start.Date == DateTime.Today ? start.ToString("t") : $"{start:ddd} {start:t}";
        var prefix = stats.IsEnded ? "ended" : "since";
        var e = stats.Elapsed;
        var length = e.TotalDays >= 1 ? $"{(int)e.TotalDays}d {e.Hours}h"
            : e.TotalHours >= 1 ? $"{(int)e.TotalHours}h {e.Minutes:00}m"
            : $"{(int)e.TotalMinutes}m";
        return $"{prefix} {when} · {length}";
    }

    private static string PrettyZone(string zone) =>
        zone.Length == 0 ? zone : char.ToUpperInvariant(zone[0]) + zone[1..];

    private static Brush Frozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
