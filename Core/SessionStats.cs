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
    // A groupmate's split shows how many shared the coin (only members in range do), so it beats the headcount
    // for a few minutes. Backtested on 1,232 real splits: right split count 95.5% of the time (was 91.4%).
    private static readonly TimeSpan RecentSplitWindow = TimeSpan.FromMinutes(5);
    // Groupmates seen looting recently (Social entries only log your party), for when no join was logged.
    private static readonly TimeSpan SeenMemberWindow = TimeSpan.FromMinutes(10);
    private int _partySize = 1;
    private bool _inParty;
    private DateTimeOffset _lastSplitSeen = DateTimeOffset.MinValue;
    private int _recentSplitCount = 1;
    private DateTimeOffset _recentSplitTime = DateTimeOffset.MinValue;
    private readonly HashSet<string> _roster = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _seenMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _classes = new(StringComparer.OrdinalIgnoreCase);
    // Your loots are split n ways but coins are whole; carry the fraction so the session total isn't rounded down.
    private double _coinFraction;

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

    /// <summary>Time of the latest loot or coin in this session (for "long break" detection).</summary>
    public DateTimeOffset? LastLootTime { get; private set; }
    public IReadOnlyList<LedgerEvent> RecentLoot => _recentLoot;

    /// <summary>Every ledger event in this session, oldest first (for export).</summary>
    public IReadOnlyList<LedgerEvent> Events => _events;

    public IReadOnlyCollection<MobSummary> Mobs => _mobs.Values;

    /// <summary>When the session ended (the game closed); null while it's running.</summary>
    public DateTimeOffset? EndTime { get; private set; }

    public bool IsEnded => EndTime != null;

    /// <summary>Freezes the session (totals stay; elapsed time stops).</summary>
    public void Stop(DateTimeOffset at) => EndTime ??= at;

    public TimeSpan Elapsed => (EndTime ?? DateTimeOffset.Now) - Start;

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

    /// <summary>
    /// Merchant value of the items you looted this session at <paramref name="unitPrice"/> (copper each, null = never
    /// sold). Priced when asked, so selling a new kind of item values every one already looted.
    /// </summary>
    public (double ValueCp, int Priced, int Total) ItemValue(Func<string, double?> unitPrice)
    {
        double value = 0;
        int priced = 0, total = 0;
        foreach (var e in MyItemLoots())
        {
            var qty = Math.Max(1, e.Quantity);
            total += qty;
            if (unitPrice(e.Item!) is not { } each) continue;
            priced += qty;
            value += each * qty;
        }
        return (value, priced, total);
    }

    /// <summary>Coin plus item value per hour, weighted toward recent loot like <see cref="CoinPerHour"/>.</summary>
    public double ValuePerHour(TimeSpan window, DateTimeOffset now, Func<string, double?> unitPrice)
    {
        var samples = new List<(DateTimeOffset Time, double Value)>(_coinSamples);
        var itemTotal = 0.0;
        foreach (var e in MyItemLoots())
            if (unitPrice(e.Item!) is { } each && each > 0)
            {
                var value = each * Math.Max(1, e.Quantity);
                samples.Add((e.Time, value));
                itemTotal += value;
            }
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        return RecentRate(samples, LootCoinCp + itemTotal, window, now);
    }

    private IEnumerable<LedgerEvent> MyItemLoots() =>
        _events.Where(e => e.Kind == LedgerKind.ItemLoot && e.IsMine && !string.IsNullOrEmpty(e.Item));

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
        LastLootTime = null;
        EndTime = null;
        _lastCorpseEvent.Clear();
        _mobs.Clear();
        _events.Clear();
        _recentLoot.Clear();
        _coinSamples.Clear();
        _corpseSamples.Clear();
        _coinFraction = 0;
    }

    /// <summary>Current split estimate (1 when solo), for the meter's "3-way" label.</summary>
    public int PartySize => PartySizeAt(DateTimeOffset.Now);

    /// <summary>Your class code from the Ledger (e.g. "CLR"), once a party entry has shown it; null until then.</summary>
    public string? MyClass { get; private set; }

    /// <summary>
    /// Your groupmates right now with their class codes when known: everyone who joined (or led) the group, plus
    /// anyone seen looting with you recently. Empty when solo.
    /// </summary>
    public IReadOnlyList<(string Name, string? Class)> PartyMembers(DateTimeOffset now)
    {
        var names = new HashSet<string>(_roster, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, seen) in _seenMembers)
            if (now - seen < SeenMemberWindow) names.Add(name);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => (n, _classes.TryGetValue(n, out var c) ? c : null)).ToList();
    }

    public void Apply(LedgerEvent e)
    {
        // Party state is tracked from all history (including before the session) so the first loot splits correctly.
        TrackParty(e);
        if (e.Time < Start || e.Time > EndTime) return;

        HasData = true;
        _events.Add(e);
        if (e.Zone.Length > 0) Zone = e.Zone;
        if (e.Kind is LedgerKind.ItemLoot or LedgerKind.CoinLoot && (LastLootTime == null || e.Time > LastLootTime)) LastLootTime = e.Time;

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
        if (string.Equals(e.CoinKind, "party_split", StringComparison.OrdinalIgnoreCase) && e.CoinShareCp.HasValue)
            return e.CoinShareCp.Value;
        if (!e.IsMine) return 0;
        var n = PartySizeAt(e.Time);
        if (n <= 1) return e.CoinTotalCp;
        // Whole coins go to whoever the game picks, so over a session each member averages total / n.
        _coinFraction += e.CoinTotalCp / (double)n;
        var whole = (long)Math.Floor(_coinFraction + 1e-9);
        _coinFraction -= whole;
        return whole;
    }

    /// <summary>How many people a corpse's coin is split between at <paramref name="time"/> (1 when solo).</summary>
    public int PartySizeAt(DateTimeOffset time)
    {
        if (time - _recentSplitTime < RecentSplitWindow && time >= _recentSplitTime) return _recentSplitCount;
        var n = _inParty || time - _lastSplitSeen < SplitEvidenceWindow ? Math.Max(1, _partySize) : 1;
        var seen = _seenMembers.Count(m => time >= m.Value && time - m.Value < SeenMemberWindow);
        return Math.Clamp(Math.Max(n, 1 + seen), 1, MaxPartySize);
    }

    /// <summary>Group sizes that turn <paramref name="total"/> into <paramref name="share"/> (shares round either way).</summary>
    public static List<int> SplitCounts(long total, long share)
    {
        var counts = new List<int>();
        for (var n = 1; n <= MaxPartySize; n++)
            if (total / n == share || (total + n - 1) / n == share) counts.Add(n);
        return counts;
    }

    private void LeaveParty()
    {
        _inParty = false;
        _partySize = 1;
        _roster.Clear();
        _seenMembers.Clear();
        _recentSplitTime = DateTimeOffset.MinValue;
    }

    private void TrackParty(LedgerEvent e)
    {
        // f10 on party entries is the actor's class (0.30.3+).
        if (!string.IsNullOrEmpty(e.ActorClass) && e.Actor.Length > 0)
        {
            _classes[e.Actor] = e.ActorClass;
            if (e.IsMine) MyClass = e.ActorClass;
        }

        if (e.Kind == LedgerKind.Party)
        {
            switch (e.Action)
            {
                case "act_31": // created
                case "act_32": // joined (f02 names the leader)
                case "act_35": // leader changed
                    _inParty = true;
                    if (e.PartySize is > 0) _partySize = Math.Min(MaxPartySize, e.PartySize.Value);
                    AddMember(e.Actor, e);
                    AddMember(e.Subject, e);
                    break;
                case "act_33": // disbanded
                    LeaveParty();
                    break;
                case "act_34": // someone left
                    if (e.IsMine) LeaveParty();
                    else
                    {
                        _partySize = e.PartySize is > 0 ? Math.Min(MaxPartySize, e.PartySize.Value) : Math.Max(1, _partySize - 1);
                        _roster.Remove(e.Actor);
                        _seenMembers.Remove(e.Actor);
                    }
                    break;
            }
            return;
        }

        if (e.Kind is not (LedgerKind.CoinLoot or LedgerKind.ItemLoot) || e.IsMine || e.Actor.Length == 0) return;
        _seenMembers[e.Actor] = e.Time;

        // A groupmate's split shows how many shared the corpse's coin. The game splits whole coins, so a silver
        // piece lands on one member; only copper piles give a clean count.
        if (e.Kind == LedgerKind.CoinLoot && e.CoinShareCp is > 0
            && string.Equals(e.CoinKind, "party_split", StringComparison.OrdinalIgnoreCase)
            && e.CoinTotalCp < CoinFormat.Silver && e.CoinShareCp < CoinFormat.Silver)
        {
            var counts = SplitCounts(e.CoinTotalCp, e.CoinShareCp.Value);
            if (counts.Count == 0) return;
            // Small piles fit several counts (5c / 1c = 3, 4 or 5): keep the current count if it fits.
            var n = counts.Contains(_recentSplitCount) ? _recentSplitCount
                : counts.Count == 1 ? counts[0]
                : counts.OrderBy(c => Math.Abs(c - _partySize)).First();
            _recentSplitCount = n;
            _recentSplitTime = _lastSplitSeen = e.Time;
            if (n > 1) _inParty = true;
            if (counts.Count == 1 && counts[0] > _partySize) _partySize = counts[0]; // the group is at least this big
        }
    }

    private void AddMember(string? name, LedgerEvent e)
    {
        if (string.IsNullOrEmpty(name) || name.Equals(e.Owner, StringComparison.OrdinalIgnoreCase)) return;
        _roster.Add(name);
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
