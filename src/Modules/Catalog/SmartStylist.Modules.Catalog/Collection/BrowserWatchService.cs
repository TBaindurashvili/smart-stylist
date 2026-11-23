using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Newsletters;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Collection;

public sealed record WatchVisit(string Shop, string Url, string Outcome);

/// <summary>
/// The real-Chrome tier, used only for watched products at shops set up for it (Catalog:Shops:BrowserWatch, today
/// J.Crew and lululemon): their pages load in Chrome but not over plain HTTP. Each watched page is visited at most
/// once per Catalog:BrowserWatch:MinHoursBetweenVisits, one page at a time at a person's pace. A bot check stops the
/// shop for the run: it is never solved, and nothing is logged into.
/// </summary>
public sealed class BrowserWatchService
{
    private readonly CatalogDbContext _db;
    private readonly BrowserPageFetcher _browser;
    private readonly PageCaptureService _capture;
    private readonly IOptions<CatalogOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BrowserWatchService> _logger;

    internal BrowserWatchService(CatalogDbContext db, BrowserPageFetcher browser, PageCaptureService capture, IOptions<CatalogOptions> options,
        TimeProvider time, ILogger<BrowserWatchService> logger)
    {
        _db = db;
        _browser = browser;
        _capture = capture;
        _options = options;
        _time = time;
        _logger = logger;
    }

    private IReadOnlyList<ShopOptions> WatchShops => _options.Value.Shops.Where(s => s.BrowserWatch && !s.Dropped).ToList();

    /// <summary>Adds a product page to the watch. Only pages of shops with BrowserWatch on are accepted.</summary>
    public async Task<string> AddAsync(Uri url, string? size, CancellationToken cancellationToken)
    {
        var config = WatchShops.FirstOrDefault(s => new ShopDomains(s.Key, s.BaseUrl, []).IsShopPage(url.Host));
        if (config is null)
        {
            var allowed = string.Join(", ", WatchShops.Select(s => s.Key));
            return $"{url.Host} is not a browser-watch shop (those are: {(allowed.Length == 0 ? "none" : allowed)}). " +
                   "Other shops are priced by the daily collection, emails and feeds.";
        }

        var shop = await _db.Shops.SingleAsync(s => s.Key == config.Key, cancellationToken);
        var address = url.GetLeftPart(UriPartial.Path);
        var watched = await _db.WatchedProducts.FirstOrDefaultAsync(w => w.ShopId == shop.Id && w.Url == address, cancellationToken);
        if (watched is null)
        {
            watched = new WatchedProduct { ShopId = shop.Id, Url = address, AddedAt = _time.GetUtcNow() };
            _db.WatchedProducts.Add(watched);
        }

        watched.Size = string.IsNullOrWhiteSpace(size) ? watched.Size : size.Trim();
        await _db.SaveChangesAsync(cancellationToken);
        return $"Watching {address} at {shop.Name}{(watched.Size is null ? "" : $", size {watched.Size}")}.";
    }

    public async Task<bool> RemoveAsync(Uri url, CancellationToken cancellationToken)
    {
        var address = url.GetLeftPart(UriPartial.Path);
        var watched = await _db.WatchedProducts.Where(w => w.Url == address).ToListAsync(cancellationToken);
        _db.WatchedProducts.RemoveRange(watched);
        await _db.SaveChangesAsync(cancellationToken);
        return watched.Count > 0;
    }

    public async Task<IReadOnlyList<WatchedProduct>> ListAsync(CancellationToken cancellationToken) =>
        await _db.WatchedProducts.AsNoTracking().Include(w => w.Shop).OrderBy(w => w.Shop.Key).ThenBy(w => w.Url).ToListAsync(cancellationToken);

    /// <summary>Visits the watched pages that are due, shop by shop.</summary>
    public async Task<IReadOnlyList<WatchVisit>> RunAsync(CancellationToken cancellationToken)
    {
        var watch = _options.Value.BrowserWatch;
        var now = _time.GetUtcNow();
        var dueBefore = now.AddHours(-watch.MinHoursBetweenVisits);
        var visits = new List<WatchVisit>();

        foreach (var config in WatchShops)
        {
            var due = await _db.WatchedProducts
                .Where(w => w.Shop.Key == config.Key && (w.LastCheckedAt == null || w.LastCheckedAt <= dueBefore))
                .OrderBy(w => w.LastCheckedAt)
                .Take(watch.MaxPagesPerShop)
                .ToListAsync(cancellationToken);

            try
            {
                foreach (var watched in due)
                {
                    var outcome = await VisitAsync(watched, cancellationToken);
                    visits.Add(new WatchVisit(config.Key, watched.Url, outcome.Text));
                    if (outcome.Blocked)
                    {
                        // Leave the shop alone for this run; the next pages would meet the same wall.
                        break;
                    }

                    await Task.Delay(_options.Value.RequestDelayMs, cancellationToken);
                }
            }
            finally
            {
                await _browser.ResetAsync();
            }
        }

        return visits;
    }

    private async Task<(string Text, bool Blocked)> VisitAsync(WatchedProduct watched, CancellationToken cancellationToken)
    {
        watched.LastCheckedAt = _time.GetUtcNow();
        (string Text, bool Blocked) outcome;
        try
        {
            var html = await _browser.GetPageAsync(new Uri(watched.Url), cancellationToken);
            var result = await _capture.CaptureAsync(new PageCapture(watched.Url, html), PriceSource.Crawl, cancellationToken);
            outcome = result.Status switch
            {
                PageCaptureStatus.Saved => ($"{result.Price:0.00} {result.Currency}" +
                                            (result.ListPrice is { } was ? $" (was {was:0.00})" : "") + SizeNote(watched.Size, result), false),
                PageCaptureStatus.Refused => ("the page's prices failed the sanity check", false),
                _ => ("no price on the page", false),
            };
            watched.LastError = result.Status == PageCaptureStatus.Saved ? null : outcome.Text;
        }
        catch (BlockedByBotWallException ex)
        {
            outcome = ($"blocked by {ex.Vendor}; left alone", true);
            watched.LastError = outcome.Text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Microsoft.Playwright.PlaywrightException or CollectorUnusableException
                                   && !cancellationToken.IsCancellationRequested)
        {
            outcome = (SitemapReader.FirstLine(ex.Message), ex is CollectorUnusableException);
            watched.LastError = outcome.Text.Length > 1000 ? outcome.Text[..1000] : outcome.Text;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Browser watch {Url}: {Outcome}", watched.Url, outcome.Text);
        return outcome;
    }

    private static string SizeNote(string? size, PageCaptureResult result)
    {
        if (size is null || result.Sizes is null)
        {
            return "";
        }

        var match = result.Sizes.FirstOrDefault(s => string.Equals(s.Label, size, StringComparison.OrdinalIgnoreCase));
        return match is null ? $", size {size} not listed"
            : $", size {size} {match.Price:0.00} {(match.InStock == false ? "sold out" : match.InStock == true ? "in stock" : "stock unknown")}";
    }
}

/// <summary>Hangfire entry point for the browser watch.</summary>
public sealed class BrowserWatchJob(BrowserWatchService watch)
{
    public const string RecurringJobId = "browser-watch";

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public Task RunAsync(CancellationToken cancellationToken) => watch.RunAsync(cancellationToken);
}
