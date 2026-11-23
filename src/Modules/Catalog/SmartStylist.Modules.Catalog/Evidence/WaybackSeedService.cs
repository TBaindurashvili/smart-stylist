using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Evidence;

public sealed record WaybackSeedResult(int Products, int PagesRead, int PagesWithPrice, int PricesAdded, IReadOnlyList<string> Notes);

/// <summary>
/// Seeds price history from old copies of product pages in the Wayback Machine (archive.org), so a sale can be
/// judged before two weeks of own prices exist. Prices are stored as outside evidence (PriceSource.Archive).
/// Reads the archive's own copies only; the shop is never contacted.
/// </summary>
public sealed class WaybackSeedService
{
    public const string ClientName = "wayback";

    private const string Archive = "https://web.archive.org";

    private readonly HttpClient _http;
    private readonly CatalogDbContext _db;
    private readonly TimeProvider _time;
    private readonly IOptions<CatalogOptions> _options;
    private readonly ILogger<WaybackSeedService> _logger;

    internal WaybackSeedService(HttpClient http, CatalogDbContext db, TimeProvider time, IOptions<CatalogOptions> options, ILogger<WaybackSeedService> logger)
    {
        _http = http;
        _db = db;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>Seeds the products of these shops (all shops when empty) that have fewer than two weeks of own prices.</summary>
    public async Task<WaybackSeedResult> SeedShopsAsync(IReadOnlyCollection<string> shopKeys, int maxProducts, int copiesPerProduct,
        CancellationToken cancellationToken)
    {
        var query = _db.Products.AsNoTracking().Where(p => p.Url.StartsWith("http"));
        if (shopKeys.Count > 0)
        {
            query = query.Where(p => shopKeys.Contains(p.Shop.Key));
        }

        var products = await query
            .Where(p => p.Snapshots.Where(s => s.Source != PriceSource.Archive && s.Source != PriceSource.ManualHistory)
                .Select(s => s.ObservedOn).Distinct().Count() < Reporting.DealsReport.MinHistoryDays)
            .OrderBy(p => p.FirstSeenAt) // long-running products have archived copies; brand-new ones do not
            .Take(maxProducts)
            .Select(p => new { p.Id, p.Url })
            .ToListAsync(cancellationToken);

        var total = new WaybackSeedResult(0, 0, 0, 0, []);
        foreach (var p in products)
        {
            var one = await SeedUrlAsync(new Uri(p.Url), null, copiesPerProduct, cancellationToken);
            total = total with
            {
                Products = total.Products + one.Products,
                PagesRead = total.PagesRead + one.PagesRead,
                PagesWithPrice = total.PagesWithPrice + one.PagesWithPrice,
                PricesAdded = total.PricesAdded + one.PricesAdded,
                Notes = [.. total.Notes, .. one.Notes],
            };
        }

        return total;
    }

    /// <summary>Seeds the long-running core products listed in Catalog:CoreProducts, by their exact page address.</summary>
    public async Task<WaybackSeedResult> SeedCoreAsync(int copiesPerProduct, CancellationToken cancellationToken)
    {
        var total = new WaybackSeedResult(0, 0, 0, 0, []);
        foreach (var url in _options.Value.CoreProducts)
        {
            var one = await SeedUrlAsync(new Uri(url), null, copiesPerProduct, cancellationToken);
            total = new WaybackSeedResult(total.Products + one.Products, total.PagesRead + one.PagesRead,
                total.PagesWithPrice + one.PagesWithPrice, total.PricesAdded + one.PricesAdded, [.. total.Notes, .. one.Notes]);
        }

        return total;
    }

    /// <summary>Seeds one product page. The product is added when the app does not know it yet.</summary>
    public async Task<WaybackSeedResult> SeedUrlAsync(Uri pageUrl, string? shopKey, int copiesPerProduct, CancellationToken cancellationToken)
    {
        var shop = await OutsideEvidenceWriter.FindShopAsync(_db, pageUrl, shopKey, cancellationToken);
        if (shop is null)
        {
            return new WaybackSeedResult(0, 0, 0, 0, [$"{pageUrl.Host}: not one of the shops in appsettings.json"]);
        }

        var now = _time.GetUtcNow();
        var since = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-365);
        var copies = await ListCopiesAsync(pageUrl, since, cancellationToken);
        if (copies.Count == 0)
        {
            return new WaybackSeedResult(0, 0, 0, 0, [$"{pageUrl}: no archived copies in the last year"]);
        }

        Product? product = null;
        int read = 0, priced = 0, added = 0;
        foreach (var copy in Spread(copies, copiesPerProduct))
        {
            await Task.Delay(_options.Value.ArchiveDelayMs, cancellationToken);
            read++;
            string html;
            try
            {
                html = await _http.GetStringAsync($"{Archive}/web/{copy.Timestamp}id_/{copy.Original}", cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogInformation("Wayback copy {Timestamp} of {Url} failed: {Error}", copy.Timestamp, pageUrl, ex.Message);
                continue;
            }

            var scraped = JsonLdProductParser.Parse(html, pageUrl, shop.Currency);
            if (scraped is null || scraped.Offers.Count == 0)
            {
                continue;
            }

            priced++;
            product ??= await OutsideEvidenceWriter.FindOrAddProductAsync(_db, shop, pageUrl.ToString(), scraped.Title, now, cancellationToken);
            added += await OutsideEvidenceWriter.AddAsync(_db, product, copy.Day, scraped.Offers, shop.Currency, PriceSource.Archive, now, cancellationToken);
        }

        var note = $"{pageUrl}: {copies.Count} archived copies, read {read}, {priced} with a price, {added} prices added";
        return new WaybackSeedResult(product is null ? 0 : 1, read, priced, added, [note]);
    }

    internal sealed record ArchivedCopy(string Timestamp, string Original, DateOnly Day);

    /// <summary>One archived copy per day (status 200) since the given day, oldest first.</summary>
    internal async Task<IReadOnlyList<ArchivedCopy>> ListCopiesAsync(Uri pageUrl, DateOnly since, CancellationToken cancellationToken)
    {
        var target = pageUrl.GetLeftPart(UriPartial.Path);
        var cdx = $"{Archive}/cdx/search/cdx?url={Uri.EscapeDataString(target)}&matchType=exact&output=json&fl=timestamp,original,statuscode" +
                  $"&filter=statuscode:200&collapse=timestamp:8&from={since:yyyyMMdd}&limit=400";
        string json;
        try
        {
            using var response = await _http.GetAsync(cdx, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Wayback lookup for {Url} failed: {Error}", pageUrl, ex.Message);
            return [];
        }

        return ParseCdx(json);
    }

    internal static IReadOnlyList<ArchivedCopy> ParseCdx(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var rows = JsonSerializer.Deserialize<string[][]>(json) ?? [];
        var copies = new List<ArchivedCopy>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Length < 2 || row[0].Length < 8
                || !DateOnly.TryParseExact(row[0][..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                continue;
            }

            copies.Add(new ArchivedCopy(row[0], row[1], day));
        }

        return copies.OrderBy(c => c.Day).ToList();
    }

    /// <summary>Up to <paramref name="count"/> copies spread over the whole period, always including the newest.</summary>
    internal static IReadOnlyList<ArchivedCopy> Spread(IReadOnlyList<ArchivedCopy> copies, int count)
    {
        if (count <= 0 || copies.Count == 0)
        {
            return [];
        }

        if (copies.Count <= count)
        {
            return copies;
        }

        var picked = new List<ArchivedCopy>();
        for (var i = 0; i < count; i++)
        {
            var index = count == 1 ? copies.Count - 1 : (int)Math.Round(i * (copies.Count - 1) / (double)(count - 1));
            if (picked.Count == 0 || picked[^1] != copies[index])
            {
                picked.Add(copies[index]);
            }
        }

        return picked;
    }
}
