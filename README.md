# Spawn Timer for Monsters & Memories

A click-through, always-on-top overlay with spawn timers you control with global hotkeys.
It also has an optional session meter built from the game's own loot ledger.

## Run it

```
dotnet publish -c Release -o publish
publish\SpawnTimer.exe
```

It needs the .NET 9 Desktop Runtime, which is already installed if you have the .NET 9 SDK.
The app lives in the system tray: right-click for Settings, Unlock/Lock overlays, Reset session or Exit.

**Set the game to Borderless or Windowed.** No overlay can draw over exclusive fullscreen.

## Moving the overlays

Overlays are click-through, so mouse clicks reach the game. To move them, do one of these:
- **Hold Ctrl+Shift** and drag. They lock again when you let go. You can turn this off in Settings ▸ Hotkeys.
- **Click the tray icon once** to unlock everything, drag, then click it again to lock.
- Press the unlock hotkey (Ctrl+Alt+0), or keep the Settings window open.

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
- **Session meter**: zone, corpses looted (per hour is optional), coin looted (your share) and per hour,
  items looted, levels gained, top mobs, recent loot (including party members' loot). Every row is optional.
- **Item drop alerts**: a banner and sound when a listed item is looted by you or your party.
  Patterns: `Exact Name`, `Head of*`, `*Longbow`, `*Bloodynose*`.
- **Named mobs**: capitalized corpse names (General Overlord Bloodynose, not "a Bloodynose sergeant") are treated
  as named, plus your watch list and minus your ignore list. Players seen in the ledger are never counted.
  Looting one shows a **NAMED DOWN** banner and creates a respawn timer, 20:00 by default; edit it on the Timers tab.
  When that timer runs out you get a **NAMED SPAWN DUE** alert. This is a prediction (loot time + respawn time),
  because the game doesn't log spawns.

Banners stack (up to 4), fade out after a set time and can be dragged anywhere while the overlays are unlocked.

### Adding drop alerts from a zone

Settings ▸ Alerts ▸ **Add items from a zone…** looks up what drops in a zone and lets you tick items to alert on.
It picks the zone you're in, using the Ledger zone code (e.g. `telekir` becomes Tel Ekir), and merges two community sources:

| Source | How it's read | Licence/permission |
|---|---|---|
| [M&M Wiki on Miraheze](https://monstersandmemories.miraheze.org/) | MediaWiki API: the zone page's NPC and Items tables (one request per zone) | CC BY-SA 4.0 |
| [MnM Drops](https://mnmdrops.com/zones) | Public HTML: the zone page plus one page per mob, spaced 1 s apart | robots.txt allows /zones and /mobs |

The first load of a zone takes about 25 s and is then cached for 7 days in `%APPDATA%\SpawnTimer\zone-cache\`.
Tick "Re-download" to refresh it. Gear from named mobs is ticked by default. Items already in your list are greyed out.
monstersandmemories.wiki is deliberately not used: its robots.txt and copyright page forbid bulk scraping.

### Session export

Tray icon ▸ **Export session to CSV…** (or Settings ▸ Overlays). It saves to `Documents\Spawn Timer Sessions\` by default and writes two files:

- `MnM session <date time>.csv`: one row per ledger event this session, with time, zone, event, who, mine,
  corpse, item, quantity, coin total, your coin share and detail. That covers loot, coin, vendor sales,
  trades, drops/destroys, level-ups and party changes.
- `MnM session <date time>_mobs.csv`: one row per mob, with corpses looted, items looted (party and yours),
  your coin and first/last seen, plus session totals.

A session runs from app start until **Reset session**, so export before you reset.

What the Ledger **cannot** give you:
- **DPS or healing**: no damage or heal data is written anywhere.
- **Exact kill time**: only loot is logged. An unlooted corpse never shows up.
- **Two mobs with the same name**: corpses can't be told apart, so loot lines from one name within
  20 s count as one corpse.

Before turning it on, read the game's [Play Nice Policy](https://account2.monstersandmemories.com/policy/pnp).
It forbids third-party "screen readers/parsers" and "data mining" and doesn't say whether reading your own
ledger files counts. Timers and hotkeys don't read anything from the game.

## Files

- Settings: `%APPDATA%\SpawnTimer\settings.json`
- `Native/`: overlay window styles and the keyboard hook
- `Core/`: timers, Ledger parsing, session stats, alerts
- `UI/`: overlays and the Settings window

The overlay technique follows EQLogParser and EQTool: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`,
with `HWND_TOPMOST` re-asserted every 2 s.
