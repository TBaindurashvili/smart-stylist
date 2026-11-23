using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Refuses prices no shop really charges, before they are saved from any source: Uniqlo shows $1.90 on sold-out
/// pages, and a parser can pick up a shipping fee or a "from $5" teaser. A price is refused when it is under 15% of
/// the SKU's usual (median) price or of its own crossed-out price, or under the floor for its category.
/// </summary>
public sealed class PriceSanityCheck
{
    private readonly PriceSanityOptions _options;
    private readonly IReadOnlyList<(PriceFloorOptions Floor, Regex Words)> _floors;

    public PriceSanityCheck(IOptions<CatalogOptions> options)
    {
        _options = options.Value.PriceSanity;
        _floors = _options.Floors
            .Where(f => f.Keywords.Count > 0)
            .Select(f => (f, new Regex(
                $@"\b({string.Join('|', f.Keywords.Select(Regex.Escape))})\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
            .ToList();
    }

    /// <summary>Why the price is not believable, or null when it is fine.</summary>
    /// <param name="usualPrice">Median of the SKU's earlier prices in the same currency; null when there are none.</param>
    public string? Reject(ScrapedOffer offer, string title, decimal? usualPrice)
    {
        if (offer.Price <= 0)
        {
            return "price is zero";
        }

        var minShare = _options.MinShareOfUsual;
        if (offer.ListPrice is > 0 and var list && offer.Price < list * minShare)
        {
            return $"{Money(offer.Price)} is under {minShare:P0} of its own crossed-out price {Money(list)}";
        }

        if (usualPrice is > 0 and var usual && offer.Price < usual * minShare)
        {
            return $"{Money(offer.Price)} is under {minShare:P0} of its usual {Money(usual)}";
        }

        if (string.Equals(offer.Currency, _options.FloorCurrency, StringComparison.OrdinalIgnoreCase))
        {
            var (category, floor) = FloorFor(title);
            if (offer.Price < floor)
            {
                return $"{Money(offer.Price)} is under the {Money(floor)} floor for {category}";
            }
        }

        return null;
    }

    internal (string Category, decimal Floor) FloorFor(string title)
    {
        foreach (var (floor, words) in _floors)
        {
            if (words.IsMatch(title))
            {
                return (floor.Category, floor.Min);
            }
        }

        return ("any item", _options.DefaultFloor);
    }

    /// <summary>The median of earlier prices, the "usual" price a new one is compared with.</summary>
    public static decimal? Median(IReadOnlyCollection<decimal> prices)
    {
        if (prices.Count == 0)
        {
            return null;
        }

        var sorted = prices.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
