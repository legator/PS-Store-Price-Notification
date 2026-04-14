namespace PSPriceNotification.Models;

public record PriceInfo(
    string? BasePrice,
    string? DiscountedPrice,
    string? Currency,
    int? DiscountPercent,
    bool IsFree,
    bool IsAvailable)
{
    public string? CurrentPrice => DiscountedPrice ?? BasePrice;

    public override string ToString()
    {
        if (IsFree) return "Free";
        if (DiscountedPrice != null && DiscountedPrice != BasePrice)
        {
            var pct = DiscountPercent.HasValue ? $" (-{DiscountPercent}%)" : "";
            return $"{DiscountedPrice}{pct} (was {BasePrice})";
        }
        return BasePrice ?? "N/A";
    }
}
