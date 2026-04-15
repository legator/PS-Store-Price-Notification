using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSPriceNotification.Services;

public sealed class PsnAuthService : IDisposable
{
    private const string AuthBaseUrl = "https://ca.account.sony.com/api/authz/v3/oauth";
    private const string ClientId    = "09515159-7237-4370-9b40-3806e67c0891";
    private const string BasicAuth   = "Basic MDk1MTUxNTktNzIzNy00MzcwLTliNDAtMzgwNmU2N2MwODkxOnVjUGprYTV0bnRCMktxc1A=";
    private const string RedirectUri = "com.scee.psxandroid.scecompcall://redirect";
    private const string Scope       = "psn:mobile.v2.core psn:clientapp";


    private readonly string _npsso;
    private readonly HttpClient _http;
    private readonly string _tokenCachePath;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _accessTokenExpiry;
    private DateTime _refreshTokenExpiry;

    public string TokenCachePath => _tokenCachePath;

    public string? AccessToken
    {
        get
        {
            if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiry)
                return _accessToken;
            return null;
        }
    }

    public PsnAuthService(string npsso, string tokenCachePath)
    {
        if (string.IsNullOrWhiteSpace(npsso))
            throw new ArgumentException("NPSSO token must not be empty.", nameof(npsso));

        _npsso          = npsso;
        _tokenCachePath = tokenCachePath;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect      = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    public async Task EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        if (_accessToken == null)
            LoadCache();

        if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiry)
            return;

        if (_refreshToken != null && DateTime.UtcNow < _refreshTokenExpiry)
        {
            Logger.Info("PSN access token expired — refreshing using refresh token...");
            try
            {
                await RefreshAccessTokenAsync(ct);
                SaveCache();
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Token refresh failed: {ex.Message}. Falling back to NPSSO exchange.");
            }
        }

        // Full exchange: NPSSO → code → tokens
        Logger.Info("Authenticating with PSN using NPSSO token...");
        var code = await ExchangeNpssoForAccessCodeAsync(ct);
        await ExchangeCodeForTokensAsync(code, ct);
        SaveCache();
        Logger.Info("PSN authentication successful.");
    }

    private async Task<string> ExchangeNpssoForAccessCodeAsync(CancellationToken ct)
    {
        var query = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "access_type",   "offline"  },
            { "client_id",     ClientId   },
            { "redirect_uri",  RedirectUri},
            { "response_type", "code"     },
            { "scope",         Scope      },
        });

        var url = $"{AuthBaseUrl}/authorize?{await query.ReadAsStringAsync()}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie", $"npsso={_npsso}");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        // Sony sends a 302 redirect; the code is in the Location header
        var location = resp.Headers.Location?.ToString()
            ?? throw new InvalidOperationException(
                "PSN did not return a redirect. Is your NPSSO valid? " +
                "Get a fresh one at https://ca.account.sony.com/api/v1/ssocookie");

        if (!location.Contains("?code=") && !location.Contains("&code="))
            throw new InvalidOperationException(
                $"Unexpected redirect location (no code parameter): {location}");

        var code = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["code"]
            ?? throw new InvalidOperationException("Could not extract code from redirect.");

        return code;
    }

    private async Task ExchangeCodeForTokensAsync(string code, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "code",         code        },
            { "redirect_uri", RedirectUri },
            { "grant_type",   "authorization_code" },
            { "token_format", "jwt"       },
        });

        await SendTokenRequestAsync(body, ct);
    }

    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "refresh_token", _refreshToken! },
            { "grant_type",    "refresh_token" },
            { "token_format",  "jwt" },
            { "scope",         Scope },
        });

        await SendTokenRequestAsync(body, ct);
    }

    private async Task SendTokenRequestAsync(FormUrlEncodedContent body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AuthBaseUrl}/token")
        {
            Content = body,
        };
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicAuth);

        var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Token endpoint returned {(int)resp.StatusCode}: {json}");

        var raw = JsonDocument.Parse(json).RootElement;
        _accessToken  = raw.GetProperty("access_token").GetString()!;
        _refreshToken = raw.GetProperty("refresh_token").GetString()!;

        var accessExpiresIn  = raw.GetProperty("expires_in").GetInt32();
        var refreshExpiresIn = raw.GetProperty("refresh_token_expires_in").GetInt32();
        // Shave 60 s off to handle clock skew
        _accessTokenExpiry  = DateTime.UtcNow.AddSeconds(accessExpiresIn  - 60);
        _refreshTokenExpiry = DateTime.UtcNow.AddSeconds(refreshExpiresIn - 60);
    }


    private void LoadCache()
    {
        if (!File.Exists(_tokenCachePath)) return;
        try
        {
            var raw = JsonDocument.Parse(File.ReadAllText(_tokenCachePath)).RootElement;
            _accessToken        = raw.GetProperty("access_token").GetString();
            _refreshToken       = raw.GetProperty("refresh_token").GetString();
            _accessTokenExpiry  = raw.GetProperty("access_token_expiry").GetDateTime();
            _refreshTokenExpiry = raw.GetProperty("refresh_token_expiry").GetDateTime();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not load token cache: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_tokenCachePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var doc = new
        {
            access_token         = _accessToken,
            refresh_token        = _refreshToken,
            access_token_expiry  = _accessTokenExpiry,
            refresh_token_expiry = _refreshTokenExpiry,
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_tokenCachePath, JsonSerializer.Serialize(doc, options));
    }

    public void Dispose() => _http.Dispose();
}
