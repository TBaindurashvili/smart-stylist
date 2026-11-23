using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Reporting;

/// <param name="Price">A price level and how many days the item sold at it.</param>
public sealed record PriceLevel(decimal Price, int Days);

/// <summary>Price history of one product (optionally one colour and size): the numbers a buy-now-or-wait call rests on.</summary>
public sealed record PriceHistory(
    long ProductId,
    string Title,
    string? Brand,
    string ShopName,
    string Url,
    string? Colour,
    string? Size,
    string Currency,
    decimal? Current,
    decimal? CurrentListPrice,
    bool? InStock,
    DateOnly? LastSeen,
    decimal? AllTimeLow,
    DateOnly? AllTimeLowOn,
    decimal? Low90Days,
    decimal? Median,
    int Days,
    IReadOnlyList<PriceLevel> Levels,
    IReadOnlyList<PricePoint> Daily);

public enum DropKind
{
    /// <summary>Cheaper than on any earlier day.</summary>
    NewLow,

    /// <summary>Out of stock on the previous day it was seen, in stock now.</summary>
    BackInStock,
}

public sealed record PriceDrop(long ProductId, string Title, string? Brand, string ShopName, string Url, string? Colour, string? Size,
    string Currency, decimal Price, decimal? PreviousLow, DateOnly On, DropKind Kind);

/// <summary>get_history and get_drops for the stylist layer.</summary>
public sealed class PriceHistoryService(CatalogDbContext db)
{
    public async Task<PriceHistory?> GetHistoryAsync(long productId, string? colour, string? size, DateOnly today, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.Title, p.Brand, ShopName = p.Shop.Name, p.Url })
            .SingleOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return null;
        }

        var query = db.PriceSnapshots.AsNoTracking().Where(s => s.ProductId == productId);
        if (!string.IsNullOrWhiteSpace(colour))
        {
            query = query.Where(s => s.Colour != null && s.Colour.ToLower() == colour.ToLower());
        }

        if (!string.IsNullOrWhiteSpace(size))
        {
            query = query.Where(s => s.SizeLabel != null && s.SizeLabel.ToLower() == size.ToLower());
        }

        var snapshots = await query
            .Select(s => new { s.ObservedOn, s.Price, s.ListPrice, s.Currency, s.InStock })
            .ToListAsync(cancellationToken);
        if (snapshots.Count == 0)
        {
            return new PriceHistory(product.Id, product.Title, product.Brand, product.ShopName, product.Url, colour, size, "", null, null, null,
                null, null, null, null, null, 0, [], []);
        }

        var lastDay = snapshots.Max(s => s.ObservedOn);
        var latest = snapshots.Where(s => s.ObservedOn == lastDay).MinBy(s => s.Price)!;
        var daily = snapshots
            .Where(s => s.Currency == latest.Currency)
            .GroupBy(s => s.ObservedOn)
            .Select(g => g.MinBy(s => s.Price)!)
            .OrderBy(s => s.ObservedOn)
            .Select(s => new PricePoint(s.ObservedOn, s.Price, s.ListPrice > s.Price))
            .ToList();
        var lowest = daily.OrderBy(d => d.Price).ThenByDescending(d => d.Day).First();
        var recent = daily.Where(d => d.Day >= today.AddDays(-90)).ToList();

        return new PriceHistory(product.Id, product.Title, product.Brand, product.ShopName, product.Url, colour, size, latest.Currency,
            latest.Price, latest.ListPrice, latest.InStock, lastDay, lowest.Price, lowest.Day,
            recent.Count == 0 ? null : recent.Min(d => d.Price),
            Median(daily.Select(d => d.Price).ToList()), daily.Count,
            daily.GroupBy(d => d.Price).Select(g => new PriceLevel(g.Key, g.Count())).OrderBy(l => l.Price).ToList(),
            daily);
    }

    /// <summary>
    /// Products (per colour and size) that hit a new low or came back in stock on or after <paramref name="since"/>.
    /// Pass sizes to keep only those, e.g. your sizes from the fit ledger.
    /// </summary>
    public async Task<IReadOnlyList<PriceDrop>> GetDropsAsync(DateOnly since, IReadOnlyCollection<string>? sizes, CancellationToken cancellationToken)
    {
        var changed = await db.PriceSnapshots.AsNoTracking()
            .Where(s => s.ObservedOn >= since)
            .Select(s => s.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (changed.Count == 0)
        {
            return [];
        }

        var snapshots = await db.PriceSnapshots.AsNoTracking()
            .Where(s => changed.Contains(s.ProductId))
            .Select(s => new
            {
                s.ProductId, s.VariantKey, s.Colour, s.SizeLabel, s.ObservedOn, s.Price, s.Currency, s.InStock,
                s.Product.Title, s.Product.Brand, ShopName = s.Product.Shop.Name, s.Product.Url,
            })
            .ToListAsync(cancellationToken);

        var wanted = sizes is { Count: > 0 } ? sizes.Select(s => s.Trim().ToUpperInvariant()).ToHashSet() : null;
        var drops = new List<PriceDrop>();
        foreach (var sku in snapshots.GroupBy(s => (s.ProductId, s.VariantKey)))
        {
            var days = sku.OrderBy(s => s.ObservedOn).ToList();
            var last = days[^1];
            if (wanted is not null && (last.SizeLabel is null || !wanted.Contains(last.SizeLabel.Trim().ToUpperInvariant())))
            {
                continue;
            }

            if (last.ObservedOn < since)
            {
                continue;
            }

            var earlier = days.Where(s => s.ObservedOn < last.ObservedOn && s.Currency == last.Currency).ToList();
            if (earlier.Count == 0)
            {
                continue;
            }

            var previousLow = earlier.Min(s => s.Price);
            if (last.Price < previousLow && last.InStock != false)
            {
                drops.Add(new PriceDrop(last.ProductId, last.Title, last.Brand, last.ShopName, last.Url, last.Colour, last.SizeLabel,
                    last.Currency, last.Price, previousLow, last.ObservedOn, DropKind.NewLow));
            }
            else if (last.InStock == true && earlier[^1].InStock == false)
            {
                drops.Add(new PriceDrop(last.ProductId, last.Title, last.Brand, last.ShopName, last.Url, last.Colour, last.SizeLabel,
                    last.Currency, last.Price, previousLow, last.ObservedOn, DropKind.BackInStock));
            }
        }

        return drops.OrderBy(d => d.Kind).ThenByDescending(d => d.On).ToList();
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
