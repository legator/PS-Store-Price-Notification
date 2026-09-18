using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

internal static class PSStoreParser
{
    internal static PriceInfo? ParsePrice(string html) =>
        ParsePriceWithDiagnosis(html, out _);

    internal static PriceInfo? ParsePriceWithDiagnosis(string html, out string diagnosis)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            diagnosis = "Response body is empty or whitespace.";
            return null;
        }

        // Check if page contains PlayStation Store error page indicators
        if (html.Contains("/error?") ||
            html.Contains("widgetErrorType=") ||
            html.Contains("/pages/%5Blocale%5D/error") ||
            html.Contains("data-qa=\"error-page\""))
        {
            diagnosis = "PlayStation Store returned an error page (product/concept likely unavailable or region-restricted in this country).";
            return null;
        }

        // Check for WAF / Bot challenge
        if (html.Contains("Access Denied") && html.Contains("Reference #"))
        {
            diagnosis = "Request was blocked by PlayStation Store CDN bot-protection (Akamai Access Denied).";
            return null;
        }

        var apolloPrice = ParseApolloCache(html);
        if (apolloPrice != null && IsValidPurchasePrice(apolloPrice))
        {
            diagnosis = "Price successfully parsed from Apollo/Next.js state cache.";
            return apolloPrice;
        }

        var jsonLdPrice = ParseJsonLd(html);
        if (jsonLdPrice != null && IsValidPurchasePrice(jsonLdPrice))
        {
            diagnosis = "Price successfully parsed from schema.org JSON-LD.";
            return jsonLdPrice;
        }

        var regexPrice = ParseRegex(html);
        if (regexPrice != null && IsValidPurchasePrice(regexPrice))
        {
            diagnosis = "Price successfully parsed from HTML regex patterns.";
            return regexPrice;
        }

        var hasJsonLd = html.Contains("application/ld+json");
        var hasNextData = html.Contains("__NEXT_DATA__");
        var hasEnvScript = html.Contains("env:");
        var hasPriceKeyword = html.Contains("\"price\"") || html.Contains("\"basePrice\"");
        var titleMatch = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase);
        var pageTitle = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "No <title>";

        diagnosis = $"Unable to extract price from HTML ({html.Length} chars, title: '{pageTitle}'). " +
                    $"Metadata found: [JSON-LD: {hasJsonLd}, __NEXT_DATA__: {hasNextData}, env-scripts: {hasEnvScript}, price-keyword: {hasPriceKeyword}].";

        return null;
    }

    internal static PriceInfo? ParseApolloCache(string html)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Legacy Next.js __NEXT_DATA__
            var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
            if (scriptNode != null && !string.IsNullOrWhiteSpace(scriptNode.InnerText))
            {
                using var jsonDoc = JsonDocument.Parse(scriptNode.InnerText);
                var price = SearchByTypename(jsonDoc.RootElement, 0)
                    ?? FindPriceRecursive(jsonDoc.RootElement, 0);
                if (price != null && IsValidPurchasePrice(price)) return price;
            }

            // Modern Sony micro-frontend state scripts: <script id="env:..." type="application/json">
            var envScripts = doc.DocumentNode.SelectNodes("//script[starts-with(@id, 'env:') and @type='application/json']");
            if (envScripts != null)
            {
                foreach (var envNode in envScripts)
                {
                    if (string.IsNullOrWhiteSpace(envNode.InnerText)) continue;
                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(envNode.InnerText);
                        var price = SearchByTypename(jsonDoc.RootElement, 0)
                            ?? FindPriceRecursive(jsonDoc.RootElement, 0);
                        if (price != null && IsValidPurchasePrice(price)) return price;
                    }
                    catch { /* skip malformed env script */ }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"State script parse failed: {ex.Message}");
            return null;
        }
    }

    internal static PriceInfo? SearchByTypename(JsonElement element, int depth)
    {
        if (depth > 25) return null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("__typename", out var tn) &&
                tn.GetString()?.Contains("price", StringComparison.OrdinalIgnoreCase) == true &&
                !IsTrialPrice(element))
            {
                var p = ExtractFromPriceElement(element);
                if (IsValidPurchasePrice(p)) return p;
            }

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
            if (LooksLikePrice(element) && !IsTrialPrice(element))
            {
                var p = ExtractFromPriceElement(element);
                if (IsValidPurchasePrice(p)) return p;
            }

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

    internal static bool IsTrialPrice(JsonElement e)
    {
        // 1. Sony explicit applicability: "UPSELL" is for subscription upsell trials
        if (e.TryGetProperty("applicability", out var app) &&
            app.GetString()?.Equals("UPSELL", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // 2. TIER_30 is PlayStation Plus Premium/Deluxe (perk: 2-hour game trials)
        if (e.TryGetProperty("tierLabel", out var tier) &&
            tier.GetString()?.StartsWith("TIER_30", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // 3. Subscription-tied offer with zero or missing purchase value
        var isTied = e.TryGetProperty("isTiedToSubscription", out var tied) && tied.ValueKind == JsonValueKind.True;
        var bpvZero = (e.TryGetProperty("basePriceValue", out var bpv) && bpv.GetInt64() == 0) ||
                      (e.TryGetProperty("basePrice", out var bp) && bp.GetString() is "0" or "0.00" or null);
        if (isTied && bpvZero)
            return true;

        // 4. Trial keywords in basePrice or discountedPrice
        var basePrice = GetStr(e, "basePrice") ?? "";
        if (ContainsTrialKeyword(basePrice))
            return true;

        var discountedPrice = GetStr(e, "discountedPrice") ?? "";
        if (ContainsTrialKeyword(discountedPrice))
            return true;

        // 5. displayUpsellText indicators
        if (e.TryGetProperty("displayUpsellText", out var upsell) && upsell.ValueKind == JsonValueKind.String)
        {
            var text = upsell.GetString() ?? "";
            if (ContainsTrialKeyword(text) ||
                text.Contains("hour", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("годин", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("stunde", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("heure", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool ContainsTrialKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("trial", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("пробн", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("essai", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("prueba", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("testversion", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("demo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("демо", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRecognizedFreeString(string s)
    {
        var trimmed = s.Trim();
        if (trimmed is "0" or "0.00" or "0,00") return true;
        if (trimmed.Equals("Free", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("Безкоштовно", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("Gratis", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("Gratuit", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("Kostenlos", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("無料", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("무료", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsValidPurchasePrice(PriceInfo? price)
    {
        if (price == null) return false;
        if (price.BasePrice != null && ContainsTrialKeyword(price.BasePrice)) return false;
        if (price.DiscountedPrice != null && ContainsTrialKeyword(price.DiscountedPrice)) return false;
        if (price.IsFree && price.BasePrice != null && !IsRecognizedFreeString(price.BasePrice)) return false;
        return true;
    }

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
        if (!IsTrialPrice(e))
        {
            if (e.TryGetProperty("isFree", out var freeEl) && freeEl.ValueKind == JsonValueKind.True)
            {
                if (!e.TryGetProperty("isTiedToSubscription", out var tied) || tied.ValueKind != JsonValueKind.True)
                    isFree = true;
            }
            else if (basePrice is "0" or "0.00" || (basePrice != null && IsRecognizedFreeString(basePrice)))
            {
                isFree = true;
            }
        }

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

                    if (priceStr == null || ContainsTrialKeyword(priceStr)) continue;

                    var currency = GetStr(offers, "priceCurrency");
                    var isFree = IsRecognizedFreeString(priceStr) || priceStr is "0" or "0.00";
                    return new PriceInfo(priceStr, null, currency, null, isFree, true);
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
            var matches = Regex.Matches(html, pat);
            foreach (Match m in matches)
            {
                var val = m.Groups[1].Value.Trim();
                if (!ContainsTrialKeyword(val) && (val.Any(char.IsDigit) || IsRecognizedFreeString(val)))
                {
                    basePrice = val;
                    break;
                }
            }
            if (basePrice != null) break;
        }

        if (basePrice == null) return null;

        var dm = Regex.Match(html, @"""discountedPrice""\s*:\s*""([^""]+)""");
        var discounted = dm.Success && !ContainsTrialKeyword(dm.Groups[1].Value) ? dm.Groups[1].Value : null;
        var cm = Regex.Match(html, @"""currencyCode""\s*:\s*""([A-Z]{3})""");

        var isFree = IsRecognizedFreeString(basePrice) || basePrice is "0" or "0.00";

        return new PriceInfo(
            basePrice,
            discounted,
            cm.Success ? cm.Groups[1].Value : null,
            null,
            isFree,
            true);
    }

    internal static string? GetStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static string? FormatCents(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? $"{e.GetDouble() / 100:F2}" : null;
}
