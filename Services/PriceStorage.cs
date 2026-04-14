using Microsoft.Data.Sqlite;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

public sealed class PriceStorage : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly int _maxHistoryDays;

    public PriceStorage(string dbPath, int maxHistoryDays = 90)
    {
        _maxHistoryDays = maxHistoryDays;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    public PriceInfo? GetLastPrice(string gameId, string country)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT base_price, discounted_price, currency, discount_percent, is_free, is_available
            FROM price_history
            WHERE game_id = @gameId AND country = @country
            ORDER BY timestamp DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@country", country.ToLowerInvariant());

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new PriceInfo(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0);
    }

    public bool HasPriceChanged(string gameId, string country, PriceInfo newPrice)
    {
        var last = GetLastPrice(gameId, country);
        if (last == null) return false;  // first run — silent baseline

        if (!last.IsAvailable && newPrice.IsAvailable) return true;
        if (!newPrice.IsAvailable) return false;
        if (last.IsFree != newPrice.IsFree) return true;

        return last.CurrentPrice != newPrice.CurrentPrice;
    }

    public void UpdatePrice(string gameId, string country, PriceInfo price, string gameName = "")
    {
        var last = GetLastPrice(gameId, country);
        if (last != null)
        {
            bool sameAsLast = last.IsAvailable == price.IsAvailable
                           && last.IsFree       == price.IsFree
                           && last.CurrentPrice == price.CurrentPrice;
            if (sameAsLast) return;
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO price_history
                (game_id, country, game_name, timestamp,
                 base_price, discounted_price, currency, discount_percent,
                 is_free, is_available)
            VALUES
                (@gameId, @country, @gameName, @timestamp,
                 @basePrice, @discountedPrice, @currency, @discountPercent,
                 @isFree, @isAvailable)
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@country", country.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@gameName", gameName);
        cmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@basePrice", (object?)price.BasePrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@discountedPrice", (object?)price.DiscountedPrice ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@currency", (object?)price.Currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@discountPercent", (object?)price.DiscountPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isFree", price.IsFree ? 1 : 0);
        cmd.Parameters.AddWithValue("@isAvailable", price.IsAvailable ? 1 : 0);
        cmd.ExecuteNonQuery();

        Prune(gameId, country);
    }

    public void Save() { }

    public void Dispose() => _conn.Dispose();

    // ─── Internals ───────────────────────────────────────────────────────────

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS price_history (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                game_id          TEXT    NOT NULL,
                country          TEXT    NOT NULL,
                game_name        TEXT    NOT NULL DEFAULT '',
                timestamp        TEXT    NOT NULL,
                base_price       TEXT,
                discounted_price TEXT,
                currency         TEXT,
                discount_percent INTEGER,
                is_free          INTEGER NOT NULL,
                is_available     INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_game_country_ts
                ON price_history(game_id, country, timestamp DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    private void Prune(string gameId, string country)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_maxHistoryDays).ToString("O");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM price_history
            WHERE game_id = @gameId AND country = @country AND timestamp < @cutoff
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@country", country.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }
}
