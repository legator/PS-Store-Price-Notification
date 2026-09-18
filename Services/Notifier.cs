using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

public partial class Notifier
{
    private readonly NotificationConfig _cfg;
    private static int _warnedNotSupported;

    public Notifier(NotificationConfig config) => _cfg = config;

    public static bool IsWindowsToastSupported
    {
        get
        {
            bool supported = false;
            CheckToastSupported(ref supported);
            return supported;
        }
    }

    // ─── Main dispatch ────────────────────────────────────────────────────────

    public Task NotifyPriceChangeAsync(
        string gameName,
        string country,
        string storeUrl,
        PriceInfo? oldPrice,
        PriceInfo newPrice)
    {
        if (_cfg.WindowsToast.Enabled && OperatingSystem.IsWindows())
        {
            if (IsWindowsToastSupported)
            {
                ShowWindowsToast(gameName, country, storeUrl, oldPrice, newPrice);
            }
            else
            {
                if (Interlocked.Exchange(ref _warnedNotSupported, 1) == 0)
                {
                    Logger.Warn("Windows toast notifications are enabled in config.yaml, but this build was run using the cross-platform 'net10.0' target. Run with '--framework net10.0-windows10.0.17763.0' to enable native toast notifications.");
                }
            }
        }

        return Task.CompletedTask;
    }

    static partial void CheckToastSupported(ref bool supported);

    static partial void ShowWindowsToast(
        string gameName,
        string country,
        string storeUrl,
        PriceInfo? oldPrice,
        PriceInfo newPrice);
}
