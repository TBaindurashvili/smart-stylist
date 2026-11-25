namespace SmartStylist.Modules.Catalog;

internal static class TextExtensions
{
    /// <summary>Cuts a value to a column's maximum length.</summary>
    public static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max];
}

internal static class PriceExtensions
{
    /// <summary>The middle price, the "usual" price a new one is compared with; null when there are none.</summary>
    public static decimal? Median(this IEnumerable<decimal> prices)
    {
        var sorted = prices.Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
