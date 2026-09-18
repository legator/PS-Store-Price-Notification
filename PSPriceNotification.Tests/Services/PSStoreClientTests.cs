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

    // ─── GetPriceDetailedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetPriceDetailedAsync_ReturnsParseFailedWithDiagnosis_OnBlankHtml()
    {
        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "<html><body><title>Test Page</title></body></html>"));
        var result = await client.GetPriceDetailedAsync("PPSA123456_00", "concept", "us");

        Assert.Equal(FetchStatus.ParseFailed, result.Status);
        Assert.Null(result.Price);
        Assert.NotNull(result.Diagnosis);
        Assert.Contains("Unable to extract price", result.Diagnosis);
    }

    [Fact]
    public async Task GetPriceDetailedAsync_ReturnsSuccess_OnValidHtml()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"price":{"__typename":"PriceReturned","basePrice":"$49.99","currencyCode":"USD"}}}}
            </script>
            </head></html>
            """;

        using var client = new PSStoreClient(new FakeHttpMessageHandler(HttpStatusCode.OK, html));
        var result = await client.GetPriceDetailedAsync("PPSA123456_00", "concept", "us");

        Assert.Equal(FetchStatus.Success, result.Status);
        Assert.NotNull(result.Price);
        Assert.Equal("$49.99", result.Price.BasePrice);
    }

    [Fact]
    public async Task GetPriceDetailedAsync_ReturnsErrorPage_WithRegionAndMismatchHint()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://store.playstation.com/en-us/error?productId=EP9000-PPSA21567_00-DDE0000000000000&widgetErrorType=emptyRequiredBatarang&widgetStatusCode=204"),
                Content = new StringContent("<html><body>Error</body></html>", System.Text.Encoding.UTF8, "text/html"),
            };
            return res;
        });

        using var client = new PSStoreClient(handler);
        var result = await client.GetPriceDetailedAsync("EP9000-PPSA21567_00-DDE0000000000000", "product", "us");

        Assert.Equal(FetchStatus.ErrorPage, result.Status);
        Assert.NotNull(result.Price);
        Assert.False(result.Price.IsAvailable);
        Assert.Contains("code: 204", result.Message);
        Assert.Contains("European region prefix 'EP'", result.Message);
    }
}
