using PSPriceNotification.Models;
using PSPriceNotification.Services;

namespace PSPriceNotification.Tests.Services;

public class PSStoreParserTests
{
    // ─── __NEXT_DATA__ Apollo cache (Strategy 1) ──────────────────────────────

    [Fact]
    public void ParsePrice_ExtractsPrice_FromApolloCache_ByTypename()
    {
        // A minimal __NEXT_DATA__ blob that contains a price node with __typename
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"productId":"test","price":{"__typename":"PriceReturned","basePrice":"$39.99","discountedPrice":"$19.99","currencyCode":"USD","discountText":"-50%"}}}}
            </script>
            </head><body></body></html>
            """;

        var result = PSStoreParser.ParsePrice(html);

        Assert.NotNull(result);
        Assert.Equal("$39.99", result.BasePrice);
        Assert.Equal("$19.99", result.DiscountedPrice);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(50, result.DiscountPercent);
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void ParsePrice_ExtractsPrice_FromApolloCache_ByFieldDetection()
    {
        // A price node without __typename but with recognisable field names
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"data":{"pricing":{"basePrice":"€59.99","currencyCode":"EUR"}}}}}
            </script>
            </head><body></body></html>
            """;

        var result = PSStoreParser.ParsePrice(html);

        Assert.NotNull(result);
        Assert.Equal("€59.99", result.BasePrice);
        Assert.Equal("EUR", result.Currency);
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void ParsePrice_DetectsFreeGame_WhenIsFreeIsTrue()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"price":{"__typename":"PriceReturned","basePrice":"0","isFree":true,"currencyCode":"USD"}}}}
            </script>
            </head><body></body></html>
            """;

        var result = PSStoreParser.ParsePrice(html);

        Assert.NotNull(result);
        Assert.True(result.IsFree);
    }

    // ─── JSON-LD (Strategy 2) ─────────────────────────────────────────────────

    [Fact]
    public void ParsePrice_ExtractsPrice_FromJsonLd()
    {
        const string html = """
            <html><head>
            <script type="application/ld+json">
            {"@type":"Product","name":"Test Game","offers":{"@type":"Offer","price":"29.99","priceCurrency":"GBP"}}
            </script>
            </head><body></body></html>
            """;

        var result = PSStoreParser.ParsePrice(html);

        Assert.NotNull(result);
        // JSON-LD parser uses the "price" field as BasePrice
        Assert.Equal("29.99", result.BasePrice);
        Assert.Equal("GBP", result.Currency);
    }

    [Fact]
    public void ParsePrice_SkipsJsonLd_WhenTypeNotProduct()
    {
        // @type is "WebSite" — should not match JSON-LD strategy
        const string html = """
            <html><head>
            <script type="application/ld+json">
            {"@type":"WebSite","name":"PlayStation Store"}
            </script>
            </head><body></body></html>
            """;

        // No __NEXT_DATA__, no matching JSON-LD, no price keywords → null
        var result = PSStoreParser.ParsePrice(html);
        Assert.Null(result);
    }

    // ─── Regex fallback (Strategy 3) ─────────────────────────────────────────

    [Fact]
    public void ParsePrice_ExtractsPrice_FromRawRegex()
    {
        // Plain HTML that only has raw JSON-like price strings (no __NEXT_DATA__, no ld+json)
        const string html = """
            <html><body>
            <div data-price='"basePrice":"$24.99","discountedPrice":"$12.49","currencyCode":"USD"'></div>
            </body></html>
            """;

        var result = PSStoreParser.ParsePrice(html);

        Assert.NotNull(result);
        Assert.Equal("$24.99", result.BasePrice);
        Assert.Equal("$12.49", result.DiscountedPrice);
        Assert.Equal("USD", result.Currency);
    }

    // ─── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void ParsePrice_ReturnsNull_WhenHtmlHasNoKnownPriceData()
    {
        const string html = "<html><body><p>Nothing here</p></body></html>";
        var result = PSStoreParser.ParsePrice(html);
        Assert.Null(result);
    }

    [Fact]
    public void ParsePrice_ReturnsNull_OnEmptyHtml()
    {
        var result = PSStoreParser.ParsePrice(string.Empty);
        Assert.Null(result);
    }

    [Fact]
    public void ParsePrice_ReturnsNull_OnMalformedJson()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            { this is not valid json !!! }
            </script>
            </head></html>
            """;

        // Should not throw; gracefully returns null
        var result = PSStoreParser.ParsePrice(html);
        Assert.Null(result);
    }

    // ─── Individual strategy methods ─────────────────────────────────────────

    [Fact]
    public void ParseApolloCache_ReturnsNull_WhenScriptMissing()
    {
        var result = PSStoreParser.ParseApolloCache("<html><body></body></html>");
        Assert.Null(result);
    }

    [Fact]
    public void ParseJsonLd_ReturnsNull_WhenNoJsonLdPresent()
    {
        var result = PSStoreParser.ParseJsonLd("<html><body></body></html>");
        Assert.Null(result);
    }

    [Fact]
    public void ParseRegex_ReturnsNull_WhenNoPriceKeywords()
    {
        var result = PSStoreParser.ParseRegex("<html><body><p>hello</p></body></html>");
        Assert.Null(result);
    }
}
