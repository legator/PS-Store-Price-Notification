using System.Net;
using PSPriceNotification.Services;
using PSPriceNotification.Tests.Helpers;

namespace PSPriceNotification.Tests.Services;

public class PSStoreClientTests
{
    // ─── GetStoreUrl ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("us", "en-us")]
    [InlineData("gb", "en-gb")]
    [InlineData("de", "de-de")]
    [InlineData("jp", "ja-jp")]
    [InlineData("br", "pt-br")]
    public void GetStoreUrl_BuildsCorrectLocaleSegment(string country, string expectedLocale)
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.OK));
        var url = client.GetStoreUrl("PPSA123456_00", "concept", country);
        Assert.Contains($"/{expectedLocale}/concept/PPSA123456_00", url);
    }

    [Fact]
    public void GetStoreUrl_FallsBackToEnUs_ForUnknownCountry()
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.OK));
        var url = client.GetStoreUrl("PPSA123456_00", "concept", "xx");
        Assert.Contains("/en-us/concept/PPSA123456_00", url);
    }

    // ─── GetPriceAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceAsync_ReturnsNull_ForUnknownCountry()
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.OK));
        var result = await client.GetPriceAsync("PPSA123456_00", "concept", "xx");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriceAsync_ReturnsUnavailable_On404()
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.NotFound));
        var result = await client.GetPriceAsync("PPSA123456_00", "concept", "us");
        Assert.NotNull(result);
        Assert.False(result.IsAvailable);
    }

    [Fact]
    public async Task GetPriceAsync_ReturnsNull_On429()
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler((HttpStatusCode)429));
        var result = await client.GetPriceAsync("PPSA123456_00", "concept", "us");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriceAsync_ReturnsNull_OnServerError()
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.InternalServerError));
        var result = await client.GetPriceAsync("PPSA123456_00", "concept", "us");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriceAsync_ParsesPrice_On200WithValidHtml()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"price":{"__typename":"PriceReturned","basePrice":"$39.99","currencyCode":"USD"}}}}
            </script>
            </head><body></body></html>
            """;

        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.OK, html));
        var result = await client.GetPriceAsync("PPSA123456_00", "concept", "us");

        Assert.NotNull(result);
        Assert.Equal("$39.99", result.BasePrice);
        Assert.Equal("USD", result.Currency);
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public async Task GetPriceAsync_ReturnsNull_On200WithBlankHtml()
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "<html></html>"));
        var result = await client.GetPriceAsync("PPSA123456_00", "concept", "us");
        // Blank HTML has no parseable price data
        Assert.Null(result);
    }

    // ─── Locales dictionary ───────────────────────────────────────────────────

    [Fact]
    public void Locales_ContainsExpectedCountries()
    {
        Assert.True(PSStoreClient.DefaultLocales.ContainsKey("us"));
        Assert.True(PSStoreClient.DefaultLocales.ContainsKey("gb"));
        Assert.True(PSStoreClient.DefaultLocales.ContainsKey("jp"));
        Assert.True(PSStoreClient.DefaultLocales.ContainsKey("au"));
        Assert.True(PSStoreClient.DefaultLocales.ContainsKey("br"));
    }

    [Fact]
    public void Locales_IsCaseInsensitive()
    {
        Assert.True(PSStoreClient.DefaultLocales.ContainsKey("US"));
        Assert.True(PSStoreClient.DefaultLocales.ContainsKey("Gb"));
        Assert.Equal(PSStoreClient.DefaultLocales["us"], PSStoreClient.DefaultLocales["US"]);
    }
}
