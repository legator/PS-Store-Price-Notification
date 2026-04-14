namespace PSPriceNotification.Models;

// ─── Root ────────────────────────────────────────────────────────────────────

public class AppConfig
{
    public NotificationConfig Notification { get; set; } = new();
    public CheckingConfig Checking { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public AccountConfig Account { get; set; } = new();
    public Dictionary<string, string>? Locales { get; set; }
}

// ─── Account ──────────────────────────────────────────────────────────────────

/// <summary>
/// Optional PSN account credentials for authenticated store requests.
///
/// How to get your NPSSO token:
///   1. Log in at https://www.playstation.com in a browser.
///   2. Visit https://ca.account.sony.com/api/v1/ssocookie in the same browser.
///   3. Copy the 64-character npsso value and paste it below.
///
/// Tokens are cached in <see cref="TokenCacheFile"/> and refreshed automatically —
/// you only need to update npsso once every ~2 months when the refresh token expires.
/// </summary>
public class AccountConfig
{
    public string? Npsso { get; set; }
    public string TokenCacheFile { get; set; } = "data/psn_tokens.json";
}

// ─── Notification ─────────────────────────────────────────────────────────────

public class NotificationConfig
{
    public WindowsToastConfig WindowsToast { get; set; } = new();
}

public class WindowsToastConfig
{
    public bool Enabled { get; set; } = true;
}

// ─── Checking ────────────────────────────────────────────────────────────────

public class CheckingConfig
{
    public List<string>? Countries { get; set; }
    public string? PrimaryCountry { get; set; }
    /// <summary>Maximum number of countries fetched simultaneously.</summary>
    public int MaxConcurrency { get; set; } = 5;
    /// <summary>Seconds to wait inside each concurrent slot before firing the HTTP request.</summary>
    public double RequestDelay { get; set; } = 0.5;
}

// ─── Storage ─────────────────────────────────────────────────────────────────

public class StorageConfig
{
    public string PriceHistoryDb { get; set; } = "data/prices.db";
    public int MaxHistoryDays { get; set; } = 90;
}

// ─── Logging ─────────────────────────────────────────────────────────────────

public class LoggingConfig
{
    public string Level { get; set; } = "INFO";
    public string File { get; set; } = "data/ps_price_check.log";
}
