using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

/// <summary>What the item lookup overlay shows; built by <see cref="ItemLookupController"/>.</summary>
public sealed class ItemLookupView
{
    /// <summary>"ITEM LOOKUP" or "MOB LOOKUP".</summary>
    public string Heading { get; init; } = "ITEM LOOKUP";
    public string Typed { get; init; } = "";
    public string Ghost { get; init; } = "";
    public string ZoneLabel { get; init; } = "";
    public string Hint { get; init; } = "";
    public IReadOnlyList<(string Title, string Tag)> Results { get; init; } = Array.Empty<(string, string)>();
    /// <summary>Index within <see cref="Results"/> (the visible window), or -1.</summary>
    public int Selected { get; init; } = -1;
    public int HiddenAbove { get; init; }
    public int HiddenBelow { get; init; }
    /// <summary>e.g. "13 of 34" when the list scrolls.</summary>
    public string Position { get; init; } = "";
    public ItemDetails? Details { get; init; }
    /// <summary>Mob lookup: the highlighted mob (shown instead of <see cref="Details"/>).</summary>
    public MobInfo? Mob { get; init; }
    /// <summary>Mob card: item → usable by your class (null = unknown), for the drops list.</summary>
    public Func<string, bool?>? UsableByYou { get; init; }
    public string CardMessage { get; init; } = "";
    public string YourClass { get; init; } = "";
}

public partial class ItemSearchOverlay : OverlayWindow
{
    private static readonly Brush Gold = Frozen(Color.FromRgb(0xFF, 0xC8, 0x57));
    private static readonly Brush Text = Brushes.White;
    private static readonly Brush Dim = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x7F, 0xD4, 0xFF));
    private static readonly Brush Good = Frozen(Color.FromRgb(0x7C, 0xE0, 0x8A));
    private static readonly Brush Bad = Frozen(Color.FromRgb(0xFF, 0x7A, 0x7A));
    private static readonly Brush SelectedRow = Frozen(Color.FromArgb(0x55, 0x7F, 0xD4, 0xFF));

    private OverlaySettings? _settings;

    public ItemSearchOverlay()
    {
        InitializeComponent();
        // Wheel over the list scrolls it (positive = up). The panel has a transparent background so the gaps catch it too.
        Results.Background = Brushes.Transparent;
        Results.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            Scrolled?.Invoke(e.Delta / 120);
        };
    }

    /// <summary>Raised when a result row is clicked (index within the visible rows).</summary>
    public event Action<int>? RowClicked;

    /// <summary>Raised on mouse wheel over the list, in notches (positive = up).</summary>
    public event Action<int>? Scrolled;

    /// <summary>Raised when the card's "Open on M&amp;M Wiki" link is clicked, with the wiki page title.</summary>
    public event Action<string>? OpenSourceClicked;

    /// <summary>Raised when a drop on the mob card is clicked, with the item's title.</summary>
    public event Action<string>? DropClicked;

    public void ApplyAppearance(OverlaySettings settings)
    {
        _settings = settings;
        Zoom.ScaleX = Zoom.ScaleY = settings.Scale;
        Frame.Background = PanelBrush(Math.Max(settings.BackgroundOpacity, 60));
    }

    public void Render(ItemLookupView view)
    {
        Heading.Text = view.Heading;
        Typed.Text = view.Typed;
        Ghost.Text = view.Ghost;
        ZoneLabel.Text = view.ZoneLabel;
        Hint.Text = view.Hint;

        Results.Children.Clear();
        if (view.HiddenAbove > 0) Results.Children.Add(MoreLine($"▲ {view.HiddenAbove} more", ""));
        for (var i = 0; i < view.Results.Count; i++)
        {
            var (title, tag) = view.Results[i];
            var index = i;
            var row = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 3, 6, 3),
                Background = i == view.Selected ? SelectedRow : Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            var dock = new DockPanel();
            var tagBlock = new TextBlock { Text = tag, Foreground = Dim, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tagBlock, Dock.Right);
            dock.Children.Add(tagBlock);
            dock.Children.Add(new TextBlock { Text = title, Foreground = Text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Child = dock;
            // Handle the press here so it selects instead of starting a window drag.
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                RowClicked?.Invoke(index);
            };
            Results.Children.Add(row);
        }
        if (view.HiddenBelow > 0 || view.Position.Length > 0)
            Results.Children.Add(MoreLine(view.HiddenBelow > 0 ? $"▼ {view.HiddenBelow} more" : "", view.Position));

        RenderCard(view);
    }

    private static DockPanel MoreLine(string left, string right)
    {
        var dock = new DockPanel { Margin = new Thickness(6, 2, 6, 2) };
        var r = new TextBlock { Text = right, Foreground = Dim, FontSize = 10.5 };
        DockPanel.SetDock(r, Dock.Right);
        dock.Children.Add(r);
        dock.Children.Add(new TextBlock { Text = left, Foreground = Accent, FontSize = 10.5 });
        return dock;
    }

    private void RenderCard(ItemLookupView view)
    {
        Card.Children.Clear();
        if (view.Mob != null)
        {
            RenderMobCard(view.Mob, view.UsableByYou);
            return;
        }
        var d = view.Details;
        if (d == null)
        {
            CardBorder.Visibility = view.CardMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (view.CardMessage.Length > 0) AddLine(view.CardMessage, Dim, 12);
            return;
        }
        CardBorder.Visibility = Visibility.Visible;

        AddLine(d.Name, Gold, 15, FontWeights.SemiBold);
        var flags = new[] { d.Magic ? "MAGIC" : null, d.Lore ? "LORE" : null, d.NoDrop ? "NO DROP" : null }.Where(f => f != null);
        var flagText = string.Join("  ", flags);
        if (flagText.Length > 0) AddLine(flagText, Accent, 10.5, FontWeights.Bold);

        if (view.YourClass.Length > 0 && d.Classes.Length > 0)
        {
            var usable = d.UsableBy(view.YourClass);
            AddLine(usable ? $"✓ Usable by {view.YourClass.ToUpperInvariant()}" : $"✗ Not usable by {view.YourClass.ToUpperInvariant()}",
                usable ? Good : Bad, 12, FontWeights.SemiBold);
        }

        AddGap();
        if (d.Slot.Length > 0) AddPair("Slot", d.Slot + (d.Handed.Length > 0 ? $" ({d.Handed})" : ""));
        if (d.Skill.Length > 0) AddPair("Skill", d.Skill);
        if (d.Damage.Length > 0 || d.Delay.Length > 0) AddPair("Damage / Delay", $"{(d.Damage.Length > 0 ? d.Damage : "?")} / {(d.Delay.Length > 0 ? d.Delay : "?")}");
        if (d.Ac.Length > 0) AddPair("AC", d.Ac);

        if (d.Stats.Count > 0)
        {
            AddGap();
            var wrap = new WrapPanel();
            foreach (var stat in d.Stats)
            {
                wrap.Children.Add(new TextBlock
                {
                    Text = $"{stat[0]} {stat[1]}",
                    Foreground = Text,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 1, 10, 1),
                });
            }
            Card.Children.Add(wrap);
        }

        if (d.Effect.Length > 0)
        {
            AddGap();
            AddLine("Effect: " + d.Effect, Accent, 12);
        }
        if (d.Description.Length > 0) AddLine(d.Description, Dim, 11.5);

        AddGap();
        if (d.Classes.Length > 0) AddClassLine(d.Classes, view.YourClass);
        if (d.Races.Length > 0) AddPair("Race", d.Races);
        var weight = string.Join(" · ", new[] { d.Weight.Length > 0 ? $"WT {d.Weight}" : null, d.Size.Length > 0 ? d.Size : null }.Where(s => s != null));
        if (weight.Length > 0) AddLine(weight, Dim, 11.5);

        if (d.DropsFrom.Count > 0)
        {
            AddGap();
            AddLine("Drops from", Accent, 10.5, FontWeights.Bold);
            foreach (var source in d.DropsFrom.Take(4)) AddLine(source, Text, 12);
            if (d.DropsFrom.Count > 4) AddLine($"+{d.DropsFrom.Count - 4} more", Dim, 11);
        }

        AddGap();
        AddWikiLink(d.Title, "Open this item's wiki page in your browser (Ctrl+O)");
        AddLine("M&M Wiki on Miraheze (CC BY-SA 4.0) · Ctrl+O", Dim, 9.5);
    }

    private const int MaxDropRows = 16;

    private void RenderMobCard(MobInfo mob, Func<string, bool?>? usableByYou)
    {
        CardBorder.Visibility = Visibility.Visible;
        AddLine(mob.Name, Gold, 15, FontWeights.SemiBold);
        if (mob.IsNamed) AddLine("NAMED", Accent, 10.5, FontWeights.Bold);
        AddGap();
        if (!string.IsNullOrEmpty(mob.Level)) AddPair("Level", mob.Level);
        if (mob.Zone.Length > 0) AddPair("Zone", mob.Zone);

        AddGap();
        AddLine(mob.Drops.Count > 0 ? $"Drops ({mob.Drops.Count})" : "Drops", Accent, 10.5, FontWeights.Bold);
        if (mob.Drops.Count == 0) AddLine("None listed on the wiki.", Dim, 12);
        foreach (var drop in mob.Drops.Take(MaxDropRows))
        {
            var item = drop.Name;
            var usable = usableByYou?.Invoke(item);
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var slot = new TextBlock { Text = drop.Slot ?? "", Foreground = Dim, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(slot, Dock.Right);
            row.Children.Add(slot);
            var mark = new TextBlock
            {
                Text = usable switch { true => "✓", false => "✗", _ => "" },
                Foreground = usable == true ? Good : Bad,
                FontSize = 12,
                Width = usableByYou == null ? 0 : 14,
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            var name = new TextBlock { Text = item, Foreground = usable == false ? Dim : Text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(name);
            row.MouseEnter += (_, _) => name.TextDecorations = TextDecorations.Underline;
            row.MouseLeave += (_, _) => name.TextDecorations = null;
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; // a click, not a window drag
                DropClicked?.Invoke(item);
            };
            Card.Children.Add(row);
        }
        if (mob.Drops.Count > MaxDropRows) AddLine($"+{mob.Drops.Count - MaxDropRows} more (on the wiki page)", Dim, 11);

        AddGap();
        AddWikiLink(mob.Name, "Open this mob's wiki page in your browser (Ctrl+O)");
        AddLine((mob.Drops.Count > 0 ? "Click a drop to see its stats · " : "") + "M&M Wiki (CC BY-SA 4.0) · Ctrl+O", Dim, 9.5);
    }

    private void AddWikiLink(string title, string tooltip)
    {
        var link = new TextBlock
        {
            Text = "Open on M&M Wiki ↗",
            Foreground = Accent,
            FontSize = 12,
            TextDecorations = TextDecorations.Underline,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Margin = new Thickness(0, 1, 0, 0),
        };
        link.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // a click, not a window drag
            OpenSourceClicked?.Invoke(title);
        };
        Card.Children.Add(link);
    }

    private void AddClassLine(string classes, string yourClass)
    {
        var block = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        block.Inlines.Add(new System.Windows.Documents.Run("Class  ") { Foreground = Dim });
        foreach (var code in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var mine = yourClass.Length > 0 && code.Equals(yourClass.Trim(), StringComparison.OrdinalIgnoreCase);
            block.Inlines.Add(new System.Windows.Documents.Run(code + " ")
            {
                Foreground = mine ? Good : Text,
                FontWeight = mine ? FontWeights.Bold : FontWeights.Normal,
            });
        }
        Card.Children.Add(block);
    }

    private void AddPair(string label, string value)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var v = new TextBlock { Text = value, Foreground = Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(v, Dock.Right);
        dock.Children.Add(v);
        dock.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 12.5 });
        Card.Children.Add(dock);
    }

    private void AddLine(string text, Brush brush, double size, FontWeight? weight = null) =>
        Card.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
        });

    private void AddGap() => Card.Children.Add(new Border { Height = 5 });

    protected override void OnEditModeChanged(bool editMode) { }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
