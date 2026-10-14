using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

/// <summary>
/// In-game item lookup: a hotkey opens a search box over the game. Keys are captured through the low-level
/// keyboard hook (the game keeps focus and doesn't receive them), results rank current-zone drops first, Tab/Enter
/// take the autocomplete, and the highlighted item's stats come from the M&M Wiki.
/// </summary>
public sealed class ItemLookupController
{
    private const int MaxResults = 250;  // enough for every drop in a zone; the list scrolls
    private const int VisibleRows = 10;
    private const int MaxLength = 48;
    private int _scroll;                 // index of the first visible result

    private readonly AppSettings _settings;
    private readonly ItemSearchOverlay _overlay;
    private readonly DispatcherTimer _detailsDebounce;
    private readonly DispatcherTimer _focusWatch;
    /// <summary>The window in front when the lookup opened (normally the game); keys are only taken while it stays in front.</summary>
    private IntPtr _ownerWindow;
    private HashSet<string> _zoneItems = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Current zone's drops: item → the mobs that drop it.</summary>
    private Dictionary<string, List<string>> _zoneDroppedBy = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Zone drops plus every cached item's drop list, so the search also matches mob names.</summary>
    private Dictionary<string, List<string>>? _droppedBy;
    private string _zoneName = "";
    private string _zoneCode = "";
    private string _text = "";
    private string _ghost = "";
    private List<string> _results = new();
    private int _selected;
    private ItemDetails? _details;
    private string _cardMessage = "";
    private string _status = "";
    private int _detailsVersion;

    public ItemLookupController(AppSettings settings)
    {
        _settings = settings;
        _overlay = new ItemSearchOverlay();
        _overlay.RowClicked += i =>
        {
            _selected = _scroll + i; // rows are numbered within the visible window
            Refresh(scheduleDetails: true);
        };
        _overlay.OpenSourceClicked += OpenSourcePage;
        _overlay.Scrolled += notches =>
        {
            // Mouse wheel scrolls the list (3 rows per notch) without changing the highlighted item.
            _scroll = Math.Clamp(_scroll - notches * 3, 0, Math.Max(0, _results.Count - VisibleRows));
            Render();
        };
        _detailsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _detailsDebounce.Tick += async (_, _) =>
        {
            _detailsDebounce.Stop();
            await LoadSelectedDetailsAsync();
        };

        // Close as soon as another window comes to the front (clicked another app, Alt+Tab…).
        _focusWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _focusWatch.Tick += (_, _) =>
        {
            if (IsOpen && LeftOwnerWindow()) Close();
        };
    }

    /// <summary>Opens the item's wiki page in the default browser (the lookup then closes as the browser comes forward).</summary>
    private static void OpenSourcePage(string title)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ItemDb.WikiUrl(title)) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private bool LeftOwnerWindow()
    {
        var front = SpawnTimer.Native.NativeMethods.GetForegroundWindow();
        return front != IntPtr.Zero && front != _ownerWindow;
    }

    public bool IsOpen { get; private set; }

    /// <summary>Raised when the box opens or closes (the app turns key capture on/off).</summary>
    public event Action<bool>? OpenChanged;

    public void ApplySettings()
    {
        _overlay.ApplyAppearance(_settings.ItemOverlay);
        var area = SystemParameters.WorkArea;
        if (!_overlay.IsLoaded)
            _overlay.BindPosition(_settings.ItemOverlay, area.Left + area.Width / 2 - 320, area.Top + area.Height * 0.18);
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        _ownerWindow = SpawnTimer.Native.NativeMethods.GetForegroundWindow();
        _focusWatch.Start();
        _text = "";
        _selected = 0;
        _details = null;
        _cardMessage = "";
        Refresh(scheduleDetails: false);
        _overlay.Show();
        _overlay.EditMode = true; // clickable rows and draggable, but still never takes focus
        OpenChanged?.Invoke(true);
        if (ItemDb.Index == null) _ = PreloadIndexAsync();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _focusWatch.Stop();
        _detailsDebounce.Stop();
        _overlay.EditMode = false;
        _overlay.Hide();
        OpenChanged?.Invoke(false);
    }

    /// <summary>
    /// Called from the keyboard hook while open (must be quick). Returns true to keep the key from the game.
    /// Alt/Win combinations pass through so Alt+Tab etc. keep working.
    /// </summary>
    public bool HandleKey(int vk, ModifierKeys mods)
    {
        if (!IsOpen) return false;
        // Another app is in front: never swallow its keys; close (after the hook returns) and let the key through.
        if (LeftOwnerWindow())
        {
            _overlay.Dispatcher.BeginInvoke(Close, DispatcherPriority.Input);
            return false;
        }
        if (mods.HasFlag(ModifierKeys.Alt) || mods.HasFlag(ModifierKeys.Windows)) return false;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);

        switch (vk)
        {
            case 0x1B: // Esc: clear, or close if already empty
                if (_text.Length > 0) _text = "";
                else
                {
                    Close();
                    return true;
                }
                break;
            case 0x09: // Tab: take the autocomplete, else move down
                if (_ghost.Length > 0) _text += _ghost;
                else Move(shift ? -1 : 1);
                break;
            case 0x0D: // Enter: complete to the highlighted item
                if (_results.Count > 0) _text = _results[Math.Clamp(_selected, 0, _results.Count - 1)];
                break;
            case 0x26: Move(-1); return Refreshed(true); // Up
            case 0x28: Move(1); return Refreshed(true);  // Down
            case 0x21: Page(-1); return Refreshed(true); // Page Up
            case 0x22: Page(1); return Refreshed(true);  // Page Down
            case 0x08: // Backspace (Ctrl: whole word)
                if (_text.Length > 0)
                {
                    if (ctrl)
                    {
                        var trimmed = _text.TrimEnd();
                        var cut = trimmed.LastIndexOf(' ');
                        _text = cut < 0 ? "" : trimmed[..(cut + 1)];
                    }
                    else _text = _text[..^1];
                }
                break;
            case 0x2E: _text = ""; break; // Delete clears
            case 0x4F when ctrl: // Ctrl+O: open the highlighted item's wiki page
                if (_results.Count > 0)
                {
                    var title = _results[Math.Clamp(_selected, 0, _results.Count - 1)];
                    _overlay.Dispatcher.BeginInvoke(() => OpenSourcePage(title), DispatcherPriority.Input);
                }
                return true;
            default:
                if (ctrl) return true; // swallow Ctrl combos so they don't reach the game
                var ch = KeyChar(vk, shift);
                if (ch == null) return true; // other keys: just keep them from the game
                if (_text.Length < MaxLength) _text += ch;
                break;
        }
        _selected = 0;
        return Refreshed(true);
    }

    private bool Refreshed(bool scheduleDetails)
    {
        // Rendering touches WPF elements; do it right after the hook returns.
        _overlay.Dispatcher.BeginInvoke(() => Refresh(scheduleDetails), DispatcherPriority.Input);
        return true;
    }

    private void Move(int delta)
    {
        if (_results.Count == 0) return;
        _selected = (_selected + delta + _results.Count) % _results.Count; // wraps top <-> bottom
    }

    private void Page(int direction)
    {
        if (_results.Count == 0) return;
        _selected = Math.Clamp(_selected + direction * VisibleRows, 0, _results.Count - 1);
    }

    /// <summary>Scrolls just enough to keep the highlighted row on screen.</summary>
    private void KeepSelectionVisible()
    {
        if (_selected < _scroll) _scroll = _selected;
        else if (_selected >= _scroll + VisibleRows) _scroll = _selected - VisibleRows + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _results.Count - VisibleRows));
    }

    private static string? KeyChar(int vk, bool shift) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)(shift ? vk : vk + 32)).ToString(), // A-Z
        >= 0x30 and <= 0x39 when !shift => ((char)vk).ToString(),         // 0-9
        >= 0x60 and <= 0x69 => ((char)('0' + vk - 0x60)).ToString(),       // numpad 0-9
        0x20 => " ",
        0xDE when !shift => "'",
        0xBD => shift ? "_" : "-",
        0xBC when !shift => ",",
        0xBE when !shift => ".",
        _ => null,
    };

    private void Refresh(bool scheduleDetails)
    {
        var index = ItemDb.Index ?? (IReadOnlyList<string>)Array.Empty<string>();
        _droppedBy = _text.Length == 0 ? null : DroppedBy();
        _results = ItemSearch.Find(_text, index, _zoneItems, MaxResults, _droppedBy);
        _selected = _results.Count == 0 ? 0 : Math.Clamp(_selected, 0, _results.Count - 1);
        if (_selected == 0) _scroll = 0;
        KeepSelectionVisible();
        _ghost = ItemSearch.Completion(_text, _results, _droppedBy);

        if (_results.Count == 0)
        {
            _details = null;
            _cardMessage = _text.Length == 0 ? "" : "No matching items or mobs on the wiki.";
        }
        else
        {
            var cached = ItemDb.TryGetCached(_results[_selected]);
            _details = cached;
            _cardMessage = cached == null ? "Loading stats…" : "";
            if (cached == null && scheduleDetails)
            {
                _detailsDebounce.Stop();
                _detailsDebounce.Start();
            }
        }
        Render();
    }

    private void Render()
    {
        var hint = _status.Length > 0
            ? _status
            : _text.Length == 0 && _zoneItems.Count > 0
                ? $"All {_zoneItems.Count} drops in {_zoneName} · type to search every item · ↑↓ PgUp/PgDn · Esc close"
                : "Type an item or mob name · ↑↓ PgUp/PgDn · Tab complete · Esc close";
        var visible = _results.Skip(_scroll).Take(VisibleRows).ToList();
        _overlay.Render(new ItemLookupView
        {
            Typed = _text,
            Ghost = _ghost,
            ZoneLabel = _zoneName,
            Hint = hint,
            Results = visible.Select(r => (r, Tag(r))).ToList(),
            Selected = _selected - _scroll,
            HiddenAbove = _scroll,
            HiddenBelow = Math.Max(0, _results.Count - _scroll - visible.Count),
            Position = _results.Count > VisibleRows ? $"{_selected + 1} of {_results.Count}" : "",
            Details = _details,
            CardMessage = _cardMessage,
            YourClass = _settings.ItemLookup.YourClass?.Trim() ?? "",
        });
    }

    private Dictionary<string, List<string>> DroppedBy()
    {
        var map = ItemDb.CachedDropSources();
        foreach (var (item, mobs) in _zoneDroppedBy)
            map[item] = map.TryGetValue(item, out var known) ? mobs.Union(known, StringComparer.OrdinalIgnoreCase).ToList() : mobs;
        return map;
    }

    private string Tag(string title)
    {
        // Matched through a mob's name: say which mob, so its drops read as a list.
        if (ItemSearch.MatchedMob(title, _text, _droppedBy) is { } mob)
            return "from " + (mob.Length > 20 ? mob[..19].TrimEnd() + "…" : mob);
        var slot = ItemDb.TryGetCached(title)?.Slot ?? "";
        if (!_zoneItems.Contains(title)) return slot;
        // Browsing the zone's own list: every row is a zone drop, so the slot says more than the star.
        if (_text.Length == 0) return slot.Length > 0 ? slot : "★";
        return "★ " + _zoneName;
    }

    private async Task LoadSelectedDetailsAsync()
    {
        if (!IsOpen || _results.Count == 0) return;
        var title = _results[Math.Clamp(_selected, 0, _results.Count - 1)];
        var version = ++_detailsVersion;
        try
        {
            var found = await ItemDb.GetAsync(new[] { title }, CancellationToken.None);
            if (version != _detailsVersion || !IsOpen) return;
            _details = found.TryGetValue(title, out var d) ? d : null;
            _cardMessage = _details == null ? "No stats on the wiki for this item." : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _cardMessage = "Couldn't reach the wiki: " + ex.Message;
        }
        Render();
    }

    /// <summary>Background: load the item list (cached 7 days).</summary>
    public async Task PreloadIndexAsync()
    {
        try
        {
            _status = ItemDb.Index == null ? "Loading the item list…" : "";
            if (IsOpen) Render();
            await ItemDb.LoadIndexAsync(refresh: false, CancellationToken.None);
            _status = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _status = "Item list unavailable (offline?). Zone items still work.";
        }
        if (IsOpen) Refresh(scheduleDetails: true);
    }

    /// <summary>
    /// Called when the Ledger shows a new zone: fetch its drop list and every drop's stats in the background so
    /// the lookup is instant there (a couple of wiki requests, cached for 7 days).
    /// </summary>
    public async Task OnZoneAsync(string ledgerZoneCode)
    {
        if (string.IsNullOrWhiteSpace(ledgerZoneCode) || ledgerZoneCode == _zoneCode) return;
        _zoneCode = ledgerZoneCode;
        _zoneName = ZoneNames.Tidy(ledgerZoneCode);
        try
        {
            var zones = await ZoneSources.ListZonesAsync(useWiki: true, useDrops: false, refresh: false, CancellationToken.None);
            var zone = ZoneNames.MatchLedgerZone(ledgerZoneCode, zones);
            if (zone == null)
            {
                _zoneItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _zoneDroppedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (IsOpen) Refresh(scheduleDetails: false);
                return;
            }
            _zoneName = zone.Name;
            var results = await ZoneSources.LoadZoneAsync(zone, useWiki: true, useDrops: false, refresh: false,
                new Progress<string>(_ => { }), CancellationToken.None);
            var droppedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mob in results.SelectMany(r => r.Mobs))
                foreach (var drop in mob.Drops.Where(d => d.Name.Length > 0))
                {
                    if (!droppedBy.TryGetValue(drop.Name, out var mobs)) droppedBy[drop.Name] = mobs = new List<string>();
                    if (!mobs.Contains(mob.Name, StringComparer.OrdinalIgnoreCase)) mobs.Add(mob.Name);
                }
            var titles = droppedBy.Keys.ToList();
            _zoneItems = new HashSet<string>(titles, StringComparer.OrdinalIgnoreCase);
            _zoneDroppedBy = droppedBy;
            if (IsOpen) Refresh(scheduleDetails: false);

            if (_settings.ItemLookup.PreloadZoneItems && titles.Count > 0)
            {
                await ItemDb.GetAsync(titles, CancellationToken.None);
                if (IsOpen) Refresh(scheduleDetails: false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _zoneCode = ""; // try again on the next zone event
        }
    }
}
