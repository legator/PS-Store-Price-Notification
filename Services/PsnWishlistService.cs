using System.Net;
using System.Text.Json;
using HtmlAgilityPack;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

/// <summary>
/// Fetches the user's PS Store wishlist using their NPSSO session cookie.
///
/// How to get your NPSSO token (same as for PsnAuthService):
///   1. Log in at https://www.playstation.com in your browser.
///   2. Visit https://ca.account.sony.com/api/v1/ssocookie in the same browser.
///   3. Copy the 64-character npsso value and paste it into config.yaml under account.npsso.
///
/// The wishlist items are extracted from the __NEXT_DATA__ Apollo cache in the
/// PS Store wishlist page (store.playstation.com/{locale}/wishlist).
///
/// Silently returns an empty list on any failure so the app keeps working via
/// the local favorites.json.
/// </summary>
public sealed class PsnWishlistService : IDisposable
{
    private const string StoreBaseUrl = "https://store.playstation.com";

    private readonly string _npsso;
    private readonly HttpClient _http;

    public PsnWishlistService(string npsso)
    {
        if (string.IsNullOrWhiteSpace(npsso))
            throw new ArgumentException("NPSSO must not be empty.", nameof(npsso));

        _npsso = npsso;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    // ─── Public API ──────────────────────────────────────────────────────────
    public async Task<List<GameEntry>> GetWishlistAsync(
        string locale = "en-us",
        CancellationToken ct = default)
    {
        try
        {
            var url = $"{StoreBaseUrl}/{locale}/wishlist";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            // NPSSO is the PS Store session SSO cookie — use it for web page authentication
            request.Headers.Add("Cookie", $"npsso={_npsso}");

            Logger.Debug($"Fetching PSN wishlist from {url}");
            var response = await _http.SendAsync(request, ct);

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

    // ─── HTML + __NEXT_DATA__ parsing ─────────────────────────────────────────
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
                    // also continue scanning siblings for other wishlist entries
                }

                if (TryExtractEntry(element, out var entry) && entry != null)
                {
                    var key = entry.ConceptId ?? entry.ProductId!;
                    if (seen.Add(key))
                        result.Add(entry);
                    return; // found a leaf entry — don't recurse further into it
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

        // Skip editorial/promotional entries injected by the PS Store (e.g. "[PROMO] Spring Sale 26 - Web - Header")
        if (name.StartsWith('[') && name.IndexOf(']') is > 1 and < 12) return false;

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

    public void Dispose() => _http.Dispose();
}
