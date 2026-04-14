using Microsoft.Toolkit.Uwp.Notifications;
using PSPriceNotification.Models;

namespace PSPriceNotification.Services;

public class Notifier
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
        if (_cfg.WindowsToast.Enabled)
            ShowWindowsToast(gameName, country, storeUrl, oldPrice, newPrice);

        return Task.CompletedTask;
    }

    // ─── Windows Toast ────────────────────────────────────────────────────────

    private static void ShowWindowsToast(
        string gameName, string country, string storeUrl,
        PriceInfo? oldPrice, PriceInfo newPrice)
    {
        try
        {
            var bodyLines = new List<string>();

            if (oldPrice != null)
                bodyLines.Add($"Was: {oldPrice}");

            bodyLines.Add($"Now: {newPrice}");

            if (newPrice.DiscountPercent.HasValue)
                bodyLines.Add($"Discount: -{newPrice.DiscountPercent}%");

            var builder = new ToastContentBuilder()
                .AddText($"🎮 {gameName} — {country.ToUpperInvariant()}")
                .AddText(string.Join("  •  ", bodyLines))
                .AddAttributionText("PlayStation Store");

            if (Uri.TryCreate(storeUrl, UriKind.Absolute, out var storeUri))
                builder.AddButton(
                    new ToastButton()
                        .SetContent("View on PS Store")
                        .SetProtocolActivation(storeUri));

            builder.Show();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Windows Toast failed: {ex.Message}");
        }
    }

}
