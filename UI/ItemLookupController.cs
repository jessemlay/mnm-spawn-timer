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

public enum LookupMode { Items, Mobs }

/// <summary>
/// In-game item lookup: a hotkey opens a search box over the game. Keys are captured through the low-level
/// keyboard hook (the game keeps focus and doesn't receive them), results rank current-zone drops first, Tab/Enter
/// take the autocomplete, and the highlighted item's stats come from the M&M Wiki. A second hotkey opens the same box
/// as a mob lookup: the card lists the mob's drops, and clicking one switches to that item.
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
    private List<ZoneMob> _zoneMobs = new();
    private LookupMode _mode = LookupMode.Items;
    /// <summary>Mob lookup: every known mob (rebuilt when the box opens or the zone loads).</summary>
    private List<MobInfo> _mobs = new();
    /// <summary>Mob lookup: the results, parallel to <see cref="_results"/> (names can repeat across zones).</summary>
    private List<MobInfo> _mobResults = new();
    /// <summary>Mob lookup: the highlighted mob's cached wiki thumbnail, or null.</summary>
    private string? _mobImage;
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
        _overlay.DropClicked += ShowItem;
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

    /// <summary>Your merchant price for an item (from the sales database), for the cards.</summary>
    public Func<string, ItemPrice?>? PriceOf { get; set; }

    /// <summary>Raised when the box opens or closes (the app turns key capture on/off).</summary>
    public event Action<bool>? OpenChanged;

    public void ApplySettings()
    {
        _overlay.ApplyAppearance(_settings.ItemOverlay);
        var area = SystemParameters.WorkArea;
        if (!_overlay.IsLoaded)
            _overlay.BindPosition(_settings.ItemOverlay, area.Left + area.Width / 2 - 320, area.Top + area.Height * 0.18);
    }

    /// <summary>Hotkey: opens in that mode, closes if already open in it, or switches modes.</summary>
    public void Toggle(LookupMode mode)
    {
        if (!IsOpen) Open(mode);
        else if (mode == _mode) Close();
        else SwitchMode(mode);
    }

    private void SwitchMode(LookupMode mode)
    {
        _mode = mode;
        _text = "";
        _selected = 0;
        _scroll = 0;
        _details = null;
        _cardMessage = "";
        if (mode == LookupMode.Mobs) RebuildMobs();
        Refresh(scheduleDetails: true);
    }

    /// <summary>Clicked a drop on the mob card: look that item up.</summary>
    private void ShowItem(string item)
    {
        _mode = LookupMode.Items;
        _text = item.Length > MaxLength ? item[..MaxLength] : item;
        _selected = 0;
        Refresh(scheduleDetails: false);
        var at = _results.FindIndex(r => r.Equals(item, StringComparison.OrdinalIgnoreCase));
        if (at > 0) _selected = at;
        Refresh(scheduleDetails: true);
    }

    private void RebuildMobs() => _mobs = MobSearch.Build(_zoneMobs, _zoneName, ItemDb.CachedDropSources());

    public void Open(LookupMode mode = LookupMode.Items)
    {
        if (IsOpen) return;
        IsOpen = true;
        _mode = mode;
        if (mode == LookupMode.Mobs) RebuildMobs();
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
            case 0x4F when ctrl: // Ctrl+O: open the highlighted item's (or mob's) wiki page
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
        if (_mode == LookupMode.Mobs)
        {
            _mobResults = MobSearch.Find(_text, _mobs, MaxResults);
            _results = _mobResults.Select(m => m.Name).ToList();
        }
        else
        {
            var index = ItemDb.Index ?? (IReadOnlyList<string>)Array.Empty<string>();
            _results = ItemSearch.Find(_text, index, _zoneItems, MaxResults);
        }
        _selected = _results.Count == 0 ? 0 : Math.Clamp(_selected, 0, _results.Count - 1);
        if (_selected == 0) _scroll = 0;
        KeepSelectionVisible();
        _ghost = ItemSearch.Completion(_text, _results);

        if (_mode == LookupMode.Mobs)
        {
            _details = null;
            _mobImage = null;
            if (_results.Count > 0)
            {
                // Thumbnail: instant from the cache, else fetched shortly after the highlight settles.
                _mobImage = MobImages.TryGetCached(_mobResults[_selected].Name, out var known);
                if (!known && scheduleDetails)
                {
                    _detailsDebounce.Stop();
                    _detailsDebounce.Start();
                }
            }
            _cardMessage = _results.Count > 0 ? ""
                : _text.Length > 0 ? "No matching mobs. Mobs come from your current zone's drop list and from items you've looked up."
                : _zoneName.Length > 0 ? $"No mobs known for {_zoneName} yet." : "Mobs show up once you're in a zone (from your loot).";
            Render();
            return;
        }

        if (_results.Count == 0)
        {
            _details = null;
            _cardMessage = _text.Length == 0 ? "" : "No matching items on the wiki.";
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
        var mobs = _mode == LookupMode.Mobs;
        var zoneCount = mobs ? _mobs.Count(m => m.InZone) : _zoneItems.Count;
        var hint = _status.Length > 0 && !mobs
            ? _status
            : _text.Length == 0 && zoneCount > 0
                ? mobs
                    ? $"All {zoneCount} mobs in {_zoneName} · type to search · ↑↓ PgUp/PgDn · Esc close"
                    : $"All {zoneCount} drops in {_zoneName} · type to search every item · ↑↓ PgUp/PgDn · Esc close"
                : mobs
                    ? "Type a mob name · ↑↓ PgUp/PgDn · Tab complete · Esc close"
                    : "Type an item name · ↑↓ PgUp/PgDn · Tab complete · Esc close";
        var visible = _results.Skip(_scroll).Take(VisibleRows).ToList();
        _overlay.Render(new ItemLookupView
        {
            Heading = mobs ? "MOB LOOKUP" : "ITEM LOOKUP",
            Typed = _text,
            Ghost = _ghost,
            ZoneLabel = _zoneName,
            Hint = hint,
            Results = visible.Select((r, i) => (r, mobs ? MobTag(_mobResults[_scroll + i]) : Tag(r))).ToList(),
            Selected = _selected - _scroll,
            HiddenAbove = _scroll,
            HiddenBelow = Math.Max(0, _results.Count - _scroll - visible.Count),
            Position = _results.Count > VisibleRows ? $"{_selected + 1} of {_results.Count}" : "",
            Details = mobs ? null : _details,
            Mob = mobs && _results.Count > 0 ? _mobResults[Math.Clamp(_selected, 0, _mobResults.Count - 1)] : null,
            MobImage = mobs ? _mobImage : null,
            UsableByYou = mobs ? UsableByYou() : null,
            PriceOf = PriceOf,
            CardMessage = _cardMessage,
            YourClass = _settings.ItemLookup.YourClass?.Trim() ?? "",
        });
    }

    /// <summary>Usable-by-your-class check for the mob card's drops (null when no class is set or stats aren't cached).</summary>
    private Func<string, bool?>? UsableByYou()
    {
        var cls = _settings.ItemLookup.YourClass?.Trim() ?? "";
        if (cls.Length == 0) return null;
        return item => ItemDb.TryGetCached(item) is { Classes.Length: > 0 } d ? d.UsableBy(cls) : null;
    }

    private static string MobTag(MobInfo mob)
    {
        var level = string.IsNullOrEmpty(mob.Level) ? null : "L" + mob.Level;
        var where = mob.InZone ? null : mob.Zone.Length > 20 ? mob.Zone[..19].TrimEnd() + "…" : mob.Zone;
        return string.Join(" · ", new[] { mob.IsNamed ? "★ named" : null, level, where }.Where(t => !string.IsNullOrEmpty(t)));
    }

    private string Tag(string title)
    {
        var slot = ItemDb.TryGetCached(title)?.Slot ?? "";
        if (!_zoneItems.Contains(title)) return slot;
        // Browsing the zone's own list: every row is a zone drop, so the slot says more than the star.
        if (_text.Length == 0) return slot.Length > 0 ? slot : "★";
        return "★ " + _zoneName;
    }

    private async Task LoadSelectedDetailsAsync()
    {
        if (!IsOpen || _results.Count == 0) return;
        if (_mode == LookupMode.Mobs)
        {
            await LoadSelectedMobImageAsync();
            return;
        }
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

    private async Task LoadSelectedMobImageAsync()
    {
        var mob = _mobResults[Math.Clamp(_selected, 0, _mobResults.Count - 1)].Name;
        var version = ++_detailsVersion;
        try
        {
            var path = await MobImages.GetAsync(mob, CancellationToken.None);
            if (version != _detailsVersion || !IsOpen || _mode != LookupMode.Mobs) return;
            _mobImage = path;
            if (path != null) Render();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.IO.IOException)
        {
            // No thumbnail this time; the card is fine without one.
        }
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
                _zoneMobs = new List<ZoneMob>();
                if (IsOpen) Refresh(scheduleDetails: false);
                return;
            }
            _zoneName = zone.Name;
            var results = await ZoneSources.LoadZoneAsync(zone, useWiki: true, useDrops: false, refresh: false,
                new Progress<string>(_ => { }), CancellationToken.None);
            _zoneMobs = results.SelectMany(r => r.Mobs).ToList();
            var titles = _zoneMobs.SelectMany(m => m.Drops).Select(d => d.Name)
                .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _zoneItems = new HashSet<string>(titles, StringComparer.OrdinalIgnoreCase);
            if (_mode == LookupMode.Mobs) RebuildMobs();
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
