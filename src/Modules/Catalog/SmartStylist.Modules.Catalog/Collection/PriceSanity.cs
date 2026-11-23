namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Rejects prices that cannot be real, such as a $1.90 placeholder read from page data next to a $49.90 sweater.
/// A price is rejected when it is under <see cref="PriceSanityOptions.MinShareOfReference"/> of the SKU's compare-at
/// price or of its usual (median) price, or under the floor for its kind of item.
/// </summary>
public static class PriceSanity
{
    /// <returns>Why the price was rejected, or null when it looks real.</returns>
    public static string? Check(decimal price, decimal? listPrice, decimal? usualPrice, string currency, string? title, PriceSanityOptions options)
    {
        if (price <= 0)
        {
            return "not a positive amount";
        }

        var share = options.MinShareOfReference;
        if (listPrice > 0 && price < listPrice.Value * share)
        {
            return $"{price} is under {share:P0} of its compare-at price {listPrice}";
        }

        if (usualPrice > 0 && price < usualPrice.Value * share)
        {
            return $"{price} is under {share:P0} of its usual price {usualPrice}";
        }

        // Floors are in dollars, euros or pounds; GEL and other currencies are left to the two checks above.
        if (currency is not ("USD" or "EUR" or "GBP"))
        {
            return null;
        }

        var floor = Floor(title, options);
        return price < floor ? $"{price} {currency} is under the {floor} floor for this kind of item" : null;
    }

    internal static decimal Floor(string? title, PriceSanityOptions options)
    {
        var text = title?.ToLowerInvariant() ?? "";
        var matched = options.CategoryFloors
            .Where(f => text.Contains(f.Key.ToLowerInvariant(), StringComparison.Ordinal))
            .Select(f => f.Value)
            .DefaultIfEmpty(options.MinPrice)
            .Max();
        return Math.Max(matched, options.MinPrice);
    }
}

public sealed class PriceSanityOptions
{
    /// <summary>A price under this share of the compare-at or usual price is a misread, not a sale. 0.15 = 85% off.</summary>
    public decimal MinShareOfReference { get; set; } = 0.15m;

    /// <summary>Lowest believable price for anything (USD/EUR/GBP).</summary>
    public decimal MinPrice { get; set; } = 3m;

    /// <summary>Lowest believable price per kind of item, matched as a word in the product title, e.g. "jeans": 15.</summary>
    public Dictionary<string, decimal> CategoryFloors { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jeans"] = 15m, ["chino"] = 12m, ["pants"] = 10m, ["sweater"] = 12m, ["merino"] = 12m, ["cardigan"] = 12m,
        ["hoodie"] = 12m, ["sweatshirt"] = 12m, ["crewneck"] = 12m, ["jacket"] = 20m, ["coat"] = 30m, ["parka"] = 30m,
        ["boot"] = 30m, ["sneaker"] = 20m, ["shoe"] = 20m, ["shirt"] = 4m, ["polo"] = 6m, ["fleece"] = 15m,
    };
}
