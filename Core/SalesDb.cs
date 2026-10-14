using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SpawnTimer.Core;

/// <summary>An item's merchant price from your own sales (latest sale, any character).</summary>
public sealed record ItemPrice(string Item, double UnitCp, DateTimeOffset LastSold, int Sales, string Character);

/// <summary>
/// Every merchant sale from the Ledger (act_24), kept in an SQLite file next to settings.json
/// (Documents\Spawn Timer\sales.db) so price history survives reinstalls and the game pruning old Ledger files.
/// Prices are served from an in-memory copy, so overlays never wait on the database.
/// </summary>
public sealed class SalesDb : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private volatile Dictionary<string, ItemPrice> _prices = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultPath => Path.Combine(SettingsStore.Folder, "sales.db");

    public string FilePath { get; }

    /// <summary>Raised (on whichever thread imported) when new sales changed the price list.</summary>
    public event Action? PricesChanged;

    private SalesDb(string path, SqliteConnection db)
    {
        FilePath = path;
        _db = db;
    }

    /// <summary>Opens (creating if needed) the database and loads the price list.</summary>
    public static SalesDb Open(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        var sales = new SalesDb(path, db);
        sales.Migrate();
        sales.ReloadPrices();
        return sales;
    }

    private void Migrate()
    {
        Exec("PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (version < 1)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS sales (
                    ledger_key  TEXT PRIMARY KEY,
                    sold_at_ms  INTEGER NOT NULL,
                    sold_at     TEXT    NOT NULL,
                    server      TEXT    NOT NULL,
                    character   TEXT    NOT NULL,
                    item        TEXT    NOT NULL COLLATE NOCASE,
                    quantity    INTEGER NOT NULL,
                    total_cp    INTEGER NOT NULL,
                    zone        TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS sales_item ON sales (item, sold_at_ms);
                """);
        }
        if (version != SchemaVersion) Exec($"PRAGMA user_version = {SchemaVersion};");
    }

    public int SaleCount
    {
        get
        {
            lock (_lock) return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sales;"));
        }
    }

    public int PricedItemCount => _prices.Count;

    /// <summary>Your latest price for an item, or null if you've never sold it.</summary>
    public ItemPrice? TryGetPrice(string? item) =>
        item != null && _prices.TryGetValue(item.Trim(), out var p) ? p : null;

    /// <summary>Unit price in copper, or null (for the meter's value lines).</summary>
    public double? UnitPrice(string item) => TryGetPrice(item)?.UnitCp;

    /// <summary>Adds the vendor sales among <paramref name="events"/>; already-known sales are skipped. Returns how many were new.</summary>
    public int Import(IEnumerable<LedgerEvent> events)
    {
        var sales = events.Where(e => e.Kind == LedgerKind.VendorSale && !string.IsNullOrWhiteSpace(e.Item) && e.Quantity > 0).ToList();
        if (sales.Count == 0) return 0;
        var added = 0;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO sales (ledger_key, sold_at_ms, sold_at, server, character, item, quantity, total_cp, zone)
                VALUES ($key, $ms, $at, $server, $character, $item, $qty, $total, $zone);
                """;
            var key = cmd.Parameters.Add("$key", SqliteType.Text);
            var ms = cmd.Parameters.Add("$ms", SqliteType.Integer);
            var at = cmd.Parameters.Add("$at", SqliteType.Text);
            var server = cmd.Parameters.Add("$server", SqliteType.Text);
            var character = cmd.Parameters.Add("$character", SqliteType.Text);
            var item = cmd.Parameters.Add("$item", SqliteType.Text);
            var qty = cmd.Parameters.Add("$qty", SqliteType.Integer);
            var total = cmd.Parameters.Add("$total", SqliteType.Integer);
            var zone = cmd.Parameters.Add("$zone", SqliteType.Text);
            foreach (var e in sales)
            {
                key.Value = e.Key;
                ms.Value = e.Time.ToUnixTimeMilliseconds();
                at.Value = e.Time.ToString("o");
                server.Value = e.Server;
                character.Value = e.Owner.Length > 0 ? e.Owner : e.Actor;
                item.Value = e.Item!.Trim();
                qty.Value = e.Quantity;
                total.Value = e.CoinTotalCp;
                zone.Value = e.Zone;
                added += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (added > 0)
        {
            ReloadPrices();
            PricesChanged?.Invoke();
        }
        return added;
    }

    /// <summary>
    /// Imports every sale in every character's Ledger folder (archives included) under <paramref name="gameDataRoot"/>.
    /// Read-only on the game's files; safe to run while the game writes them. Returns how many sales were new.
    /// </summary>
    public int Backfill(string gameDataRoot, CancellationToken ct = default)
    {
        if (!Directory.Exists(gameDataRoot)) return 0;
        var added = 0;
        foreach (var ledger in Directory.EnumerateDirectories(gameDataRoot, "Ledger", SearchOption.AllDirectories))
        {
            var server = LedgerWatcher.ServerOf(ledger);
            foreach (var file in Directory.EnumerateFiles(ledger, "*.json", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                List<LedgerEvent> events;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    events = LedgerParser.Parse(reader.ReadToEnd());
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    continue; // mid-write or unreadable; the live watcher picks up anything new
                }
                foreach (var e in events) e.Server = server;
                added += Import(events);
            }
        }
        return added;
    }

    public Task<int> BackfillAsync(string gameDataRoot, CancellationToken ct = default) =>
        Task.Run(() => Backfill(gameDataRoot, ct), ct);

    /// <summary>Rebuilds the in-memory price list: each item's latest sale, plus how many times you've sold it.</summary>
    private void ReloadPrices()
    {
        var prices = new Dictionary<string, ItemPrice>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT s.item, s.total_cp, s.quantity, s.sold_at_ms, s.character, c.n
                FROM sales s
                JOIN (SELECT item, MAX(sold_at_ms) AS last, COUNT(*) AS n FROM sales GROUP BY item) c
                  ON s.item = c.item AND s.sold_at_ms = c.last;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                var qty = Math.Max(1, r.GetInt64(2));
                prices[name] = new ItemPrice(name, r.GetInt64(1) / (double)qty,
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).ToLocalTime(), r.GetInt32(5), r.GetString(4));
            }
        }
        _prices = prices;
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
