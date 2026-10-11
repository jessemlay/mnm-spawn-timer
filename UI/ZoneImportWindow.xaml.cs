using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

/// <summary>Pick a zone, load its drops from community sites, tick items, add them to the drop alert list.</summary>
public partial class ZoneImportWindow : Window
{
    private static readonly IReadOnlySet<string> NoPlayers = new HashSet<string>();

    private readonly AppSettings _settings;
    private readonly string _currentLedgerZone;
    private CancellationTokenSource? _cts;
    private List<ZoneItemRow> _rows = new();
    private List<ZoneSourceResult> _results = new();
    private ICollectionView? _view;

    public ZoneImportWindow(AppSettings settings, string currentLedgerZone)
    {
        InitializeComponent();
        _settings = settings;
        _currentLedgerZone = currentLedgerZone;
        AttributionText.Text = ZoneSources.Attribution;
        Loaded += async (_, _) => await LoadZoneListAsync(refresh: false);
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>Number of item names added during this dialog session.</summary>
    public int AddedItems { get; private set; }

    private async Task LoadZoneListAsync(bool refresh)
    {
        await RunAsync("Loading zone list…", async ct =>
        {
            var zones = await ZoneSources.ListZonesAsync(WikiBox.IsChecked == true, DropsBox.IsChecked == true, refresh, ct);
            ZoneBox.ItemsSource = zones;
            ZoneBox.SelectedItem = ZoneNames.MatchLedgerZone(_currentLedgerZone, zones) ?? zones.FirstOrDefault();
            StatusText.Text = zones.Count == 0
                ? "No zones found."
                : $"{zones.Count} zones. " + (ZoneNames.MatchLedgerZone(_currentLedgerZone, zones) != null
                    ? "Selected the zone you're in."
                    : "Pick a zone and press Load drops.");
        });
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel(); // the button reads "Cancel" while loading
            return;
        }
        if (ZoneBox.ItemsSource == null)
        {
            await LoadZoneListAsync(RefreshBox.IsChecked == true);
            return;
        }
        if (ZoneBox.SelectedItem is not ZoneRef zone) return;

        await RunAsync($"Loading {zone.Name}…", async ct =>
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            _results = await ZoneSources.LoadZoneAsync(zone, WikiBox.IsChecked == true, DropsBox.IsChecked == true,
                RefreshBox.IsChecked == true, progress, ct);
            ShowRows();
            var mobs = ZoneMerge.CountMobsWithDrops(_results);
            var sources = string.Join(" + ", _results.Select(r => r.Source));
            var age = _results.Count > 0 ? _results.Min(r => r.FetchedUtc) : DateTime.UtcNow;
            var ageText = DateTime.UtcNow - age < TimeSpan.FromMinutes(2) ? "just downloaded" : $"saved copy from {age.ToLocalTime():g}";
            StatusText.Text = _rows.Count == 0
                ? $"No drops listed for {zone.Name} in {sources}."
                : $"{_rows.Count} items from {mobs} mobs in {zone.Name} ({sources}, {ageText}). Suggested gear is ticked.";
        });
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        LoadButton.Content = "Cancel";
        ZoneBox.IsEnabled = WikiBox.IsEnabled = DropsBox.IsEnabled = false;
        StatusText.Text = status;
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded) StatusText.Text = "Cancelled.";
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = "Couldn't download: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            if (IsLoaded)
            {
                LoadButton.Content = "Load drops";
                ZoneBox.IsEnabled = WikiBox.IsEnabled = DropsBox.IsEnabled = true;
            }
        }
    }

    private void ShowRows()
    {
        foreach (var row in _rows) row.PropertyChanged -= Row_PropertyChanged;
        _rows = ZoneMerge.BuildRows(_results, _settings.DropAlerts.ItemNames);
        foreach (var row in _rows) row.PropertyChanged += Row_PropertyChanged;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Matches;
        ItemGrid.ItemsSource = _view;
        UpdateAddButton();
    }

    private bool Matches(object o)
    {
        if (o is not ZoneItemRow row) return false;
        if (NamedOnlyBox.IsChecked == true && !row.FromNamed) return false;
        var filter = FilterBox.Text.Trim();
        return filter.Length == 0
               || row.Item.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || row.DroppedBy.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || row.Slot.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<ZoneItemRow> ShownRows() => _view?.Cast<ZoneItemRow>() ?? Enumerable.Empty<ZoneItemRow>();

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoneItemRow.Selected)) UpdateAddButton();
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        // Ticking one of several highlighted rows ticks them all.
        if (sender is not FrameworkElement { DataContext: ZoneItemRow clicked }) return;
        if (ItemGrid.SelectedItems.Count > 1 && ItemGrid.SelectedItems.Contains(clicked))
            foreach (var row in ItemGrid.SelectedItems.OfType<ZoneItemRow>()) row.Selected = clicked.Selected;
    }

    private void TickSuggested_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ShownRows())
            row.Selected = !row.AlreadyAlerting && ZoneMerge.IsLikelyGood(row.Item, row.Slot, row.FromNamed);
    }

    private void TickShown_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ShownRows()) row.Selected = !row.AlreadyAlerting;
    }

    private void UntickAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Selected = false;
    }

    private void UpdateAddButton()
    {
        var count = _rows.Count(r => r.Selected && !r.AlreadyAlerting);
        AddButton.Content = $"Add {count} item{(count == 1 ? "" : "s")} to drop alerts";
        AddButton.IsEnabled = count > 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var picked = _rows.Where(r => r.Selected && !r.AlreadyAlerting).Select(r => r.Item).ToList();
        if (picked.Count == 0) return;

        _settings.DropAlerts.ItemNames = AppendLines(_settings.DropAlerts.ItemNames, picked);
        _settings.DropAlerts.Enabled = true;
        AddedItems += picked.Count;

        var addedNamed = 0;
        if (AddNamedBox.IsChecked == true)
        {
            var missed = _results.SelectMany(r => r.Mobs)
                .Where(m => m.IsNamed && !NamedDetector.IsNamed(m.Name, _settings.Named, NoPlayers))
                .Select(m => m.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            addedNamed = missed.Count;
            if (missed.Count > 0) _settings.Named.WatchNames = AppendLines(_settings.Named.WatchNames, missed);
        }

        App.Instance.ApplySettings();
        ShowRows();
        StatusText.Text = $"Added {picked.Count} item{(picked.Count == 1 ? "" : "s")} to your drop alerts" +
                          (addedNamed > 0 ? $" and {addedNamed} named mob{(addedNamed == 1 ? "" : "s")} to the watch list." : ".") +
                          " Pick another zone or close.";
    }

    private static string AppendLines(string existing, IEnumerable<string> additions)
    {
        var lines = existing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var add in additions)
            if (!lines.Any(l => string.Equals(l, add, StringComparison.OrdinalIgnoreCase))) lines.Add(add);
        return string.Join(Environment.NewLine, lines);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
