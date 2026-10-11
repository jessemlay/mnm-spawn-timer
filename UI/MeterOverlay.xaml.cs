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
        HeaderRight.Text = meter.ShowSessionTime ? TimeFormat.Clock(stats.Elapsed) : "";

        if (!ledgerEnabled)
        {
            AddNote("Game data is off. Turn it on in Settings ▸ Game data.");
            return;
        }
        if (!stats.HasData)
        {
            AddNote(ledgerStatus);
            AddNote("Waiting for loot…");
        }

        if (meter.ShowZone && stats.Zone.Length > 0) AddRow("Zone", PrettyZone(stats.Zone));
        if (meter.ShowKills)
            AddRow("Corpses looted", meter.ShowKillRate ? $"{stats.Kills}   ({stats.PerHour(stats.Kills):0}/hr)" : $"{stats.Kills}");
        if (meter.ShowCoin)
        {
            // Rates over the first few minutes swing wildly, so wait before showing one.
            var rate = stats.Elapsed < TimeSpan.FromMinutes(5)
                ? "(…/hr)"
                : $"({CoinFormat.Format((long)stats.PerHour(stats.LootCoinCp))}/hr)";
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

        if (meter.ShowRecentLoot && meter.RecentLootCount > 0 && stats.RecentLoot.Count > 0)
        {
            AddSection("RECENT LOOT");
            foreach (var loot in stats.RecentLoot.Take(meter.RecentLootCount))
            {
                var qty = loot.Quantity > 1 ? $" ×{loot.Quantity}" : "";
                var who = loot.IsMine ? "" : $" · {loot.Actor}";
                AddLine($"{loot.Item}{qty}", $"{loot.Corpse}{who}");
            }
        }
    }

    protected override void OnEditModeChanged(bool editMode)
    {
        Header.Text = editMode ? "SESSION · drag to move" : "SESSION";
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

    private static string PrettyZone(string zone) =>
        zone.Length == 0 ? zone : char.ToUpperInvariant(zone[0]) + zone[1..];

    private static Brush Frozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
