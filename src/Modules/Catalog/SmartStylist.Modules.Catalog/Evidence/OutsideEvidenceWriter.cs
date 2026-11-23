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
        url = Truncate(url, MaxUrl);
        var product = await db.Products.FirstOrDefaultAsync(p => p.ShopId == shop.Id && (p.Url == url || p.ExternalId == url), cancellationToken);
        if (product is not null)
        {
            return product;
        }

        product = new Product
        {
            ShopId = shop.Id,
            ExternalId = Truncate(url, 500),
            // A product known only from notes has a "manual:" key and no page to link to.
            Url = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "",
            Title = Truncate(string.IsNullOrWhiteSpace(title) ? url : title, 500),
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return product;
    }

    /// <summary>Adds one day of prices; returns how many were new. Days the app already has for a variant are left alone.</summary>
    public static async Task<int> AddAsync(CatalogDbContext db, Product product, DateOnly day, IEnumerable<ScrapedOffer> offers,
        string fallbackCurrency, PriceSource source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.PriceSnapshots
            .Where(s => s.ProductId == product.Id && s.ObservedOn == day)
            .Select(s => s.VariantKey)
            .ToListAsync(cancellationToken);
        var taken = existing.ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var offer in offers.GroupBy(o => o.VariantKey).Select(g => g.First()))
        {
            var key = Truncate(offer.VariantKey, 200);
            if (PriceSanity.Check(offer.Price, offer.ListPrice, null, offer.Currency, product.Title, new PriceSanityOptions()) is not null
                || !taken.Add(key))
            {
                continue;
            }

            db.PriceSnapshots.Add(new PriceSnapshot
            {
                ProductId = product.Id,
                VariantKey = key,
                SizeLabel = offer.SizeLabel is null ? null : Truncate(offer.SizeLabel, 100),
                Colour = offer.Colour is null ? null : Truncate(offer.Colour, 100),
                ObservedOn = day,
                Price = offer.Price,
                ListPrice = offer.ListPrice,
                Currency = offer.Currency.Length == 3 ? offer.Currency : fallbackCurrency,
                InStock = offer.InStock,
                CapturedAt = now,
                Source = source,
            });
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    public static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
