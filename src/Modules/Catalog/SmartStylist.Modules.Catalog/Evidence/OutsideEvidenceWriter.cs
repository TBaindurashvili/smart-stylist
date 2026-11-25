using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Newsletters;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Evidence;

/// <summary>
/// Stores prices the app did not see itself (archived pages, written-down prices). They never overwrite a price
/// the app saw on the same day: own data wins.
/// </summary>
internal static class OutsideEvidenceWriter
{
    private const int MaxUrl = 2000;

    public static async Task<Shop?> FindShopAsync(CatalogDbContext db, Uri url, string? shopKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(shopKey))
        {
            return await db.Shops.FirstOrDefaultAsync(s => s.Key == shopKey, cancellationToken);
        }

        var shops = await db.Shops.ToListAsync(cancellationToken);
        return shops.FirstOrDefault(s => new ShopDomains(s.Key, s.BaseUrl, []).IsShopPage(url.Host));
    }

    /// <summary>The product with this page address, created when it is new.</summary>
    public static async Task<Product> FindOrAddProductAsync(CatalogDbContext db, Shop shop, string url, string? title, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        url = url.Truncate(MaxUrl);
        var product = await db.Products.FirstOrDefaultAsync(p => p.ShopId == shop.Id && (p.Url == url || p.ExternalId == url), cancellationToken);
        if (product is not null)
        {
            return product;
        }

        product = new Product
        {
            ShopId = shop.Id,
            ExternalId = url.Truncate(500),
            Url = url,
            Title = (string.IsNullOrWhiteSpace(title) ? url : title).Truncate(500),
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return product;
    }

    /// <summary>
    /// Adds one day of prices; returns how many were new and how many the sanity check refused.
    /// Days the app already has for a variant are left alone.
    /// </summary>
    public static async Task<(int Added, int Rejected)> AddAsync(CatalogDbContext db, PriceSanityCheck sanity, Product product, DateOnly day,
        IEnumerable<ScrapedOffer> offers, string fallbackCurrency, PriceSource source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var known = await db.PriceSnapshots
            .Where(s => s.ProductId == product.Id)
            .Select(s => new { s.VariantKey, s.ObservedOn, s.Currency, s.Price })
            .ToListAsync(cancellationToken);
        var taken = known.Where(s => s.ObservedOn == day).Select(s => s.VariantKey).ToHashSet(StringComparer.Ordinal);

        int added = 0, rejected = 0;
        foreach (var offer in offers.GroupBy(o => o.VariantKey).Select(g => g.First()))
        {
            var key = offer.VariantKey.Truncate(200);
            if (taken.Contains(key))
            {
                continue;
            }

            var currency = offer.Currency.Length == 3 ? offer.Currency : fallbackCurrency;
            var usual = known.Where(s => s.VariantKey == key && s.Currency == currency).Select(s => s.Price).Median();
            if (sanity.Reject(offer with { Currency = currency }, product.Title, usual) is not null)
            {
                rejected++;
                continue;
            }

            taken.Add(key);

            db.PriceSnapshots.Add(new PriceSnapshot
            {
                ProductId = product.Id,
                VariantKey = key,
                SizeLabel = offer.SizeLabel?.Truncate(100),
                Colour = offer.Colour?.Truncate(100),
                ObservedOn = day,
                Price = offer.Price,
                ListPrice = offer.ListPrice,
                Currency = currency,
                InStock = offer.InStock,
                CapturedAt = now,
                Source = source,
            });
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (added, rejected);
    }

}
