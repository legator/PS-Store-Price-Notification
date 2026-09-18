using System.Net;
using System.Text.RegularExpressions;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

public enum FetchStatus
{
    Success,
    NotAvailable,
    ErrorPage,
    RateLimited,
    HttpError,
    ParseFailed,
    NetworkError,
    UnknownCountry
}

public sealed record FetchPriceResult(
    PriceInfo? Price,
    FetchStatus Status,
    string Message,
    HttpStatusCode? StatusCode = null,
    string? EffectiveUrl = null,
    string? Diagnosis = null,
    string? HtmlSnippet = null);

public sealed class PSStoreClient : IDisposable
{
    // ─── Locale map ──────────────────────────────────────────────────────────
    public static readonly IReadOnlyDictionary<string, string> DefaultLocales =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Americas
            { "us", "en-us" }, { "ca", "en-ca" }, { "br", "pt-br" }, { "mx", "es-mx" },
            { "ar", "es-ar" }, { "cl", "es-cl" }, { "co", "es-co" }, { "pe", "es-pe" },
            { "bo", "es-bo" }, { "ec", "es-ec" }, { "pa", "es-pa" }, { "cr", "es-cr" },
            { "gt", "es-gt" }, { "hn", "es-hn" }, { "sv", "es-sv" }, { "py", "es-py" },
            { "uy", "es-uy" }, { "ni", "es-ni" },
            // Europe
            { "gb", "en-gb" }, { "de", "de-de" }, { "fr", "fr-fr" }, { "es", "es-es" },
            { "it", "it-it" }, { "nl", "nl-nl" }, { "pt", "pt-pt" }, { "be", "nl-be" },
            { "at", "de-at" }, { "ch", "de-ch" }, { "dk", "da-dk" }, { "fi", "fi-fi" },
            { "no", "no-no" }, { "se", "sv-se" }, { "pl", "pl-pl" }, { "cz", "cs-cz" },
            { "hu", "hu-hu" }, { "ro", "ro-ro" }, { "sk", "sk-sk" }, { "si", "sl-si" },
            { "hr", "hr-hr" }, { "bg", "bg-bg" }, { "gr", "el-gr" }, { "cy", "en-cy" },
            { "mt", "en-mt" }, { "lu", "fr-lu" }, { "ie", "en-ie" }, { "tr", "tr-tr" },
            { "ru", "ru-ru" }, { "ua", "ru-ua" },
            // Middle East & Africa
            { "sa", "en-sa" }, { "ae", "en-ae" }, { "kw", "en-kw" }, { "qa", "en-qa" },
            { "bh", "en-bh" }, { "jo", "en-jo" }, { "om", "en-om" }, { "in", "en-in" },
            { "za", "en-za" }, { "il", "en-il" },
            // Asia Pacific
            { "jp", "ja-jp" }, { "hk", "en-hk" }, { "sg", "en-sg" }, { "kr", "ko-kr" },
            { "tw", "zh-hant-tw" }, { "th", "th-th" }, { "my", "en-my" }, { "id", "en-id" },
            { "ph", "en-ph" },
            // Oceania
            { "au", "en-au" }, { "nz", "en-nz" },
        };

    private const string BaseUrl = "https://store.playstation.com";

    private readonly HttpClient _http;
    private readonly PsnAuthService? _auth;
    private readonly bool _ownsHttpClient;

    public IReadOnlyDictionary<string, string> Locales { get; }

    public PSStoreClient(IReadOnlyDictionary<string, string>? locales = null, PsnAuthService? auth = null)
    {
        Locales = locales ?? DefaultLocales;
        _auth = auth;
        _http = PlayStationHttp.BrowserClient;
        _ownsHttpClient = false;
    }

    internal PSStoreClient(HttpMessageHandler handler, IReadOnlyDictionary<string, string>? locales = null, PsnAuthService? auth = null)
    {
        Locales = locales ?? DefaultLocales;
        _auth   = auth;
        _ownsHttpClient = true;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Add("DNT", "1");
    }

    public string GetStoreUrl(string gameId, string idType, string country)
    {
        var locale = Locales.TryGetValue(country, out var l) ? l : "en-us";
        return $"{BaseUrl}/{locale}/{idType}/{gameId}";
    }

    public async Task<FetchPriceResult> GetPriceDetailedAsync(
        string gameId, string idType, string country,
        CancellationToken ct = default)
    {
        if (!Locales.TryGetValue(country, out var locale))
        {
            var msg = $"Unknown country code: {country}";
            Logger.Debug(msg);
            return new FetchPriceResult(null, FetchStatus.UnknownCountry, msg);
        }

        var url = $"{BaseUrl}/{locale}/{idType}/{gameId}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var token = _auth?.AccessToken;
            if (token != null)
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct);

            var finalUri = response.RequestMessage?.RequestUri;
            var effectiveUrl = finalUri?.ToString() ?? url;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new FetchPriceResult(
                    new PriceInfo(null, null, null, null, false, IsAvailable: false),
                    FetchStatus.NotAvailable,
                    $"HTTP 404 Not Found at {effectiveUrl}",
                    HttpStatusCode.NotFound,
                    effectiveUrl);
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                var msg = $"Rate limited by PS Store (HTTP 429) for country {country}";
                Logger.Debug(msg);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return new FetchPriceResult(null, FetchStatus.RateLimited, msg, (HttpStatusCode)429, effectiveUrl);
            }

            if (!response.IsSuccessStatusCode)
            {
                var msg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {effectiveUrl}";
                Logger.Debug(msg);
                return new FetchPriceResult(null, FetchStatus.HttpError, msg, response.StatusCode, effectiveUrl);
            }

            // Check if redirected to an error page (e.g. regional product ID not in this country's catalog)
            if (finalUri != null && (finalUri.AbsolutePath.EndsWith("/error") || finalUri.AbsolutePath.Contains("/error/")))
            {
                var query = System.Web.HttpUtility.ParseQueryString(finalUri.Query);
                var statusCode = query["widgetStatusCode"];
                var errorType = query["widgetErrorType"];
                var hint = GetRegionalMismatchHint(gameId, country);
                var hintSuffix = hint != null ? $" — {hint}" : string.Empty;
                var msg = $"Redirected to error page [code: {statusCode ?? "204"}, error: {errorType ?? "not in catalog"}]{hintSuffix}";
                Logger.Debug($"[{country.ToUpperInvariant()}] {msg}");
                return new FetchPriceResult(
                    new PriceInfo(null, null, null, null, false, IsAvailable: false),
                    FetchStatus.ErrorPage,
                    msg,
                    response.StatusCode,
                    effectiveUrl);
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var price = PSStoreParser.ParsePriceWithDiagnosis(html, out var diagnosis);

            if (price == null)
            {
                var snippet = ExtractSnippet(html);
                var msg = $"Could not parse price from {effectiveUrl}: {diagnosis}";
                Logger.Debug($"[{country.ToUpperInvariant()}] {msg}");
                Logger.Debug($"[{country.ToUpperInvariant()}] Response snippet: {snippet}");

                return new FetchPriceResult(
                    null,
                    FetchStatus.ParseFailed,
                    msg,
                    response.StatusCode,
                    effectiveUrl,
                    diagnosis,
                    snippet);
            }

            return new FetchPriceResult(
                price,
                FetchStatus.Success,
                "Price retrieved successfully",
                response.StatusCode,
                effectiveUrl);
        }
        catch (TaskCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = $"HTTP request failed for {url}: {ex.Message}";
            Logger.Warn(msg);
            return new FetchPriceResult(null, FetchStatus.NetworkError, msg, null, url);
        }
    }

    public async Task<PriceInfo?> GetPriceAsync(
        string gameId, string idType, string country,
        CancellationToken ct = default)
    {
        var result = await GetPriceDetailedAsync(gameId, idType, country, ct);
        return result.Price;
    }

    private static string ExtractSnippet(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "<empty>";

        var jsonLdMatch = Regex.Match(html, @"<script[^>]*type=[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (jsonLdMatch.Success)
        {
            var text = jsonLdMatch.Groups[1].Value.Trim();
            return text.Length > 300 ? text[..300] + "..." : text;
        }

        var metaDesc = Regex.Match(html, @"<meta[^>]*name=[""']description[""'][^>]*content=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (metaDesc.Success && !string.IsNullOrWhiteSpace(metaDesc.Groups[1].Value))
        {
            return $"Meta description: {metaDesc.Groups[1].Value}";
        }

        var cleaned = Regex.Replace(html, @"\s+", " ").Trim();
        return cleaned.Length > 300 ? cleaned[..300] + "..." : cleaned;
    }

    private static string? GetRegionalMismatchHint(string gameId, string country)
    {
        if (string.IsNullOrEmpty(gameId) || gameId.Length < 2) return null;

        var prefix = gameId[..2].ToUpperInvariant();
        var c = country.ToLowerInvariant();

        var isEuropePrefix = prefix == "EP";
        var isAmericasPrefix = prefix == "UP";
        var isJapanPrefix = prefix == "JP";
        var isAsiaPrefix = prefix is "HP" or "AP";

        if (!isEuropePrefix && !isAmericasPrefix && !isJapanPrefix && !isAsiaPrefix)
            return null;

        var isAmericasCountry = c is "us" or "ca" or "mx" or "br" or "ar" or "cl" or "co" or "pe";
        var isEuropeCountry = c is "gb" or "uk" or "ua" or "de" or "fr" or "es" or "it" or "nl" or "pl" or "pt" or "se" or "no" or "fi" or "dk" or "au" or "nz" or "za" or "in" or "tr" or "cz" or "gr" or "ro" or "hu" or "sk" or "hr" or "bg";
        var isJapanCountry = c is "jp";
        var isAsiaCountry = c is "hk" or "tw" or "sg" or "kr" or "my" or "th" or "id";

        if (isEuropePrefix && isAmericasCountry)
            return $"product ID '{gameId}' has European region prefix '{prefix}' which does not exist in the {country.ToUpperInvariant()} catalog; consider using a universal concept ID or {country.ToUpperInvariant()} product ID";
        if (isEuropePrefix && isJapanCountry)
            return $"product ID '{gameId}' has European region prefix '{prefix}' which does not exist in the Japan catalog; consider using a universal concept ID or JP product ID";
        if (isEuropePrefix && isAsiaCountry)
            return $"product ID '{gameId}' has European region prefix '{prefix}' which does not exist in the Asian catalog; consider using a universal concept ID or Asian product ID";

        if (isAmericasPrefix && isEuropeCountry)
            return $"product ID '{gameId}' has Americas region prefix '{prefix}' which does not exist in the {country.ToUpperInvariant()} catalog; consider using a universal concept ID or European product ID";
        if (isAmericasPrefix && isJapanCountry)
            return $"product ID '{gameId}' has Americas region prefix '{prefix}' which does not exist in the Japan catalog; consider using a universal concept ID or JP product ID";
        if (isAmericasPrefix && isAsiaCountry)
            return $"product ID '{gameId}' has Americas region prefix '{prefix}' which does not exist in the Asian catalog; consider using a universal concept ID or Asian product ID";

        if (isJapanPrefix && !isJapanCountry)
            return $"product ID '{gameId}' has Japan region prefix '{prefix}' which does not exist in the {country.ToUpperInvariant()} catalog; consider using a universal concept ID or {country.ToUpperInvariant()} product ID";

        if (isAsiaPrefix && !isAsiaCountry)
            return $"product ID '{gameId}' has Asian region prefix '{prefix}' which does not exist in the {country.ToUpperInvariant()} catalog; consider using a universal concept ID or {country.ToUpperInvariant()} product ID";

        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

