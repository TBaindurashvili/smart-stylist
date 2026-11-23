using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Affiliates;

public sealed record AwinFeedResult(AwinFeedInfo Feed, string? ShopKey, int Products, int Prices, string? Error);

/// <summary>
/// Downloads Awin product feeds and saves each product's price for today, like a collector run.
/// An advertiser maps to the shop whose AwinAdvertiserId matches; otherwise a shop "awin-{id}" is created for it.
/// </summary>
public sealed class AwinFeedImportService(
    HttpClient http,
    CatalogDbContext db,
    IOptions<AwinOptions> awinOptions,
    IOptions<CatalogOptions> catalogOptions,
    TimeProvider time,
    ILogger<AwinFeedImportService> logger)
{
    public const string ClientName = "awin";

    private const int SaveEvery = 500;

    private AwinOptions Options => awinOptions.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Options.ApiKey);

    public async Task<IReadOnlyList<AwinFeedInfo>> ListFeedsAsync(CancellationToken cancellationToken)
    {
        RequireApiKey();
        using var response = await http.GetAsync($"{Options.BaseUrl.TrimEnd('/')}/datafeed/list/apikey/{Options.ApiKey}", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        return await AwinFeed.ParseFeedListAsync(reader, cancellationToken);
    }

    /// <summary>Imports the configured feeds, or every joined programme's feeds when none are configured.</summary>
    public async Task<IReadOnlyList<AwinFeedResult>> ImportAsync(CancellationToken cancellationToken)
    {
        var feeds = await ListFeedsAsync(cancellationToken);
        var selected = Options.FeedIds.Count > 0
            ? feeds.Where(f => Options.FeedIds.Contains(f.FeedId)).ToList()
            : feeds.Where(f => f.Joined).ToList();

        var results = new List<AwinFeedResult>();
        foreach (var feed in selected)
        {
            try
            {
                results.Add(await ImportFeedAsync(feed, cancellationToken));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
            {
                logger.LogWarning(ex, "Awin feed {FeedId} ({Advertiser}) failed", feed.FeedId, feed.AdvertiserName);
                results.Add(new AwinFeedResult(feed, null, 0, 0, ex.Message));
            }
        }

        return results;
    }

    public string DownloadUrl(int feedId) =>
        $"{Options.BaseUrl.TrimEnd('/')}/datafeed/download/apikey/{Options.ApiKey}/fid/{feedId}" +
        $"/columns/{Uri.EscapeDataString(string.Join(',', AwinFeed.Columns))}/format/csv/delimiter/%2C/compression/gzip/adultcontent/1/";

    private async Task<AwinFeedResult> ImportFeedAsync(AwinFeedInfo feed, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(DownloadUrl(feed.FeedId), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // Buffered so the gzip check can look at the first bytes; feeds are a few MB compressed.
        var body = new MemoryStream();
        await response.Content.CopyToAsync(body, cancellationToken);
        body.Position = 0;
        await using Stream unzipped = IsGzip(body) ? new GZipStream(body, CompressionMode.Decompress) : body;
        using var reader = new StreamReader(unzipped);
        var products = await AwinFeed.ParseProductsAsync(reader, Options.MaxProductsPerFeed, cancellationToken);
        if (products.Count == 0)
        {
            return new AwinFeedResult(feed, null, 0, 0, "The feed has no products with a price.");
        }

        var shop = await ShopForAsync(feed, products[0], cancellationToken);
        var prices = await SaveAsync(shop, products, cancellationToken);
        logger.LogInformation("Awin feed {FeedId} ({Advertiser}): {Products} products, {Prices} prices for shop {Shop}",
            feed.FeedId, feed.AdvertiserName, products.Count, prices, shop.Key);
        return new AwinFeedResult(feed, shop.Key, products.Count, prices, null);
    }

    private static bool IsGzip(MemoryStream stream) =>
        stream.Length >= 2 && stream.GetBuffer()[0] == 0x1f && stream.GetBuffer()[1] == 0x8b;

    private async Task<Shop> ShopForAsync(AwinFeedInfo feed, AwinProduct sample, CancellationToken cancellationToken)
    {
        var configuredKey = catalogOptions.Value.Shops.FirstOrDefault(s => s.AwinAdvertiserId == feed.AdvertiserId)?.Key;
        var key = configuredKey ?? $"awin-{feed.AdvertiserId}";
        var shop = await db.Shops.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (shop is not null)
        {
            return shop;
        }

        // Not in config: a shop only for prices from the feed. No daily collector runs for it.
        var url = new Uri(sample.Product.Url);
        var currency = sample.Product.Offers[0].Currency;
        shop = new Shop
        {
            Key = key,
            Name = Truncate(string.IsNullOrWhiteSpace(feed.AdvertiserName) ? sample.AdvertiserName : feed.AdvertiserName, 200),
            BaseUrl = $"{url.Scheme}://{url.Host}",
            Currency = currency.Length == 3 ? currency : "USD",
            Enabled = false,
        };
        db.Shops.Add(shop);
        await db.SaveChangesAsync(cancellationToken);
        return shop;
    }

    private async Task<int> SaveAsync(Shop shop, IReadOnlyList<AwinProduct> products, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var existing = await db.Products.Where(p => p.ShopId == shop.Id).ToListAsync(cancellationToken);
        var byId = existing.GroupBy(p => p.ExternalId).ToDictionary(g => g.Key, g => g.First());
        var byUrl = existing.GroupBy(p => p.Url).ToDictionary(g => g.Key, g => g.First());
        var todays = (await db.PriceSnapshots.Where(s => s.Product.ShopId == shop.Id && s.ObservedOn == today).ToListAsync(cancellationToken))
            .ToDictionary(s => (s.ProductId, s.VariantKey));

        var prices = 0;
        var pending = 0;
        foreach (var item in products)
        {
            var scraped = item.Product;
            var externalId = Truncate(scraped.ExternalId, 500);
            var url = Truncate(scraped.Url, 2000);

            // The extension or a sale email may have saved the same product under its page URL.
            var product = byId.GetValueOrDefault(externalId) ?? byUrl.GetValueOrDefault(url);
            if (product is null)
            {
                product = new Product { ShopId = shop.Id, ExternalId = externalId, Url = url, Title = "", FirstSeenAt = now };
                db.Products.Add(product);
                byId[externalId] = product;
            }

            product.Url = url;
            product.Title = Truncate(scraped.Title, 500);
            product.Brand = scraped.Brand is null ? product.Brand : Truncate(scraped.Brand, 200);
            product.ImageUrl = scraped.ImageUrl is { Length: <= 2000 } ? scraped.ImageUrl : product.ImageUrl;
            product.LastSeenAt = now;

            foreach (var offer in scraped.Offers.GroupBy(o => o.VariantKey).Select(g => g.First()))
            {
                var variantKey = Truncate(offer.VariantKey, 200);
                if (product.Id == 0 || !todays.TryGetValue((product.Id, variantKey), out var snapshot))
                {
                    snapshot = new PriceSnapshot { Product = product, VariantKey = variantKey, ObservedOn = today, Currency = shop.Currency };
                    db.PriceSnapshots.Add(snapshot);
                }

                snapshot.SizeLabel = offer.SizeLabel is null ? null : Truncate(offer.SizeLabel, 100);
                snapshot.Price = offer.Price;
                snapshot.ListPrice = offer.ListPrice;
                snapshot.Currency = offer.Currency.Length == 3 ? offer.Currency : shop.Currency;
                snapshot.InStock = offer.InStock;
                snapshot.CapturedAt = now;
                snapshot.Source = PriceSource.AffiliateFeed;
                prices++;
            }

            if (++pending >= SaveEvery)
            {
                await db.SaveChangesAsync(cancellationToken);
                pending = 0;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return prices;
    }

    private void RequireApiKey()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Awin feed import is off: set Awin:ApiKey (dotnet user-secrets set \"Awin:ApiKey\" <key>, or the Awin__ApiKey environment variable). " +
                "The key is in Awin under Toolbox → Create-a-Feed.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
