# Spawn Timer for Monsters & Memories

[![Build](https://github.com/jessemlay/mnm-spawn-timer/actions/workflows/build.yml/badge.svg)](https://github.com/jessemlay/mnm-spawn-timer/actions/workflows/build.yml)

A click-through, always-on-top overlay with spawn timers you control with global hotkeys.
It also has an optional session meter built from the game's own loot ledger.

Fan-made and unofficial. It's not affiliated with or endorsed by Niche Worlds Cult.

## Download

- **Releases:** [latest release](https://github.com/jessemlay/mnm-spawn-timer/releases/latest)
- **Latest build:** open the newest [Build run](https://github.com/jessemlay/mnm-spawn-timer/actions/workflows/build.yml) and download its artifact. You need to be signed in to GitHub.

Download **`SpawnTimer-Setup.exe`** (~2 MB) and run it. The installer:

- checks for the .NET 9 Desktop Runtime and, if it's missing, downloads (~58 MB) and installs it from Microsoft
- adds a Start Menu shortcut, an optional desktop icon and "Start with Windows", and an uninstaller in Windows Settings ▸ Apps
- upgrades in place when you run a newer one, keeping your settings

Windows may show a SmartScreen warning because the installer isn't code-signed: click **More info ▸ Run anyway**.
Settings live in `Documents\Spawn Timer\settings.json` (with a `.bak` of the previous session). Installs, updates and uninstalls never touch them. Versions before 1.4.0 used `%APPDATA%\SpawnTimer`; that file is copied over automatically the first time.
The app lives in the system tray: right-click for Settings, Unlock/Lock overlays, Export, Reset session or Exit.

## Build it yourself

```
dotnet publish -c Release -o publish
publish\SpawnTimer.exe
```

### Making a release

Every push to `main` builds and test-installs `SpawnTimer-Setup.exe` and keeps it as a workflow artifact. To publish a release with the installer attached, push a version tag:

```
git tag v1.0.0
git push origin v1.0.0
```

**Set the game to Borderless or Windowed.** No overlay can draw over exclusive fullscreen.

The timer and meter overlays only show while the game is running. They still appear while unlocked from the tray, the
hotkey or Settings, so you can place them with the game closed. To always show them, turn this off in Settings ▸ Overlays.

## Moving the overlays

Overlays are click-through, so mouse clicks reach the game. To move them, do one of these:
- **Hold Ctrl+Shift** and drag. They lock again when you let go. You can turn this off in Settings ▸ Hotkeys.
- **Click the tray icon once** to unlock everything, drag, then click it again to lock.
- Press the unlock hotkey (Ctrl+Alt+0), or keep the Settings window open.

## Managing timers without leaving the game

Nothing in the app pulls focus from the game: there are no confirmation boxes, and messages appear as overlay banners.

- **While unlocked** (tray click, Ctrl+Alt+0, or holding Ctrl+Shift), each timer row shows **Restart / Clear / Remove** buttons.
  **Clear** sets it back to READY and forgets the countdown. **Remove** deletes it immediately.
- **Tray ▸ Timers ▸ <timer>** has the same options.
- A removed auto-created named timer isn't recreated on the next kill (you still get NAMED DOWN).
  To undo, delete the name from Settings ▸ Alerts ▸ Named mobs ▸ "Don't create timers for".
- **Tray ▸ Export session** saves straight to `Documents\Spawn Timer Sessions\` and shows a banner. There's no Save As dialog.

## Item lookup

Press **Ctrl+Alt+I** (changeable) in game and start typing an item name. The game keeps focus: while the box is open
your keys go to the search instead of the game, and Alt/Win shortcuts like Alt+Tab still work.

- With the box empty you get **every drop in your current zone**. Typing searches all ~5,200 wiki items, with your zone's drops first (★). The list scrolls: **↑/↓**, **PgUp/PgDn**, or the mouse wheel.
- Faint **autocomplete** text shows the best match: **Tab** takes it, **↑/↓** move, **Enter** fills in the highlighted
  item, **Backspace** (Ctrl = whole word), **Esc** clears, then closes.
- The highlighted item's **stat card** shows slot, AC, damage/delay, stats, resists, effect, classes, races,
  weight/size and where it drops, plus an **Open on M&M Wiki ↗** link (or **Ctrl+O**) that opens the item's page in your browser. Set **your class** (e.g. `CLR`) in Settings ▸ Overlays to get ✓ usable / ✗ not usable.
- When the app sees you've entered a zone (from your loot), it **preloads that zone's items** in the background,
  so lookups there are instant.

Data comes from the [M&M Wiki on Miraheze](https://monstersandmemories.miraheze.org/) (CC BY-SA 4.0) and is cached
for 7 days in `%LOCALAPPDATA%\SpawnTimer\item-cache\`.

## Hotkeys (defaults, change them in Settings ▸ Hotkeys)

| Hotkey | Action |
|---|---|
| Ctrl+Alt+1 | Start or resume the selected (▶) timer |
| Ctrl+Alt+2 | Stop: first press pauses, second press resets |
| Ctrl+Alt+3 | Restart: a full countdown from now (press it when the mob dies) |
| Ctrl+Alt+4 | Select the next timer |
| Ctrl+Alt+0 | Unlock or lock overlays to drag them |

Each timer can also have its own **restart hotkey**. Hotkeys use a low-level keyboard hook. By default
the game doesn't see the key. Turn on "Also send hotkey presses to the game" to bind a timer to a key
you already use in-game. If the game runs as administrator, run Spawn Timer as administrator too.

After zero the timer turns red, says "UP!" and counts overtime (+0:15), which helps when spawn times vary.

## Game data (optional, off by default)

M&M has no combat log. It does write a JSON **Ledger** on your PC:

`%USERPROFILE%\AppData\LocalLow\Niche Worlds Cult\Monsters and Memories\<server>\<character>\Ledger\`

`<Char>_Character_<date>.json` holds your own actions. `<Char>_Social_<date>.json` holds what your party sees.
Names and zones are base64-encoded. With **Game data** on, Spawn Timer reads (never writes) these files
once a second and enables:

- **Auto-restart**: a timer restarts when a corpse matching its mob names is looted. Use `a skeletal*` for prefixes.
- **Session meter**: zone, corpses looted (per hour is optional), coin looted (your share) and per hour.
  Per-hour rates follow your recent pace (last ~10 minutes, adjustable), fade when you stop looting, and reach 0
  after about 40 minutes without loot. Recent loot entries show their age and drop off after 15 minutes (adjustable). Other rows:
  items looted, levels gained, top mobs, recent loot (including party members' loot). Every row is optional.
- **Item drop alerts**: a banner and sound when a listed item is looted by you or your party.
  Patterns: `Exact Name`, `Head of*`, `*Longbow`, `*Bloodynose*`.
- **Named mobs**: capitalized corpse names (General Overlord Bloodynose, not "a Bloodynose sergeant") are treated
  as named, plus your watch list and minus your ignore list. Players seen in the ledger are never counted.
  Looting one shows a **NAMED DOWN** banner and creates a respawn timer, 20:00 by default; edit it on the Timers tab.
  When that timer runs out you get a **NAMED SPAWN DUE** alert. This is a prediction (loot time + respawn time),
  because the game doesn't log spawns.

#### Placeholders

Many named mobs share their spawn point with a placeholder (PH), so a named timer really tracks the **spawn point**.
When it runs out the overlay shows **UP?** and the banner says *named or placeholder*.
Each timer has a **Placeholder mobs** list (Settings ▸ Timers). Looting a listed PH **while the timer is at zero**
restarts it as a new cycle ("PLACEHOLDER DOWN", "PH cycle 2"). PH kills during a running countdown are ignored,
because the Ledger has no locations and the same mob may die elsewhere. Under the bar, the overlay shows the camp history
(e.g. `PH cycle 2 · named 47m ago`). This history survives restarts and is filled in from today's Ledger when the app starts.

Banners stack (up to 4), fade out after a set time and can be dragged anywhere while the overlays are unlocked.

### Adding drop alerts from a zone

Settings ▸ Alerts ▸ **Add items from a zone…** looks up what drops in a zone and lets you tick items to alert on.
It picks the zone you're in, using the Ledger zone code (e.g. `telekir` becomes Tel Ekir), and merges two community sources:

| Source | How it's read | Licence/permission |
|---|---|---|
| [M&M Wiki on Miraheze](https://monstersandmemories.miraheze.org/) | MediaWiki API: the zone page's NPC and Items tables (one request per zone) | CC BY-SA 4.0 |
| [MnM Drops](https://mnmdrops.com/zones) | Public HTML: the zone page plus one page per mob, spaced 1 s apart | robots.txt allows /zones and /mobs |

The first load of a zone takes about 25 s and is then cached for 7 days in `%LOCALAPPDATA%\SpawnTimer\zone-cache\`.
Tick "Re-download" to refresh it. Gear from named mobs is ticked by default. Items already in your list are greyed out.
monstersandmemories.wiki is deliberately not used: its robots.txt and copyright page forbid bulk scraping.

### Session export

Tray icon ▸ **Export session to CSV…** (or Settings ▸ Overlays). It saves to `Documents\Spawn Timer Sessions\` by default and writes two files:

- `MnM session <date time>.csv`: one row per ledger event this session, with time, zone, event, who, mine,
  corpse, item, quantity, coin total, your coin share and detail. That covers loot, coin, vendor sales,
  trades, drops/destroys, level-ups and party changes.
- `MnM session <date time>_mobs.csv`: one row per mob, with corpses looted, items looted (party and yours),
  your coin and first/last seen, plus session totals.

A session **follows the game**: it starts when Monsters & Memories launches and ends (totals frozen) when it closes. A loot after 45 minutes with none (adjustable) also starts a new one, for when the game is left running. The meter header shows when the session started, e.g. "since 12:22 PM · 29m". To start one by hand, use **Restart session**: it's on the meter while the overlays are unlocked (tray click, Ctrl+Alt+0, or hold Ctrl+Shift), and in the tray menu. Export first if you want the old session's CSV.

What the Ledger **cannot** give you:
- **DPS or healing**: no damage or heal data is written anywhere.
- **Exact kill time**: only loot is logged. An unlooted corpse never shows up.
- **Two mobs with the same name**: corpses can't be told apart, so loot lines from one name within
  20 s count as one corpse.

Before turning it on, read the game's [Play Nice Policy](https://account2.monstersandmemories.com/policy/pnp).
It forbids third-party "screen readers/parsers" and "data mining" and doesn't say whether reading your own
ledger files counts. Timers and hotkeys don't read anything from the game.

## Files

- Settings: `Documents\Spawn Timer\settings.json`
- `Native/`: overlay window styles and the keyboard hook
- `Core/`: timers, Ledger parsing, session stats, alerts
- `UI/`: overlays and the Settings window

The overlay technique follows EQLogParser and EQTool: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`,
with `HWND_TOPMOST` re-asserted every 2 s.
