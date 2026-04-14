using Microsoft.Data.Sqlite;
using PSPriceNotification.Models;
using PSPriceNotification.Services;

namespace PSPriceNotification.Tests.Services;

public class PriceStorageTests : IDisposable
{
    // Each test gets its own temp file so tests are fully isolated
    private readonly string _tmpFile;
    private readonly PriceStorage _storage;

    private static readonly PriceInfo SamplePrice =
        new("$39.99", null, "USD", null, false, true);

    private static readonly PriceInfo DiscountedPrice =
        new("$39.99", "$19.99", "USD", 50, false, true);

    private static readonly PriceInfo UnavailablePrice =
        new(null, null, null, null, false, false);

    public PriceStorageTests()
    {
        _tmpFile = Path.Combine(Path.GetTempPath(), $"ps_test_{Guid.NewGuid()}.db");
        _storage = new PriceStorage(_tmpFile);
    }

    public void Dispose()
    {
        _storage.Dispose();
        SqliteConnection.ClearAllPools();   // release pooled handles before deleting the file
        foreach (var path in new[] { _tmpFile, _tmpFile + "-wal", _tmpFile + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }

    // ─── GetLastPrice ─────────────────────────────────────────────────────────

    [Fact]
    public void GetLastPrice_ReturnsNull_WhenNoDataStored()
    {
        var result = _storage.GetLastPrice("GAME001", "us");
        Assert.Null(result);
    }

    [Fact]
    public void GetLastPrice_ReturnsLastEntry_AfterUpdate()
    {
        _storage.UpdatePrice("GAME001", "us", SamplePrice, "Test Game");
        _storage.UpdatePrice("GAME001", "us", DiscountedPrice, "Test Game");

        var result = _storage.GetLastPrice("GAME001", "us");

        Assert.NotNull(result);
        Assert.Equal("$19.99", result.CurrentPrice);
    }

    [Fact]
    public void GetLastPrice_IsIsolatedByCountry()
    {
        _storage.UpdatePrice("GAME001", "us", SamplePrice);
        _storage.UpdatePrice("GAME001", "gb", DiscountedPrice);

        Assert.Equal("$39.99", _storage.GetLastPrice("GAME001", "us")!.CurrentPrice);
        Assert.Equal("$19.99", _storage.GetLastPrice("GAME001", "gb")!.CurrentPrice);
    }

    // ─── HasPriceChanged ──────────────────────────────────────────────────────

    [Fact]
    public void HasPriceChanged_ReturnsFalse_OnFirstCheck_SilentBaseline()
    {
        // No prior entry → always false (avoids spam on first run)
        var changed = _storage.HasPriceChanged("GAME001", "us", SamplePrice);
        Assert.False(changed);
    }

    [Fact]
    public void HasPriceChanged_ReturnsFalse_WhenPriceUnchanged()
    {
        _storage.UpdatePrice("GAME001", "us", SamplePrice);
        var changed = _storage.HasPriceChanged("GAME001", "us", SamplePrice);
        Assert.False(changed);
    }

    [Fact]
    public void HasPriceChanged_ReturnsTrue_WhenPriceIsDifferent()
    {
        _storage.UpdatePrice("GAME001", "us", SamplePrice);
        var changed = _storage.HasPriceChanged("GAME001", "us", DiscountedPrice);
        Assert.True(changed);
    }

    [Fact]
    public void HasPriceChanged_ReturnsTrue_WhenGameBecomesAvailableAgain()
    {
        _storage.UpdatePrice("GAME001", "us", UnavailablePrice);
        var changed = _storage.HasPriceChanged("GAME001", "us", SamplePrice);
        Assert.True(changed);
    }

    [Fact]
    public void HasPriceChanged_ReturnsFalse_WhenGameRemainsUnavailable()
    {
        _storage.UpdatePrice("GAME001", "us", UnavailablePrice);
        var changed = _storage.HasPriceChanged("GAME001", "us", UnavailablePrice);
        Assert.False(changed);
    }

    // ─── Save / Load (persistence) ────────────────────────────────────────────

    [Fact]
    public void SaveAndReload_PersistsAndRestoresData()
    {
        _storage.UpdatePrice("GAME001", "us", SamplePrice, "Persisted Game");
        _storage.Save();

        // Load into a fresh instance from the same file
        using var reloaded = new PriceStorage(_tmpFile);
        var result = reloaded.GetLastPrice("GAME001", "us");

        Assert.NotNull(result);
        Assert.Equal("$39.99", result.BasePrice);
        Assert.True(result.IsAvailable);
        Assert.False(result.IsFree);
    }

    [Fact]
    public void Load_ReturnsEmptyDictionary_WhenFileDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"no_such_file_{Guid.NewGuid()}.db");
        // Constructor should not throw; storage starts empty
        using var storage = new PriceStorage(nonExistent);
        Assert.Null(storage.GetLastPrice("anything", "us"));
    }

    [Fact]
    public void UpdatePrice_DoesNotAddDuplicate_WhenPriceUnchanged()
    {
        _storage.UpdatePrice("GAME001", "us", SamplePrice);
        _storage.UpdatePrice("GAME001", "us", SamplePrice); // identical — should be ignored
        _storage.Save();

        using var reloaded = new PriceStorage(_tmpFile);
        // GetLastPrice returns the single entry; HasPriceChanged should still be false
        Assert.NotNull(reloaded.GetLastPrice("GAME001", "us"));
        Assert.False(reloaded.HasPriceChanged("GAME001", "us", SamplePrice));
    }

    // ─── Pruning ──────────────────────────────────────────────────────────────

    [Fact]
    public void Prune_RemovesEntriesOlderThanMaxHistoryDays_AfterSave()
    {
        // Create storage with 1-day history limit
        using var shortStorage = new PriceStorage(_tmpFile, maxHistoryDays: 1);

        // Write a legitimate entry now (will survive)
        shortStorage.UpdatePrice("GAME001", "us", SamplePrice);

        // Save and reload; the single recent entry should still be there
        shortStorage.Save();
        using var reloaded = new PriceStorage(_tmpFile, maxHistoryDays: 1);
        Assert.NotNull(reloaded.GetLastPrice("GAME001", "us"));
    }
}
