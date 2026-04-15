using System.Text.Json;
using PSPriceNotification.Models;
using PSPriceNotification.Services;

namespace PSPriceNotification.Tests.Services;

public class PsnWishlistServiceTests
{
    // ─── ParseWishlistFromHtml ────────────────────────────────────────────────

    [Fact]
    public void ParseWishlistFromHtml_ReturnsEmpty_WhenNoNextData()
    {
        const string html = "<html><body><p>No script here</p></body></html>";
        var result = PsnWishlistService.ParseWishlistFromHtml(html);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseWishlistFromHtml_ReturnsEmpty_WhenNextDataHasNoWishlistItems()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{}}}
            </script>
            </head><body></body></html>
            """;
        var result = PsnWishlistService.ParseWishlistFromHtml(html);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseWishlistFromHtml_ExtractsItems_WithConceptId()
    {
        // Simulates an Apollo cache where wishlistItems hold entries by conceptId
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {
              "props": {
                "pageProps": {
                  "wishlistItems": [
                    { "displayName": "God of War Ragnarök", "conceptId": "10004409" },
                    { "displayName": "Marvel's Spider-Man 2", "conceptId": "10007040" }
                  ]
                }
              }
            }
            </script>
            </head><body></body></html>
            """;
        var result = PsnWishlistService.ParseWishlistFromHtml(html);

        Assert.Equal(2, result.Count);
        Assert.Equal("God of War Ragnarök", result[0].Name);
        Assert.Equal("10004409", result[0].ConceptId);
        Assert.Null(result[0].ProductId);

        Assert.Equal("Marvel's Spider-Man 2", result[1].Name);
        Assert.Equal("10007040", result[1].ConceptId);
    }

    [Fact]
    public void ParseWishlistFromHtml_ExtractsItems_WithProductId()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {
              "props": {
                "pageProps": {
                  "items": [
                    { "displayName": "Horizon Forbidden West", "productId": "UP9000-PPSA01435_00-HFW0000000000001" }
                  ]
                }
              }
            }
            </script>
            </head><body></body></html>
            """;
        var result = PsnWishlistService.ParseWishlistFromHtml(html);

        Assert.Single(result);
        Assert.Equal("Horizon Forbidden West", result[0].Name);
        Assert.Equal("UP9000-PPSA01435_00-HFW0000000000001", result[0].ProductId);
        Assert.Null(result[0].ConceptId);
    }

    [Fact]
    public void ParseWishlistFromHtml_DeduplicatesItems()
    {
        // Same conceptId appearing twice in the response
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {
              "props": {
                "pageProps": {
                  "wishlistItems": [
                    { "displayName": "Game A", "conceptId": "11111111" },
                    { "displayName": "Game A duplicate", "conceptId": "11111111" }
                  ]
                }
              }
            }
            </script>
            </head><body></body></html>
            """;
        var result = PsnWishlistService.ParseWishlistFromHtml(html);
        Assert.Single(result);
        Assert.Equal("Game A", result[0].Name);  // first occurrence wins
    }

    [Fact]
    public void ParseWishlistFromHtml_ReturnsEmpty_WhenNextDataIsInvalidJson()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            { this is not valid json
            </script>
            </head><body></body></html>
            """;
        var result = PsnWishlistService.ParseWishlistFromHtml(html);
        Assert.Empty(result);
    }

    // ─── ExtractWishlistItems ─────────────────────────────────────────────────

    [Fact]
    public void ExtractWishlistItems_FindsItemsInNestedObject()
    {
        var json = JsonDocument.Parse("""
            {
              "apollo": {
                "cache": {
                  "Concept:10004409": {
                    "displayName": "God of War Ragnarök",
                    "conceptId": "10004409"
                  }
                }
              }
            }
            """);

        var result = PsnWishlistService.ExtractWishlistItems(json.RootElement);
        Assert.Single(result);
        Assert.Equal("10004409", result[0].ConceptId);
        Assert.Equal("God of War Ragnarök", result[0].Name);
    }

    [Fact]
    public void ExtractWishlistItems_IgnoresObjectsWithoutId()
    {
        var json = JsonDocument.Parse("""
            {
              "items": [
                { "displayName": "No ID here" },
                { "displayName": "Has concept ID", "conceptId": "99887766" }
              ]
            }
            """);

        var result = PsnWishlistService.ExtractWishlistItems(json.RootElement);
        Assert.Single(result);
        Assert.Equal("99887766", result[0].ConceptId);
    }

    [Fact]
    public void ExtractWishlistItems_IgnoresObjectsWithoutName()
    {
        var json = JsonDocument.Parse("""
            {
              "items": [
                { "conceptId": "99887766" },
                { "displayName": "Proper Game", "conceptId": "12345678" }
              ]
            }
            """);

        var result = PsnWishlistService.ExtractWishlistItems(json.RootElement);
        Assert.Single(result);
        Assert.Equal("12345678", result[0].ConceptId);
    }

    [Fact]
    public void ExtractWishlistItems_IgnoresNonNumericConceptId()
    {
        // A non-numeric "conceptId" should be rejected (it's not a real concept ID)
        var json = JsonDocument.Parse("""
            {
              "items": [
                { "displayName": "Fake", "conceptId": "not-a-number" }
              ]
            }
            """);

        var result = PsnWishlistService.ExtractWishlistItems(json.RootElement);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseWishlistFromHtml_ReturnsEmpty_WhenHtmlIsEmpty()
    {
        var result = PsnWishlistService.ParseWishlistFromHtml(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseWishlistFromHtml_UsesNameFallback_WhenDisplayNameMissing()
    {
        const string html = """
            <html><head>
            <script id="__NEXT_DATA__" type="application/json">
            {
              "props": {
                "pageProps": {
                  "wishlistItems": [
                    { "name": "Fallback Name Game", "conceptId": "55555555" }
                  ]
                }
              }
            }
            </script>
            </head><body></body></html>
            """;
        var result = PsnWishlistService.ParseWishlistFromHtml(html);

        Assert.Single(result);
        Assert.Equal("Fallback Name Game", result[0].Name);
        Assert.Equal("55555555", result[0].ConceptId);
    }

    [Fact]
    public void ExtractWishlistItems_KeepsEntriesRegardlessOfEditorialName()
    {
        var json = JsonDocument.Parse("""
            {
              "items": [
                { "displayName": "[PROMO] Spring Sale 26 - Web - Header",                           "conceptId": "100001" },
                { "displayName": "See More",                                                         "conceptId": "100002" },
                { "displayName": "See All",                                                          "conceptId": "100003" },
                { "displayName": "PS Plus - Learn More - CTA",                                      "conceptId": "100004" },
                { "displayName": "Pre-orders - Strand",                                             "conceptId": "100005" },
                { "displayName": "Top 10 Games in your Country",                                    "conceptId": "100006" },
                { "displayName": "Discover PlayStation Plus",                                       "conceptId": "100007" },
                { "displayName": "Global Web Store",                                                "conceptId": "100008" },
                { "displayName": "Enjoy hundreds of PS5, PS4 and classic PlayStation games, online multiplayer, and more.", "conceptId": "100009" },
                { "displayName": "Latest - New Games - Web - Header",                               "conceptId": "100010" },
                { "displayName": "Real Game",                                                        "conceptId": "200001" },
                { "displayName": "Gothic 1 Remake",                                                  "conceptId": "200002" }
              ]
            }
            """);

        var result = PsnWishlistService.ExtractWishlistItems(json.RootElement);

        Assert.Equal(12, result.Count);
        Assert.Contains(result, r => r.Name == "[PROMO] Spring Sale 26 - Web - Header");
        Assert.Contains(result, r => r.Name == "See More");
        Assert.Contains(result, r => r.Name == "PS Plus - Learn More - CTA");
        Assert.Contains(result, r => r.Name == "Real Game");
        Assert.Contains(result, r => r.Name == "Gothic 1 Remake");
    }
}
