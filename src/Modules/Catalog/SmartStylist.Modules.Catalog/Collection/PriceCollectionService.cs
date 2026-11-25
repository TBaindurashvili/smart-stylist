using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>Runs one shop's daily collection and stores products, prices and a run record.</summary>
public sealed class PriceCollectionService
{
    // The browser is not in the automatic order: a crawl in Chrome is set per shop (Strategy), and blocked shops
    // get their watched pages visited a few times a day instead (BrowserWatchService).
    private static readonly CollectorStrategy[] AutoOrder = [CollectorStrategy.Shopify, CollectorStrategy.SitemapJsonLd];

    private readonly CatalogDbContext _db;
    private readonly IReadOnlyDictionary<CollectorStrategy, IShopCollector> _collectors;
    private readonly IOptions<CatalogOptions> _options;
    private readonly PriceWriter _writer;
    private readonly TimeProvider _time;
    private readonly ILogger<PriceCollectionService> _logger;

    internal PriceCollectionService(
        CatalogDbContext db,
        IEnumerable<IShopCollector> collectors,
        IOptions<CatalogOptions> options,
        PriceWriter writer,
        TimeProvider time,
        ILogger<PriceCollectionService> logger)
    {
        _db = db;
        _collectors = collectors.ToDictionary(c => c.Strategy);
        _options = options;
        _writer = writer;
        _time = time;
        _logger = logger;
    }

    public async Task<CollectionRun> CollectAsync(string shopKey, int? maxProducts, CancellationToken cancellationToken)
    {
        var shop = await _db.Shops.SingleOrDefaultAsync(s => s.Key == shopKey, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown shop '{shopKey}'. Check Catalog:Shops in appsettings.json.");

        var run = new CollectionRun { Shop = shop, StartedAt = _time.GetUtcNow() };
        _db.CollectionRuns.Add(run);
        if (shop.Dropped)
        {
            run.Status = RunStatus.Failed;
            run.Error = "Dropped from collection (Catalog:Shops Dropped = true).";
            run.FinishedAt = run.StartedAt;
            await _db.SaveChangesAsync(cancellationToken);
            return run;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var shopOptions = _options.Value.Shops.FirstOrDefault(s => s.Key == shopKey);
        var limit = maxProducts ?? _options.Value.MaxProductsPerShop;
        // Revisit products priced in the last week first; ones gone longer make room for new sitemap URLs.
        var recent = _time.GetUtcNow().AddDays(-7);
        var knownUrls = await _db.Products
            .Where(p => p.ShopId == shop.Id && p.LastSeenAt >= recent)
            .OrderByDescending(p => p.LastSeenAt)
            .Select(p => p.Url)
            .Take(limit)
            .ToListAsync(cancellationToken);
        // The long-running core products are priced every day, whether or not the sitemap lists them first.
        knownUrls = _options.Value.CoreProducts.Where(c => c.Shop == shopKey).Select(c => c.Url)
            .Concat(knownUrls).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToList();

        var baseUrl = new Uri(shop.BaseUrl);
        var request = new ShopRequest(
            ShopRoot: new Uri(baseUrl.GetLeftPart(UriPartial.Authority)),
            BaseUrl: baseUrl,
            Currency: shop.Currency,
            SitemapUrl: shopOptions?.SitemapUrl,
            ProductUrlPattern: shopOptions?.ProductUrlPattern,
            KnownProductUrls: knownUrls,
            MaxProducts: limit);

        var errors = new List<string>();
        try
        {
            foreach (var strategy in StrategiesToTry(shop))
            {
                if (!_collectors.TryGetValue(strategy, out var collector))
                {
                    continue;
                }

                try
                {
                    _logger.LogInformation("Collecting {Shop} with {Strategy}", shop.Key, strategy);
                    var result = await collector.CollectAsync(request, cancellationToken);
                    if (result.Products.Count == 0)
                    {
                        errors.Add($"{strategy}: no products");
                        continue;
                    }

                    run.Strategy = strategy;
                    run.PagesFailed = result.FailedRequests;
                    if (shop.ConfiguredStrategy == CollectorStrategy.Auto)
                    {
                        shop.DetectedStrategy = strategy;
                    }

                    var written = await _writer.WriteAsync(shop, result.Products, PriceSource.Crawl, cancellationToken);
                    run.ProductsSeen = written.ProductsSeen;
                    run.SnapshotsWritten = written.PricesWritten;
                    run.PricesRejected = written.PricesRefused;
                    run.Status = RunStatus.Succeeded;
                    break;
                }
                catch (CollectorUnusableException ex)
                {
                    _logger.LogWarning("{Shop}: {Error}", shop.Key, ex.Message);
                    errors.Add(ex.Message);
                }
            }

            if (run.Status != RunStatus.Succeeded)
            {
                run.Status = RunStatus.Failed;
                if (shop.ConfiguredStrategy == CollectorStrategy.Auto)
                {
                    // Probe everything again tomorrow instead of retrying a strategy that stopped working.
                    shop.DetectedStrategy = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Collection for {Shop} crashed", shop.Key);
            foreach (var entry in _db.ChangeTracker.Entries().Where(e => e.Entity is Product or PriceSnapshot).ToList())
            {
                entry.State = EntityState.Detached;
            }

            run.Status = RunStatus.Failed;
            errors.Add(ex.Message);
        }
        finally
        {
            run.FinishedAt = _time.GetUtcNow();
            run.Error = errors.Count == 0 ? null : string.Join(" | ", errors).Truncate(4000);
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        _logger.LogInformation(
            "{Shop}: {Status} via {Strategy}, {Products} products, {Snapshots} prices, {Rejected} refused, {Failed} failed pages",
            shop.Key, run.Status, run.Strategy, run.ProductsSeen, run.SnapshotsWritten, run.PricesRejected, run.PagesFailed);
        return run;
    }

    private static IEnumerable<CollectorStrategy> StrategiesToTry(Shop shop)
    {
        if (shop.ConfiguredStrategy != CollectorStrategy.Auto)
        {
            return [shop.ConfiguredStrategy];
        }

        return shop.DetectedStrategy is { } detected
            ? AutoOrder.Where(s => s != detected).Prepend(detected)
            : AutoOrder;
    }

}
