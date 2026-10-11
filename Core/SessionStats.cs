using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnTimer.Core;

/// <summary>Per-mob totals for the session.</summary>
public sealed class MobSummary
{
    public string Name { get; init; } = "";
    public string Zone { get; set; } = "";
    public int Corpses { get; set; }
    public long MyCoinCp { get; set; }
    public int ItemsLooted { get; set; }
    public int MyItemsLooted { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Session totals built from Ledger events (loot-based; the game logs no damage or healing).</summary>
public sealed class SessionStats
{
    // Loot lines for one corpse (coin plus each item) arrive within seconds; a longer gap means a new corpse.
    private static readonly TimeSpan SameCorpseWindow = TimeSpan.FromSeconds(20);
    private const int RecentLootCap = 50;

    private readonly Dictionary<string, DateTimeOffset> _lastCorpseEvent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MobSummary> _mobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LedgerEvent> _events = new();
    private readonly List<LedgerEvent> _recentLoot = new();
    private readonly List<(DateTimeOffset Time, double Value)> _coinSamples = new();
    private readonly List<(DateTimeOffset Time, double Value)> _corpseSamples = new();
    private const int MaxPartySize = 6;
    private static readonly TimeSpan SplitEvidenceWindow = TimeSpan.FromMinutes(15);
    private int _partySize = 1;
    private bool _inParty;
    private DateTimeOffset _lastSplitSeen = DateTimeOffset.MinValue;

    public SessionStats() => Reset();

    public DateTimeOffset Start { get; private set; }
    public int Kills { get; private set; }
    public long LootCoinCp { get; private set; }
    public long SalesCp { get; private set; }
    public long CoinCp => LootCoinCp + SalesCp;
    public int ItemsLooted { get; private set; }
    public int LevelsGained { get; private set; }
    public int? Level { get; private set; }
    public string Zone { get; private set; } = "";
    public bool HasData { get; private set; }
    public IReadOnlyList<LedgerEvent> RecentLoot => _recentLoot;

    /// <summary>Every ledger event in this session, oldest first (for export).</summary>
    public IReadOnlyList<LedgerEvent> Events => _events;

    public IReadOnlyCollection<MobSummary> Mobs => _mobs.Values;

    public TimeSpan Elapsed => DateTimeOffset.Now - Start;

    public double PerHour(double value) => value / Math.Max(Elapsed.TotalHours, 1.0 / 60);

    /// <summary>Your coin per hour, weighted toward recent loot (see <see cref="RecentRate"/>).</summary>
    public double CoinPerHour(TimeSpan window, DateTimeOffset now) => RecentRate(_coinSamples, LootCoinCp, window, now);

    /// <summary>Corpses per hour, weighted toward recent kills.</summary>
    public double CorpsesPerHour(TimeSpan window, DateTimeOffset now) => RecentRate(_corpseSamples, Kills, window, now);

    /// <summary>
    /// Per-hour rate where each sample's weight decays as e^(-age/window), so the rate follows your current pace
    /// and fades toward zero when you stop. Normalized by (1 - e^(-elapsed/window)) so it isn't low early in a session,
    /// and forced to 0 once nothing has happened for 4 windows (~2% left). A zero window gives the whole-session average.
    /// </summary>
    private double RecentRate(List<(DateTimeOffset Time, double Value)> samples, double total, TimeSpan window, DateTimeOffset now)
    {
        if (window <= TimeSpan.Zero) return PerHour(total);
        if (samples.Count == 0 || now - samples[^1].Time > window * 4) return 0;

        var tau = window.TotalHours;
        var weighted = 0.0;
        foreach (var (time, value) in samples)
        {
            if (time > now) continue; // only what had happened by "now"
            weighted += value * Math.Exp(-(now - time).TotalHours / tau);
        }
        var elapsedHours = Math.Max((now - Start).TotalHours, 1.0 / 60);
        return weighted / (tau * (1 - Math.Exp(-elapsedHours / tau)));
    }

    public IEnumerable<KeyValuePair<string, int>> TopMobs(int count) =>
        _mobs.Values.Where(m => m.Corpses > 0)
            .OrderByDescending(m => m.Corpses).ThenBy(m => m.Name)
            .Take(count)
            .Select(m => new KeyValuePair<string, int>(m.Name, m.Corpses));

    public void Reset(DateTimeOffset? start = null)
    {
        Start = start ?? DateTimeOffset.Now;
        Kills = ItemsLooted = LevelsGained = 0;
        LootCoinCp = SalesCp = 0;
        HasData = false;
        _lastCorpseEvent.Clear();
        _mobs.Clear();
        _events.Clear();
        _recentLoot.Clear();
        _coinSamples.Clear();
        _corpseSamples.Clear();
    }

    /// <summary>Current group size estimate (1 when solo).</summary>
    public int PartySize => _inParty ? _partySize : 1;

    public void Apply(LedgerEvent e)
    {
        // Party state is tracked from all history (including before the session) so the first loot splits correctly.
        TrackParty(e);
        if (e.Time < Start) return;

        HasData = true;
        _events.Add(e);
        if (e.Zone.Length > 0) Zone = e.Zone;

        switch (e.Kind)
        {
            case LedgerKind.ItemLoot:
            {
                CountCorpse(e);
                var qty = Math.Max(1, e.Quantity);
                if (e.IsMine) ItemsLooted += qty;
                if (Mob(e) is { } mob)
                {
                    mob.ItemsLooted += qty;
                    if (e.IsMine) mob.MyItemsLooted += qty;
                }
                _recentLoot.Insert(0, e);
                if (_recentLoot.Count > RecentLootCap) _recentLoot.RemoveAt(_recentLoot.Count - 1);
                break;
            }

            case LedgerKind.CoinLoot:
                CountCorpse(e);
                e.MyCoinCp = MyCoinShare(e);
                LootCoinCp += e.MyCoinCp;
                if (e.MyCoinCp > 0) _coinSamples.Add((e.Time, e.MyCoinCp));
                if (Mob(e) is { } coinMob) coinMob.MyCoinCp += e.MyCoinCp;
                break;

            case LedgerKind.VendorSale when e.IsMine:
                e.MyCoinCp = e.CoinTotalCp;
                SalesCp += e.CoinTotalCp;
                break;

            case LedgerKind.LevelUp when e.IsMine:
                if (Level.HasValue && e.Level > Level) LevelsGained += e.Level.Value - Level.Value;
                else if (!Level.HasValue) LevelsGained++;
                Level = e.Level ?? Level;
                break;
        }
    }

    /// <summary>
    /// Your coin from a corpse. When a groupmate loots, the ledger logs "party_split" with each member's share.
    /// When you loot, it logs "npc_corpse" with the corpse's whole coin even while grouped, and the game then
    /// splits it, so divide by the group size.
    /// </summary>
    private long MyCoinShare(LedgerEvent e)
    {
        if (string.Equals(e.CoinKind, "party_split", StringComparison.OrdinalIgnoreCase))
        {
            if (e.CoinShareCp.HasValue) return e.CoinShareCp.Value;
            return e.IsMine ? e.CoinTotalCp / Math.Max(1, PartySizeAt(e.Time)) : 0;
        }
        return e.IsMine ? e.CoinTotalCp / Math.Max(1, PartySizeAt(e.Time)) : 0;
    }

    private int PartySizeAt(DateTimeOffset time) =>
        _inParty || time - _lastSplitSeen < SplitEvidenceWindow ? Math.Max(1, _partySize) : 1;

    private void TrackParty(LedgerEvent e)
    {
        if (e.Kind == LedgerKind.Party)
        {
            switch (e.Action)
            {
                case "act_31": // created
                case "act_32": // joined
                case "act_35": // leader changed
                    _inParty = true;
                    if (e.PartySize is > 0) _partySize = Math.Min(MaxPartySize, e.PartySize.Value);
                    break;
                case "act_33": // disbanded
                    _inParty = false;
                    _partySize = 1;
                    break;
                case "act_34": // someone left
                    if (e.IsMine)
                    {
                        _inParty = false;
                        _partySize = 1;
                    }
                    else
                    {
                        _partySize = e.PartySize is > 0 ? Math.Min(MaxPartySize, e.PartySize.Value) : Math.Max(1, _partySize - 1);
                    }
                    break;
            }
        }
        else if (e.Kind == LedgerKind.CoinLoot && !e.IsMine && e.CoinShareCp is > 0
                 && string.Equals(e.CoinKind, "party_split", StringComparison.OrdinalIgnoreCase)
                 // The game splits actual coins, so a lone silver piece lands on one member and skews the ratio.
                 // Only copper-only piles big enough to divide cleanly give a reliable size.
                 && e.CoinTotalCp is >= 20 and < CoinFormat.Silver && e.CoinShareCp < CoinFormat.Silver)
        {
            // A groupmate's split shows the real group size: total / per-member share (e.g. 71 / 14 = 5).
            _partySize = (int)Math.Clamp(Math.Round(e.CoinTotalCp / (double)e.CoinShareCp.Value), 1, MaxPartySize);
            _inParty = _partySize > 1;
            _lastSplitSeen = e.Time;
        }
    }

    private MobSummary? Mob(LedgerEvent e)
    {
        if (string.IsNullOrEmpty(e.Corpse)) return null;
        if (!_mobs.TryGetValue(e.Corpse, out var mob))
        {
            mob = new MobSummary { Name = e.Corpse, FirstSeen = e.Time };
            _mobs[e.Corpse] = mob;
        }
        mob.LastSeen = e.Time;
        if (e.Zone.Length > 0) mob.Zone = e.Zone;
        return mob;
    }

    private void CountCorpse(LedgerEvent e)
    {
        if (string.IsNullOrEmpty(e.Corpse)) return;
        if (!_lastCorpseEvent.TryGetValue(e.Corpse, out var last) || e.Time - last > SameCorpseWindow)
        {
            Kills++;
            Mob(e)!.Corpses++;
            _corpseSamples.Add((e.Time, 1));
        }
        _lastCorpseEvent[e.Corpse] = e.Time;
    }
}
