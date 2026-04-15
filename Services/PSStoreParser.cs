using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

internal static class PSStoreParser
{
    internal static PriceInfo? ParsePrice(string html) =>
        ParseApolloCache(html)
        ?? ParseJsonLd(html)
        ?? ParseRegex(html);

    internal static PriceInfo? ParseApolloCache(string html)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
            if (scriptNode == null) return null;

            using var jsonDoc = JsonDocument.Parse(scriptNode.InnerText);
            return SearchByTypename(jsonDoc.RootElement, 0)
                ?? FindPriceRecursive(jsonDoc.RootElement, 0);
        }
        catch (Exception ex)
        {
            Logger.Debug($"__NEXT_DATA__ parse failed: {ex.Message}");
            return null;
        }
    }

    internal static PriceInfo? SearchByTypename(JsonElement element, int depth)
    {
        if (depth > 25) return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("__typename", out var tn) &&
                tn.GetString()?.Contains("price", StringComparison.OrdinalIgnoreCase) == true)
                return ExtractFromPriceElement(element);

            foreach (var prop in element.EnumerateObject())
            {
                var r = SearchByTypename(prop.Value, depth + 1);
                if (r != null) return r;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (i++ > 10) break;
                var r = SearchByTypename(item, depth + 1);
                if (r != null) return r;
            }
        }
        return null;
    }

    internal static PriceInfo? FindPriceRecursive(JsonElement element, int depth)
    {
        if (depth > 25) return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikePrice(element))
                return ExtractFromPriceElement(element);

            foreach (var key in new[] { "price", "pricing", "defaultProduct", "products", "skus" })
            {
                if (element.TryGetProperty(key, out var child))
                {
                    var r = FindPriceRecursive(child, depth + 1);
                    if (r != null) return r;
                }
            }
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var r = FindPriceRecursive(prop.Value, depth + 1);
                    if (r != null) return r;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (i++ > 5) break;
                var r = FindPriceRecursive(item, depth + 1);
                if (r != null) return r;
            }
        }
        return null;
    }

    internal static bool LooksLikePrice(JsonElement element) =>
        element.TryGetProperty("basePrice", out _) ||
        element.TryGetProperty("discountedPrice", out _) ||
        element.TryGetProperty("basePriceValue", out _) ||
        element.TryGetProperty("discountedValue", out _);

    internal static PriceInfo ExtractFromPriceElement(JsonElement e)
    {
        string? basePrice = GetStr(e, "basePrice");
        if (basePrice == null && e.TryGetProperty("basePriceValue", out var bpv))
            basePrice = FormatCents(bpv);

        string? discountedPrice = GetStr(e, "discountedPrice");
        if (discountedPrice == null && e.TryGetProperty("discountedValue", out var dpv))
            discountedPrice = FormatCents(dpv);

        string? currency = GetStr(e, "currencyCode") ?? GetStr(e, "currency");
        if (currency == null)
        {
            foreach (var nestedKey in new[] { "basePriceMoney", "priceMoney" })
            {
                if (e.TryGetProperty(nestedKey, out var nested) &&
                    nested.ValueKind == JsonValueKind.Object)
                {
                    currency = GetStr(nested, "currencyCode");
                    if (basePrice == null) basePrice = GetStr(nested, "amount");
                    break;
                }
            }
        }

        int? discountPercent = null;
        foreach (var key in new[] { "discountText", "discountPercent", "discount" })
        {
            if (!e.TryGetProperty(key, out var dv)) continue;
            if (dv.ValueKind == JsonValueKind.String)
            {
                var m = Regex.Match(dv.GetString() ?? "", @"\d+");
                if (m.Success) discountPercent = int.Parse(m.Value);
            }
            else if (dv.ValueKind is JsonValueKind.Number)
            {
                discountPercent = Math.Abs(dv.GetInt32());
            }
            if (discountPercent.HasValue) break;
        }

        bool isFree = false;
        if (e.TryGetProperty("isFree", out var freeEl) && freeEl.ValueKind == JsonValueKind.True)
            isFree = true;
        else if (basePrice is "0" or "0.00" or "Free")
            isFree = true;

        return new PriceInfo(basePrice, discountedPrice, currency, discountPercent, isFree, true);
    }

    internal static PriceInfo? ParseJsonLd(string html)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var nodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (nodes == null) return null;

            foreach (var node in nodes)
            {
                try
                {
                    using var jd = JsonDocument.Parse(node.InnerText);
                    var root = jd.RootElement;
                    if (!root.TryGetProperty("@type", out var type)) continue;
                    var typeName = type.GetString();
                    if (typeName is not ("Product" or "VideoGame" or "SoftwareApplication"))
                        continue;

                    if (!root.TryGetProperty("offers", out var offers) ||
                        offers.ValueKind != JsonValueKind.Object) continue;

                    if (!offers.TryGetProperty("price", out var price)) continue;

                    var priceStr = price.ValueKind == JsonValueKind.String
                        ? price.GetString()
                        : price.GetDouble().ToString("F2");

                    var currency = GetStr(offers, "priceCurrency");
                    return new PriceInfo(priceStr, null, currency, null, priceStr is "0" or "0.00", true);
                }
                catch { /* skip malformed */ }
            }
        }
        catch (Exception ex) { Logger.Debug($"JSON-LD parse failed: {ex.Message}"); }
        return null;
    }

    internal static PriceInfo? ParseRegex(string html)
    {
        string? basePrice = null;
        foreach (var pat in new[] { @"""basePrice""\s*:\s*""([^""]+)""", @"""price""\s*:\s*""([^""]+)""" })
        {
            var m = Regex.Match(html, pat);
            if (m.Success) { basePrice = m.Groups[1].Value; break; }
        }

        if (basePrice == null) return null;

        var dm = Regex.Match(html, @"""discountedPrice""\s*:\s*""([^""]+)""");
        var cm = Regex.Match(html, @"""currencyCode""\s*:\s*""([A-Z]{3})""");

        return new PriceInfo(
            basePrice,
            dm.Success ? dm.Groups[1].Value : null,
            cm.Success ? cm.Groups[1].Value : null,
            null,
            basePrice is "0" or "0.00" or "Free",
            true);
    }

    internal static string? GetStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static string? FormatCents(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? $"{e.GetDouble() / 100:F2}" : null;
}
