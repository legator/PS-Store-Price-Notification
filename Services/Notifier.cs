using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

public partial class Notifier
{
    private readonly NotificationConfig _cfg;

    public Notifier(NotificationConfig config) => _cfg = config;

    // ─── Main dispatch ────────────────────────────────────────────────────────

    public Task NotifyPriceChangeAsync(
        string gameName,
        string country,
        string storeUrl,
        PriceInfo? oldPrice,
        PriceInfo newPrice)
    {
        if (_cfg.WindowsToast.Enabled && OperatingSystem.IsWindows())
            ShowWindowsToast(gameName, country, storeUrl, oldPrice, newPrice);

        return Task.CompletedTask;
    }

    static partial void ShowWindowsToast(
        string gameName,
        string country,
        string storeUrl,
        PriceInfo? oldPrice,
        PriceInfo newPrice);
}
