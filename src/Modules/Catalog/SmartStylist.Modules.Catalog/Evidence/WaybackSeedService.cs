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
    private readonly PriceSanityCheck _sanity;
    private readonly TimeProvider _time;
    private readonly IOptions<CatalogOptions> _options;
    private readonly ILogger<WaybackSeedService> _logger;

    internal WaybackSeedService(HttpClient http, CatalogDbContext db, PriceSanityCheck sanity, TimeProvider time, IOptions<CatalogOptions> options,
        ILogger<WaybackSeedService> logger)
    {
        _http = http;
        _db = db;
        _sanity = sanity;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Seeds the long-running products in Catalog:CoreProducts. New products have no archived copies yet; these
    /// have been sold (and archived) for years, so they give real history from day one.
    /// </summary>
    public async Task<WaybackSeedResult> SeedCoreAsync(int copiesPerProduct, CancellationToken cancellationToken)
    {
        var total = new WaybackSeedResult(0, 0, 0, 0, []);
        foreach (var core in _options.Value.CoreProducts)
        {
            if (!Uri.TryCreate(core.Url, UriKind.Absolute, out var url))
            {
                total = total with { Notes = [.. total.Notes, $"core product '{core.Url}': not a page address"] };
                continue;
            }

            var also = core.ArchiveUrls.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var a) ? a : null).OfType<Uri>().ToList();
            total = Add(total, await SeedUrlAsync(url, core.Shop, copiesPerProduct, cancellationToken, also, core.Title));
        }

        return total;
    }

    private static WaybackSeedResult Add(WaybackSeedResult total, WaybackSeedResult one) => total with
    {
        Products = total.Products + one.Products,
        PagesRead = total.PagesRead + one.PagesRead,
        PagesWithPrice = total.PagesWithPrice + one.PagesWithPrice,
        PricesAdded = total.PricesAdded + one.PricesAdded,
        Notes = [.. total.Notes, .. one.Notes],
    };

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
            .OrderByDescending(p => p.LastSeenAt)
            .Take(maxProducts)
            .Select(p => new { p.Id, p.Url })
            .ToListAsync(cancellationToken);

        var total = new WaybackSeedResult(0, 0, 0, 0, []);
        foreach (var p in products)
        {
            total = Add(total, await SeedUrlAsync(new Uri(p.Url), null, copiesPerProduct, cancellationToken));
        }

        return total;
    }

    /// <summary>
    /// Seeds one product page. The product is added when the app does not know it yet.
    /// <paramref name="alsoArchivedAs"/> are older addresses of the same product; their copies count for it too.
    /// </summary>
    public async Task<WaybackSeedResult> SeedUrlAsync(Uri pageUrl, string? shopKey, int copiesPerProduct, CancellationToken cancellationToken,
        IReadOnlyList<Uri>? alsoArchivedAs = null, string? title = null)
    {
        var shop = await OutsideEvidenceWriter.FindShopAsync(_db, pageUrl, shopKey, cancellationToken);
        if (shop is null)
        {
            return new WaybackSeedResult(0, 0, 0, 0, [$"{pageUrl.Host}: not one of the shops in catalogue.json"]);
        }

        var now = _time.GetUtcNow();
        var lookback = _options.Value.ArchiveLookbackDays;
        var since = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-lookback);
        var copies = new List<ArchivedCopy>();
        string? lookupError = null;
        foreach (var address in (alsoArchivedAs ?? []).Prepend(pageUrl))
        {
            var (found, error) = await ListCopiesAsync(address, since, cancellationToken);
            copies.AddRange(found);
            lookupError ??= error;
        }

        copies = copies.GroupBy(c => c.Day).Select(g => g.First()).OrderBy(c => c.Day).ToList();
        if (copies.Count == 0)
        {
            return new WaybackSeedResult(0, 0, 0, 0, [lookupError is null
                ? $"{pageUrl}: no archived copies in the last {lookback} days"
                : $"{pageUrl}: the archive lookup failed ({lookupError}); try again later"]);
        }

        Product? product = null;
        int read = 0, priced = 0, added = 0, refused = 0;
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

            ScrapedProduct? scraped;
            try
            {
                scraped = JsonLdProductParser.Parse(html, pageUrl, shop.Currency);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
            {
                // Old copies carry old page layouts; one odd copy must not stop the rest.
                _logger.LogInformation("Wayback copy {Timestamp} of {Url} could not be read: {Error}", copy.Timestamp, pageUrl, ex.Message);
                continue;
            }

            if (scraped is null || scraped.Offers.Count == 0)
            {
                continue;
            }

            priced++;
            product ??= await OutsideEvidenceWriter.FindOrAddProductAsync(_db, shop, pageUrl.ToString(), title ?? scraped.Title, now, cancellationToken);
            var (written, rejected) = await OutsideEvidenceWriter.AddAsync(_db, _sanity, product, copy.Day, scraped.Offers, shop.Currency,
                PriceSource.Archive, now, cancellationToken);
            added += written;
            refused += rejected;
        }

        var note = $"{pageUrl}: {copies.Count} archived days, read {read}, {priced} with a price, {added} prices added" +
                   (refused > 0 ? $", {refused} refused by the sanity check" : "");
        return new WaybackSeedResult(product is null ? 0 : 1, read, priced, added, [note]);
    }

    internal sealed record ArchivedCopy(string Timestamp, string Original, DateOnly Day);

    /// <summary>
    /// One archived copy per day (status 200) of exactly this product page since the given day, oldest first.
    /// Copies saved with a query string (?variant=123, ?_pos=4) are the same page and count; longer paths that only
    /// start the same way (/p/1818106920 for /p/181810692) do not. Shopify and Patagonia pages are mostly archived
    /// with a query string, so an exact-only lookup misses most of their history.
    /// </summary>
    internal async Task<(IReadOnlyList<ArchivedCopy> Copies, string? Error)> ListCopiesAsync(Uri pageUrl, DateOnly since,
        CancellationToken cancellationToken)
    {
        var target = pageUrl.GetLeftPart(UriPartial.Path);
        var cdx = $"{Archive}/cdx/search/cdx?url={Uri.EscapeDataString(target)}&matchType=prefix&output=json&fl=timestamp,original,statuscode" +
                  $"&filter=statuscode:200&collapse=timestamp:8&from={since:yyyyMMdd}&limit=2000";
        string json;
        try
        {
            await Task.Delay(_options.Value.ArchiveDelayMs, cancellationToken);
            var response = await _http.GetAsync(cdx, cancellationToken);
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                // archive.org sheds load with 503/429; one patient retry, then give up for today.
                response.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(_options.Value.ArchiveDelayMs * 8L), cancellationToken);
                response = await _http.GetAsync(cdx, cancellationToken);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                {
                    return ([], null);
                }

                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Wayback lookup for {Url} failed: {Error}", pageUrl, ex.Message);
            return ([], ex.StatusCode is { } status ? $"HTTP {(int)status}" : SitemapReader.FirstLine(ex.Message));
        }

        var copies = ParseCdx(json)
            .Where(c => SamePage(c.Original, pageUrl))
            .GroupBy(c => c.Day)
            .Select(g => g.First())
            .ToList();
        return (copies, null);
    }

    /// <summary>Same host (with or without www) and same path; the query string and scheme do not matter.</summary>
    internal static bool SamePage(string archived, Uri page)
    {
        if (!Uri.TryCreate(archived, UriKind.Absolute, out var url))
        {
            return false;
        }

        static string Host(Uri u) => u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;

        return string.Equals(Host(url), Host(page), StringComparison.OrdinalIgnoreCase)
               && string.Equals(url.AbsolutePath.TrimEnd('/'), page.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
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
