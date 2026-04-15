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

    public void RecordRun(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        int totalGames,
        int totalCountries,
        int totalChecked,
        int totalChanges,
        int totalSkipped,
        bool cancelled)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_history
                (started_at, completed_at, total_games, total_countries,
                 total_checked, total_changes, total_skipped, cancelled)
            VALUES
                (@startedAt, @completedAt, @totalGames, @totalCountries,
                 @totalChecked, @totalChanges, @totalSkipped, @cancelled)
            """;
        cmd.Parameters.AddWithValue("@startedAt", startedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@completedAt", completedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@totalGames", totalGames);
        cmd.Parameters.AddWithValue("@totalCountries", totalCountries);
        cmd.Parameters.AddWithValue("@totalChecked", totalChecked);
        cmd.Parameters.AddWithValue("@totalChanges", totalChanges);
        cmd.Parameters.AddWithValue("@totalSkipped", totalSkipped);
        cmd.Parameters.AddWithValue("@cancelled", cancelled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public StorageStatistics GetStatistics(int topDiscountLimit = 5)
    {
        RunStatistics runs;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN cancelled = 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN cancelled != 0 THEN 1 ELSE 0 END),
                    COALESCE(SUM(total_checked), 0),
                    COALESCE(SUM(total_changes), 0),
                    COALESCE(AVG(total_checked), 0.0),
                    COALESCE(AVG((julianday(completed_at) - julianday(started_at)) * 86400.0), 0.0),
                    MIN(started_at),
                    MAX(completed_at)
                FROM run_history
                """;

            using var reader = cmd.ExecuteReader();
            reader.Read();
            runs = new RunStatistics(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                ParseTimestamp(reader, 7),
                ParseTimestamp(reader, 8));
        }

        SnapshotStatistics snapshots;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(*),
                    COUNT(DISTINCT game_id),
                    COUNT(DISTINCT country),
                    COUNT(DISTINCT game_id || '|' || country),
                    COALESCE(SUM(CASE WHEN is_available = 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN discount_percent IS NOT NULL AND discount_percent > 0 THEN 1 ELSE 0 END), 0),
                    MIN(timestamp),
                    MAX(timestamp)
                FROM price_history
                """;

            using var reader = cmd.ExecuteReader();
            reader.Read();
            snapshots = new SnapshotStatistics(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                ParseTimestamp(reader, 6),
                ParseTimestamp(reader, 7));
        }

        var topDiscounts = new List<DiscountStat>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    game_name,
                    game_id,
                    country,
                    discount_percent,
                    discounted_price,
                    base_price,
                    timestamp
                FROM price_history
                WHERE is_available = 1 AND discount_percent IS NOT NULL AND discount_percent > 0
                ORDER BY discount_percent DESC, timestamp DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@limit", topDiscountLimit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var gameName = reader.IsDBNull(0) ? null : reader.GetString(0);
                var gameId = reader.GetString(1);
                topDiscounts.Add(new DiscountStat(
                    string.IsNullOrWhiteSpace(gameName) ? gameId : gameName,
                    reader.GetString(2).ToUpperInvariant(),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    ParseTimestamp(reader, 6)));
            }
        }

        return new StorageStatistics(runs, snapshots, topDiscounts);
    }

    public void Dispose() => _conn.Dispose();


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
            CREATE TABLE IF NOT EXISTS run_history (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at      TEXT    NOT NULL,
                completed_at    TEXT    NOT NULL,
                total_games     INTEGER NOT NULL,
                total_countries INTEGER NOT NULL,
                total_checked   INTEGER NOT NULL,
                total_changes   INTEGER NOT NULL,
                total_skipped   INTEGER NOT NULL,
                cancelled       INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_game_country_ts
                ON price_history(game_id, country, timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_run_history_started_at
                ON run_history(started_at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    private static DateTime? ParseTimestamp(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        return DateTime.Parse(reader.GetString(index), null, System.Globalization.DateTimeStyles.RoundtripKind);
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

public sealed record RunStatistics(
    int TotalRuns,
    int CompletedRuns,
    int CancelledRuns,
    int TotalCheckedPairs,
    int TotalChanges,
    double AverageCheckedPairsPerRun,
    double AverageDurationSeconds,
    DateTime? FirstRunAtUtc,
    DateTime? LastRunAtUtc);

public sealed record SnapshotStatistics(
    int TotalSnapshots,
    int TitlesTracked,
    int CountriesTracked,
    int GameCountryPairsTracked,
    int UnavailableSnapshots,
    int DiscountedSnapshots,
    DateTime? FirstSnapshotAtUtc,
    DateTime? LastSnapshotAtUtc);

public sealed record DiscountStat(
    string GameName,
    string Country,
    int DiscountPercent,
    string? CurrentPrice,
    string? BasePrice,
    DateTime? TimestampUtc);

public sealed record StorageStatistics(
    RunStatistics Runs,
    SnapshotStatistics Snapshots,
    IReadOnlyList<DiscountStat> TopDiscounts);
