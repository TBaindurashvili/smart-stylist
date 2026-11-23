using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Collection;

public sealed record WatchResult(string ShopKey, int Pages, int Saved, string? StoppedBecause);

/// <summary>
/// Opens watched product pages in the browser (a visible Chrome with its own profile on the home PC) a few times a day,
/// for shops such as J.Crew and lululemon that only answer a real browser. Only watched SKUs, never a whole-shop crawl.
/// A bot-check or CAPTCHA page stops that shop for the run; it is never solved.
/// </summary>
public sealed class WatchedPagesService
{
    private readonly CatalogDbContext _db;
    private readonly IPageFetcher _browser;
    private readonly PageCaptureService _capture;
    private readonly IOptions<CatalogOptions> _options;
    private readonly ILogger<WatchedPagesService> _logger;

    internal WatchedPagesService(CatalogDbContext db, IPageFetcher browser, PageCaptureService capture, IOptions<CatalogOptions> options,
        ILogger<WatchedPagesService> logger)
    {
        _db = db;
        _browser = browser;
        _capture = capture;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WatchResult>> RunAsync(IReadOnlyCollection<string> shopKeys, CancellationToken cancellationToken)
    {
        var watch = _options.Value.Watch;
        var shops = _options.Value.Shops
            .Where(s => s.WatchInBrowser && (shopKeys.Count == 0 || shopKeys.Contains(s.Key, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var results = new List<WatchResult>();
        foreach (var shop in shops)
        {
            var saved = await _db.Products
                .Where(p => p.Shop.Key == shop.Key && p.Snapshots.Any(s => s.Source == PriceSource.Extension))
                .OrderByDescending(p => p.LastSeenAt)
                .Select(p => p.Url)
                .ToListAsync(cancellationToken);
            var urls = shop.WatchUrls.Concat(saved).Distinct(StringComparer.OrdinalIgnoreCase).Take(watch.MaxPagesPerShop).ToList();

            int pages = 0, stored = 0;
            string? stopped = null;
            foreach (var url in urls)
            {
                if (pages > 0)
                {
                    await Task.Delay(watch.DelayMs, cancellationToken);
                }

                pages++;
                try
                {
                    var html = await _browser.GetPageAsync(new Uri(url), cancellationToken);
                    var result = await _capture.CaptureAsync(new PageCapture(url, html), PriceSource.Crawl, cancellationToken);
                    if (result.Status == PageCaptureStatus.Saved)
                    {
                        stored++;
                    }
                }
                catch (BlockedByBotWallException ex)
                {
                    stopped = ex.Message;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Microsoft.Playwright.PlaywrightException && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Watched page {Url} failed: {Error}", url, SitemapReader.FirstLine(ex.Message));
                }
            }

            if (_browser is BrowserPageFetcher browser)
            {
                await browser.ResetAsync();
            }

            _logger.LogInformation("{Shop}: watched {Pages} pages, saved {Saved}{Stopped}", shop.Key, pages, stored, stopped is null ? "" : $", stopped: {stopped}");
            results.Add(new WatchResult(shop.Key, pages, stored, stopped));
        }

        return results;
    }
}

/// <summary>Hangfire entry point for watched pages, a few times a day (Catalog:Watch:Cron).</summary>
public sealed class WatchPagesJob(WatchedPagesService watch)
{
    public const string RecurringJobId = "watch-pages";

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
    public Task RunAsync(CancellationToken cancellationToken) => watch.RunAsync([], cancellationToken);
}
