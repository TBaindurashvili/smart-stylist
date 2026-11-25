using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>What one <see cref="PriceWriter.WriteAsync"/> call saved.</summary>
/// <param name="Saved">The saved products and their kept prices, in input order; a product whose prices were all refused is left out.</param>
public sealed record PriceWriteResult(IReadOnlyList<SavedProduct> Saved, int ProductsSeen, int PricesWritten, int PricesRefused);

public sealed record SavedProduct(Product Product, IReadOnlyList<ScrapedOffer> Offers);

/// <summary>
/// Saves today's prices of products read from a shop (crawl, browser extension, browser watch, affiliate feed).
/// Matches each product to the one already saved (by id, or by page address for products first seen in a sale
/// email), refuses prices that fail the sanity check, and replaces today's price of each variant.
/// </summary>
public sealed class PriceWriter(CatalogDbContext db, PriceSanityCheck sanity, TimeProvider time, ILogger<PriceWriter> logger)
{
    /// <summary>Earlier days a SKU's usual price is taken from, for the sanity check.</summary>
    public const int UsualPriceDays = 90;

    private const int MaxUrl = 2000;
    private const int SaveEvery = 500;
    private const int RefusalsLogged = 5;

    public async Task<PriceWriteResult> WriteAsync(Shop shop, IReadOnlyList<ScrapedProduct> scraped, PriceSource source, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var items = scraped
            .Select(p => p with { ExternalId = p.ExternalId.Truncate(500), Url = p.Url.Truncate(MaxUrl) })
            .GroupBy(p => p.ExternalId)
            .Select(g => g.First())
            .ToList();

        // The extension or a sale email may have saved the same product under its page address.
        var ids = items.Select(p => p.ExternalId).ToList();
        var urls = items.Select(p => p.Url).ToList();
        var existing = await db.Products
            .Where(p => p.ShopId == shop.Id && (ids.Contains(p.ExternalId) || urls.Contains(p.Url) || urls.Contains(p.ExternalId)))
            .ToListAsync(cancellationToken);
        var byId = existing.GroupBy(p => p.ExternalId).ToDictionary(g => g.Key, g => g.First());
        var byUrl = existing.GroupBy(p => p.Url).ToDictionary(g => g.Key, g => g.First());

        var existingIds = existing.Select(p => p.Id).ToList();
        var todays = (await db.PriceSnapshots
                .Where(s => s.ObservedOn == today && existingIds.Contains(s.ProductId))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => (s.ProductId, s.VariantKey));
        var usualFrom = today.AddDays(-UsualPriceDays);
        var usualPrices = (await db.PriceSnapshots
                .Where(s => s.ObservedOn < today && s.ObservedOn >= usualFrom && existingIds.Contains(s.ProductId))
                .Select(s => new { s.ProductId, s.VariantKey, s.Currency, s.Price })
                .ToListAsync(cancellationToken))
            .GroupBy(s => (s.ProductId, s.VariantKey, s.Currency))
            .ToDictionary(g => g.Key, g => PriceSanityCheck.Median(g.Select(s => s.Price).ToList()));

        // Two items of one batch can land on the same product (same page address): one price per variant and day.
        var thisCall = new Dictionary<(Product, string), PriceSnapshot>();
        var saved = new List<SavedProduct>();
        int written = 0, refused = 0, pending = 0;
        foreach (var item in items)
        {
            var product = byId.GetValueOrDefault(item.ExternalId) ?? byUrl.GetValueOrDefault(item.Url) ?? byId.GetValueOrDefault(item.Url);
            var offers = new List<ScrapedOffer>();
            foreach (var offer in item.Offers.GroupBy(o => o.VariantKey.Truncate(200)).Select(g => g.First() with { VariantKey = g.Key }))
            {
                var priced = offer with { Currency = offer.Currency.Length == 3 ? offer.Currency : shop.Currency };
                var usual = product is null ? null : usualPrices.GetValueOrDefault((product.Id, priced.VariantKey, priced.Currency));
                if (sanity.Reject(priced, item.Title, usual) is { } reason)
                {
                    if (refused++ < RefusalsLogged)
                    {
                        logger.LogWarning("{Shop}: refused {Url} {Variant}: {Reason}", shop.Key, item.Url, priced.VariantKey, reason);
                    }

                    continue;
                }

                offers.Add(priced);
            }

            if (offers.Count == 0)
            {
                continue;
            }

            if (product is null)
            {
                product = new Product { ShopId = shop.Id, ExternalId = item.ExternalId, Url = item.Url, Title = "", FirstSeenAt = now };
                db.Products.Add(product);
                byId[item.ExternalId] = product;
            }

            product.Url = item.Url;
            // An API that gives prices but no name (Uniqlo's) keeps the title the product already has.
            product.Title = string.IsNullOrWhiteSpace(item.Title)
                ? string.IsNullOrWhiteSpace(product.Title) ? item.ExternalId : product.Title
                : item.Title.Truncate(500);
            product.Brand = item.Brand?.Truncate(200) ?? product.Brand;
            product.ImageUrl = item.ImageUrl is { Length: <= MaxUrl } ? item.ImageUrl : product.ImageUrl;
            product.LastSeenAt = now;

            foreach (var offer in offers)
            {
                if (!thisCall.TryGetValue((product, offer.VariantKey), out var snapshot)
                    && (product.Id == 0 || !todays.TryGetValue((product.Id, offer.VariantKey), out snapshot)))
                {
                    snapshot = new PriceSnapshot { Product = product, VariantKey = offer.VariantKey, ObservedOn = today, Currency = offer.Currency };
                    db.PriceSnapshots.Add(snapshot);
                }

                thisCall[(product, offer.VariantKey)] = snapshot;

                snapshot.SizeLabel = offer.SizeLabel?.Truncate(100);
                snapshot.Colour = offer.Colour?.Truncate(100);
                snapshot.Price = offer.Price;
                snapshot.ListPrice = offer.ListPrice;
                snapshot.Currency = offer.Currency;
                snapshot.InStock = offer.InStock;
                snapshot.CapturedAt = now;
                snapshot.Source = source;
                written++;
            }

            saved.Add(new SavedProduct(product, offers));

            if (++pending >= SaveEvery)
            {
                await db.SaveChangesAsync(cancellationToken);
                pending = 0;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PriceWriteResult(saved, items.Count, written, refused);
    }
}
