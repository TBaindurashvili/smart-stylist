using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Catalogue;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Reporting;

/// <param name="Best">The best route that works, or null when no price source works yet.</param>
/// <param name="ProductsPriced">Products of this brand priced in the last 30 days, by where the price came from: the proof that a route works.</param>
public sealed record BrandCoverage(
    BrandOptions Brand,
    RouteStatus? Best,
    IReadOnlyList<RouteStatus> Routes,
    IReadOnlyDictionary<PriceSource, int> ProductsPriced);

/// <summary>For each catalogue brand: how its prices can be read, and what actually arrived in the last 30 days.</summary>
public sealed class CatalogueReport(CatalogDbContext db, QualityCatalogue catalogue, IOptions<CatalogOptions> options, EgressRouter egress, TimeProvider time)
{
    public const int Days = 30;

    public async Task<IReadOnlyList<BrandCoverage>> BuildAsync(CancellationToken cancellationToken)
    {
        var strategies = await db.Shops.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.DetectedStrategy, cancellationToken);

        var from = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-Days);
        var priced = await db.PriceSnapshots.AsNoTracking()
            .Where(s => s.ObservedOn >= from)
            .Select(s => new { s.ProductId, Shop = s.Product.Shop.Key, s.Product.Brand, s.Product.Title, s.Source })
            .Distinct()
            .ToListAsync(cancellationToken);

        // A product counts for a brand at a shop only that brand is sold at, or when it names the brand.
        var brandsByShop = catalogue.Brands
            .SelectMany(b => b.Shops.Select(s => (Shop: s, Brand: b.Key)))
            .ToLookup(x => x.Shop, x => x.Brand);
        var productBrand = priced
            .GroupBy(p => p.ProductId)
            .ToDictionary(g => g.Key, g =>
            {
                var p = g.First();
                var owners = brandsByShop[p.Shop].ToList();
                return owners.Count == 1 ? owners[0] : catalogue.FindBrand(p.Brand, p.Title)?.Key;
            });

        var shops = options.Value.Shops;
        return catalogue.Brands
            .Select(brand =>
            {
                var routes = QualityCatalogue.Routes(brand, shops, key => strategies.GetValueOrDefault(key), egress.IsConfigured);
                var counts = priced
                    .Where(p => productBrand[p.ProductId] == brand.Key && (brand.Shops.Contains(p.Shop) || p.Shop.StartsWith("awin-", StringComparison.Ordinal)))
                    .GroupBy(p => p.Source)
                    .ToDictionary(g => g.Key, g => g.Select(p => p.ProductId).Distinct().Count());
                return new BrandCoverage(brand, QualityCatalogue.Best(routes), routes, counts);
            })
            .ToList();
    }
}
