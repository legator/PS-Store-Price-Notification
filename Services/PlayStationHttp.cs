using System.Net;

namespace PSPriceNotification.Services;

internal static class PlayStationHttp
{
    private static readonly Lazy<HttpClient> BrowserClientInstance = new(CreateBrowserClient);

    public static HttpClient BrowserClient => BrowserClientInstance.Value;

    private static HttpClient CreateBrowserClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("DNT", "1");

        return client;
    }
}