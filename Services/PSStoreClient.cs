using System.Net;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

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

    public IReadOnlyDictionary<string, string> Locales { get; }

    public PSStoreClient(IReadOnlyDictionary<string, string>? locales = null, PsnAuthService? auth = null)
        : this(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }, locales, auth)
    { }

    internal PSStoreClient(HttpMessageHandler handler, IReadOnlyDictionary<string, string>? locales = null, PsnAuthService? auth = null)
    {
        Locales = locales ?? DefaultLocales;
        _auth   = auth;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Add("DNT", "1");
    }

    // ─── Public API ──────────────────────────────────────────────────────────
    public string GetStoreUrl(string gameId, string idType, string country)
    {
        var locale = Locales.TryGetValue(country, out var l) ? l : "en-us";
        return $"{BaseUrl}/{locale}/{idType}/{gameId}";
    }

    public async Task<PriceInfo?> GetPriceAsync(
        string gameId, string idType, string country,
        CancellationToken ct = default)
    {
        if (!Locales.TryGetValue(country, out var locale))
        {
            Logger.Warn($"Unknown country code: {country}");
            return null;
        }

        var url = $"{BaseUrl}/{locale}/{idType}/{gameId}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Attach Bearer token when authenticated — enables PS Plus prices
            var token = _auth?.AccessToken;
            if (token != null)
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new PriceInfo(null, null, null, null, false, IsAvailable: false);

            if (response.StatusCode == (HttpStatusCode)429)
            {
                Logger.Warn($"Rate limited by PS Store for country {country}");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"HTTP {(int)response.StatusCode} for {url}");
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var price = PSStoreParser.ParsePrice(html);

            if (price == null)
            {
                var snippet = html.Length > 200 ? html[..200].Replace('\n', ' ') : html;
                Logger.Debug($"Could not parse price from page ({url})");
                Logger.Debug($"Response snippet: {snippet}");
            }

            return price;
        }
        catch (TaskCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Warn($"HTTP request failed for {url}: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

