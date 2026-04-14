using PSPriceNotification.Models;

namespace PSPriceNotification.Tests.Models;

public class PriceInfoTests
{
    // ─── CurrentPrice ─────────────────────────────────────────────────────────

    [Fact]
    public void CurrentPrice_ReturnsDiscountedPrice_WhenPresent()
    {
        var info = new PriceInfo("$39.99", "$19.99", "USD", 50, false, true);
        Assert.Equal("$19.99", info.CurrentPrice);
    }

    [Fact]
    public void CurrentPrice_FallsBackToBasePrice_WhenNoDiscount()
    {
        var info = new PriceInfo("$39.99", null, "USD", null, false, true);
        Assert.Equal("$39.99", info.CurrentPrice);
    }

    [Fact]
    public void CurrentPrice_IsNull_WhenBothPricesAbsent()
    {
        var info = new PriceInfo(null, null, null, null, false, false);
        Assert.Null(info.CurrentPrice);
    }

    // ─── ToString ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToString_ReturnsFree_WhenIsFreeIsTrue()
    {
        var info = new PriceInfo("0", null, "USD", null, true, true);
        Assert.Equal("Free", info.ToString());
    }

    [Fact]
    public void ToString_ShowsDiscountWithPercent_WhenDiscountedPriceDiffersAndPercentKnown()
    {
        var info = new PriceInfo("$39.99", "$19.99", "USD", 50, false, true);
        Assert.Equal("$19.99 (-50%) (was $39.99)", info.ToString());
    }

    [Fact]
    public void ToString_ShowsDiscountWithoutPercent_WhenDiscountedPriceDiffersAndPercentUnknown()
    {
        var info = new PriceInfo("$39.99", "$19.99", "USD", null, false, true);
        Assert.Equal("$19.99 (was $39.99)", info.ToString());
    }

    [Fact]
    public void ToString_ShowsBasePrice_WhenNoDiscount()
    {
        var info = new PriceInfo("$39.99", null, "USD", null, false, true);
        Assert.Equal("$39.99", info.ToString());
    }

    [Fact]
    public void ToString_ShowsBasePrice_WhenDiscountedMatchesBase()
    {
        var info = new PriceInfo("$39.99", "$39.99", "USD", 0, false, true);
        Assert.Equal("$39.99", info.ToString());
    }

    [Fact]
    public void ToString_ReturnsNA_WhenNoPricesAvailable()
    {
        var info = new PriceInfo(null, null, null, null, false, false);
        Assert.Equal("N/A", info.ToString());
    }

    // ─── Record equality ──────────────────────────────────────────────────────

    [Fact]
    public void RecordEquality_HoldsForIdenticalValues()
    {
        var a = new PriceInfo("$9.99", null, "USD", null, false, true);
        var b = new PriceInfo("$9.99", null, "USD", null, false, true);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_FailsForDifferentPrices()
    {
        var a = new PriceInfo("$9.99", null, "USD", null, false, true);
        var b = new PriceInfo("$19.99", null, "USD", null, false, true);
        Assert.NotEqual(a, b);
    }
}
