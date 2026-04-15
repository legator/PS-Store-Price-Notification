using System.Text.Json;
using HtmlAgilityPack;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

public sealed class PsnWishlistService
{
    private const string WishlistUrl = "https://library.playstation.com/wishlist";

    private readonly string _npsso;
    private readonly HttpClient _http = PlayStationHttp.BrowserClient;

    public PsnWishlistService(string npsso)
    {
        if (string.IsNullOrWhiteSpace(npsso))
            throw new ArgumentException("NPSSO must not be empty.", nameof(npsso));

        _npsso = npsso;
    }

    public async Task<List<GameEntry>> GetWishlistAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WishlistUrl);
            request.Headers.Add("Cookie", $"npsso={_npsso}");

            Logger.Debug($"Fetching PSN wishlist from {WishlistUrl}");
            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Debug($"PSN wishlist page returned {(int)response.StatusCode} — no wishlist items.");
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var items = ParseWishlistFromHtml(html);
            Logger.Info($"PSN wishlist: {items.Count} item(s) fetched.");
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn($"PSN wishlist fetch failed: {ex.Message}");
            return [];
        }
    }

    internal static List<GameEntry> ParseWishlistFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
            if (scriptNode == null)
            {
                Logger.Debug("PSN wishlist: no __NEXT_DATA__ script found in page.");
                return [];
            }

            using var jsonDoc = JsonDocument.Parse(scriptNode.InnerText);
            return ExtractWishlistItems(jsonDoc.RootElement);
        }
        catch (Exception ex)
        {
            Logger.Debug($"PSN wishlist HTML parse failed: {ex.Message}");
            return [];
        }
    }

    internal static List<GameEntry> ExtractWishlistItems(JsonElement root)
    {
        var items = new List<GameEntry>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectItems(root, items, seen, depth: 0);
        return items;
    }

    private static void CollectItems(
        JsonElement element,
        List<GameEntry> result,
        HashSet<string> seen,
        int depth)
    {
        if (depth > 30) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // First check wishlist-specific key names before general recursion
                if (element.TryGetProperty("wishlistItems", out var wishlistItems))
                {
                    CollectItems(wishlistItems, result, seen, depth + 1);
                    return;
                }
                if (element.TryGetProperty("items", out var items))
                {
                    CollectItems(items, result, seen, depth + 1);
                }

                if (TryExtractEntry(element, out var entry) && entry != null)
                {
                    var key = entry.ConceptId ?? entry.ProductId!;
                    if (seen.Add(key))
                        result.Add(entry);
                    return;
                }

                foreach (var prop in element.EnumerateObject())
                    CollectItems(prop.Value, result, seen, depth + 1);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectItems(item, result, seen, depth + 1);
                break;
        }
    }

    private static bool TryExtractEntry(JsonElement el, out GameEntry? entry)
    {
        entry = null;

        var name = GetStr(el, "displayName")
                ?? GetStr(el, "name")
                ?? GetStr(el, "titleName");

        if (string.IsNullOrWhiteSpace(name)) return false;

        var conceptId = GetStr(el, "conceptId");
        var productId = GetStr(el, "productId");

        // Some PS5 product IDs come as "id" in certain contexts
        if (productId == null)
        {
            var id = GetStr(el, "id");
            // Concept IDs are all-numeric; product IDs have format "UP9000-PPSA01234_00-..."
            if (id != null && id.Contains('-'))
                productId = id;
        }

        if (conceptId == null && productId == null) return false;

        // Sanity check: conceptId should be numeric-only
        if (conceptId != null && !IsNumericOnly(conceptId)) conceptId = null;
        // Sanity check: productId should be reasonably long and contain alphanumeric chars
        if (productId != null && productId.Length < 6) productId = null;

        if (conceptId == null && productId == null) return false;

        entry = new GameEntry
        {
            Name      = name,
            ConceptId = conceptId,
            ProductId = conceptId == null ? productId : null,
        };
        return true;
    }

    private static bool IsNumericOnly(string s) => !string.IsNullOrEmpty(s) && s.All(char.IsDigit);

    private static string? GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
