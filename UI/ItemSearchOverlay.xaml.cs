using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

/// <summary>The mob card's TIMER section: the timer the actions apply to and how this mob is linked to it.</summary>
public sealed class MobTimerView
{
    /// <summary>Name of the target timer, or null when there are no timers yet.</summary>
    public string? Target { get; init; }
    /// <summary>"2 of 3" when there's more than one timer to choose from.</summary>
    public string Position { get; init; } = "";
    public bool Restarts { get; init; }
    public bool IsPlaceholder { get; init; }
    /// <summary>The link is this exact name (a wildcard line like "a skeletal*" can't be removed per mob).</summary>
    public bool CanRemoveRestart { get; init; }
    public bool CanRemovePlaceholder { get; init; }
    /// <summary>No timer restarts this mob yet, so offer to create one.</summary>
    public bool CanCreate { get; init; }
    /// <summary>Links to other timers, e.g. "Also: placeholder for Night Terror".</summary>
    public string Elsewhere { get; init; } = "";
}

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
    /// <summary>Mob card: the TIMER section (null hides it).</summary>
    public MobTimerView? MobTimers { get; init; }
    /// <summary>Mob card: the page its link opens (the wiki, or MnM Archive while the wiki is down).</summary>
    public string MobPageUrl { get; init; } = "";
    /// <summary>Mob card: path of the mob's cached wiki thumbnail, or null.</summary>
    public string? MobImage { get; init; }
    /// <summary>Mob card: item → usable by your class (null = unknown), for the drops list.</summary>
    public Func<string, bool?>? UsableByYou { get; init; }
    /// <summary>Your merchant price for an item (null = never sold); shown on both cards.</summary>
    public Func<string, ItemPrice?>? PriceOf { get; init; }
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

    /// <summary>Raised when a card's "Open on …" link (or the mob picture) is clicked, with the page address.</summary>
    public event Action<string>? OpenSourceClicked;

    /// <summary>Raised when a drop on the mob card is clicked, with the item's title.</summary>
    public event Action<string>? DropClicked;

    /// <summary>Raised by the mob card's TIMER actions.</summary>
    public event Action<MobTimerAction>? TimerActionClicked;

    /// <summary>Raised by the TIMER section's ‹ › (−1 or +1) to pick another timer.</summary>
    public event Action<int>? TimerTargetClicked;

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
            RenderMobCard(view.Mob, view.MobImage, view.MobPageUrl, view.UsableByYou, view.PriceOf, view.MobTimers);
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

        if ((view.PriceOf?.Invoke(d.Name) ?? view.PriceOf?.Invoke(d.Title)) is { } price)
        {
            AddGap();
            AddPair("Sells for", PriceText(price.UnitCp), Gold);
            AddLine($"your sale, {price.LastSold.LocalDateTime:MMM d}" + (price.Sales > 1 ? $" · sold {price.Sales}×" : ""), Dim, 10.5);
        }

        if (d.DropsFrom.Count > 0)
        {
            AddGap();
            AddLine("Drops from", Accent, 10.5, FontWeights.Bold);
            foreach (var source in d.DropsFrom.Take(4)) AddLine(source, Text, 12);
            if (d.DropsFrom.Count > 4) AddLine($"+{d.DropsFrom.Count - 4} more", Dim, 11);
        }

        AddGap();
        // While the wiki is down: a saved copy (dated) or MnM Archive's page, credited and linked.
        if (d.IsStale) AddLine($"Saved {d.FetchedUtc.ToLocalTime():MMM d} · the wiki is offline", Gold, 10.5);
        var url = ItemDb.PageUrl(d.Title);
        AddSourceLink(url, "Open this item's page in your browser (Ctrl+O)");
        AddLine(Credit(url) + " · Ctrl\u2060+\u2060O", Dim, 9.5);
    }

    private static bool IsArchive(string url) => url.StartsWith(MnmArchive.Base, StringComparison.OrdinalIgnoreCase);

    private static string Credit(string url) => IsArchive(url) ? MnmArchive.Credit : "M&M Wiki on Miraheze (CC BY-SA 4.0)";

    private const int MaxDropRows = 12;

    /// <summary>
    /// TIMER: which timer the actions apply to (‹ › picks another), whether this mob restarts it or is its
    /// placeholder, and the actions with their keys. Done links show ✓ and a small "remove".
    /// </summary>
    private void RenderTimerSection(MobTimerView t, bool named)
    {
        AddGap();
        var head = new DockPanel();
        if (t.Position.Length > 0)
        {
            var pick = new StackPanel { Orientation = Orientation.Horizontal };
            pick.Children.Add(Clickable("‹", Accent, 13, () => TimerTargetClicked?.Invoke(-1), "Previous timer (Ctrl+←)"));
            pick.Children.Add(new TextBlock { Text = $" {t.Position} ", Foreground = Dim, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
            pick.Children.Add(Clickable("›", Accent, 13, () => TimerTargetClicked?.Invoke(1), "Next timer (Ctrl+→)"));
            DockPanel.SetDock(pick, Dock.Right);
            head.Children.Add(pick);
        }
        head.Children.Add(new TextBlock { Text = "TIMER", Foreground = Accent, FontSize = 10.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        Card.Children.Add(head);

        if (t.Target != null)
        {
            AddLine(t.Target, Gold, 12.5, FontWeights.SemiBold);
            ActionRow(t.Restarts, "Restarts it when looted", "Restart on kill", "Ctrl+R",
                MobTimerAction.Restart, t.CanRemoveRestart ? MobTimerAction.RemoveRestart : null);
            ActionRow(t.IsPlaceholder, "Placeholder for it", "Placeholder", "Ctrl+P",
                MobTimerAction.Placeholder, t.CanRemovePlaceholder ? MobTimerAction.RemovePlaceholder : null);
        }
        if (t.CanCreate)
            ActionRow(false, "", named ? "New timer for this named" : "New timer for this mob", "Ctrl+N", MobTimerAction.NewTimer, null);
        if (t.Elsewhere.Length > 0) AddLine(t.Elsewhere, Dim, 10.5);
    }

    /// <summary>"＋ Restart on kill   Ctrl+R" (the whole row clicks), or "✓ Restarts it when looted   remove".</summary>
    private void ActionRow(bool done, string doneText, string addText, string key, MobTimerAction add, MobTimerAction? remove)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
        if (done)
        {
            if (remove is { } r)
            {
                var link = Clickable("remove", Dim, 10.5, () => TimerActionClicked?.Invoke(r), "Undo this link");
                DockPanel.SetDock(link, Dock.Right);
                row.Children.Add(link);
            }
            row.Children.Add(new TextBlock { Text = "✓ " + doneText, Foreground = Good, FontSize = 12 });
        }
        else
        {
            var hint = new TextBlock { Text = key, Foreground = Dim, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(hint, Dock.Right);
            row.Children.Add(hint);
            var label = new TextBlock { Text = "＋ " + addText, Foreground = Accent, FontSize = 12 };
            row.Children.Add(label);
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => label.TextDecorations = TextDecorations.Underline;
            row.MouseLeave += (_, _) => label.TextDecorations = null;
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; // a click, not a window drag
                TimerActionClicked?.Invoke(add);
            };
        }
        Card.Children.Add(row);
    }

    private static TextBlock Clickable(string text, Brush brush, double size, Action onClick, string tooltip)
    {
        var block = new TextBlock { Text = text, Foreground = brush, FontSize = size, Cursor = Cursors.Hand, ToolTip = tooltip, VerticalAlignment = VerticalAlignment.Center };
        block.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // a click, not a window drag
            onClick();
        };
        return block;
    }

    private void RenderMobCard(MobInfo mob, string? imagePath, string pageUrl, Func<string, bool?>? usableByYou,
        Func<string, ItemPrice?>? priceOf, MobTimerView? timers)
    {
        CardBorder.Visibility = Visibility.Visible;
        // Header: name, level and zone on the left; the wiki thumbnail (if any) on the right.
        var header = new DockPanel { LastChildFill = true };
        var image = LoadThumbnail(imagePath);
        if (image != null)
        {
            var frame = new Border
            {
                Child = new Image { Source = image, Stretch = Stretch.Uniform, MaxWidth = 84, MaxHeight = 96 },
                BorderBrush = Frozen(Color.FromArgb(0x55, 0xFF, 0xC8, 0x57)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(8, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = "Open this mob's page (Ctrl+O)",
            };
            frame.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; // a click, not a window drag
                OpenSourceClicked?.Invoke(pageUrl);
            };
            DockPanel.SetDock(frame, Dock.Right);
            header.Children.Add(frame);
        }
        var info = new StackPanel();
        header.Children.Add(info);
        Card.Children.Add(header);

        AddLine(mob.Name, Gold, 15, FontWeights.SemiBold, info);
        if (mob.IsNamed) AddLine("NAMED", Accent, 10.5, FontWeights.Bold, info);
        info.Children.Add(new Border { Height = 5 });
        if (!string.IsNullOrEmpty(mob.Level)) AddPair("Level", mob.Level, info);
        if (mob.Zone.Length > 0) AddPair("Zone", mob.Zone, info);

        AddGap();
        AddLine(mob.Drops.Count > 0 ? $"Drops ({mob.Drops.Count})" : "Drops", Accent, 10.5, FontWeights.Bold);
        if (mob.Drops.Count == 0) AddLine("None listed on the wiki.", Dim, 12);
        foreach (var drop in mob.Drops.Take(MaxDropRows))
        {
            var item = drop.Name;
            var usable = usableByYou?.Invoke(item);
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            // Price you've sold it for (gold), then the slot.
            if (priceOf?.Invoke(item) is { } price)
            {
                var value = new TextBlock { Text = PriceText(price.UnitCp), Foreground = Gold, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(value, Dock.Right);
                row.Children.Add(value);
            }
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
        if (mob.Drops.Count > MaxDropRows) AddLine($"+{mob.Drops.Count - MaxDropRows} more (on its page)", Dim, 11);
        if (timers != null) RenderTimerSection(timers, mob.IsNamed);

        AddGap();
        AddSourceLink(pageUrl, "Open this mob's page in your browser (Ctrl+O)");
        AddLine((mob.Drops.Count > 0 ? "Click a drop to see its stats · " : "") + Credit(pageUrl) + " · Ctrl\u2060+\u2060O", Dim, 9.5);
    }

    /// <summary>Loads a cached thumbnail fully into memory (no file lock), or null if missing or unreadable.</summary>
    private static ImageSource? LoadThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 168; // 2× the shown width: sharp at overlay scale up to 2
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or InvalidOperationException
                                       or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private void AddSourceLink(string url, string tooltip)
    {
        var link = new TextBlock
        {
            Text = IsArchive(url) ? "Open on MnM Archive ↗" : "Open on M&M Wiki ↗",
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
            OpenSourceClicked?.Invoke(url);
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

    /// <summary>A unit price; stack items can be worth under a copper each ("0.33c").</summary>
    private static string PriceText(double cp) =>
        cp >= 1 ? CoinFormat.Format((long)Math.Round(cp)) : $"{cp:0.##}c";

    private void AddPair(string label, string value, Panel? into = null) => AddPair(label, value, Text, into);

    private void AddPair(string label, string value, Brush valueBrush, Panel? into = null)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var v = new TextBlock { Text = value, Foreground = valueBrush, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(v, Dock.Right);
        dock.Children.Add(v);
        dock.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 12.5 });
        (into ?? Card).Children.Add(dock);
    }

    private void AddLine(string text, Brush brush, double size, FontWeight? weight = null, Panel? into = null) =>
        (into ?? Card).Children.Add(new TextBlock
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
