using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Newsletters;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>A product page as seen in the user's own browser. Html only needs the page's structured data.</summary>
public sealed record PageCapture(string Url, string Html);

public enum PageCaptureStatus
{
    Saved,
    UnknownShop,
    NoPrice,

    /// <summary>The page had prices, but the sanity check refused all of them (e.g. a sold-out placeholder).</summary>
    Refused,
}

public sealed record CapturedSize(string Label, decimal Price, bool? InStock);

/// <summary>What the browser extension shows for a page. LowestPrice and DaysTracked cover every source of prices.</summary>
public sealed record PageCaptureResult(
    PageCaptureStatus Status,
    string? Shop = null,
    string? Title = null,
    decimal? Price = null,
    decimal? ListPrice = null,
    string? Currency = null,
    decimal? LowestPrice = null,
    int DaysTracked = 0,
    IReadOnlyList<CapturedSize>? Sizes = null);

/// <summary>
/// Stores the price of a product page the user opened. The shop sees a normal visitor, so this also works
/// for shops that block the collectors. The page is parsed the same way a crawled page is.
/// </summary>
public sealed class PageCaptureService(CatalogDbContext db, PriceWriter writer)
{
    public Task<PageCaptureResult> CaptureAsync(PageCapture capture, CancellationToken cancellationToken) =>
        CaptureAsync(capture, PriceSource.Extension, cancellationToken);

    /// <param name="source">Extension for pages the user opened; Crawl for the browser watch, which reads the same page itself.</param>
    internal async Task<PageCaptureResult> CaptureAsync(PageCapture capture, PriceSource source, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(capture.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return new PageCaptureResult(PageCaptureStatus.UnknownShop);
        }

        var shops = await db.Shops.ToListAsync(cancellationToken);
        var shop = shops.FirstOrDefault(s => new ShopDomains(s.Key, s.BaseUrl, []).IsShopPage(url.Host));
        if (shop is null)
        {
            return new PageCaptureResult(PageCaptureStatus.UnknownShop);
        }

        var scraped = JsonLdProductParser.Parse(capture.Html, url, shop.Currency);
        if (scraped is null)
        {
            return new PageCaptureResult(PageCaptureStatus.NoPrice, shop.Name);
        }

        var written = await writer.WriteAsync(shop, [scraped], source, cancellationToken);
        if (written.Saved is not [var (product, kept)])
        {
            return new PageCaptureResult(PageCaptureStatus.Refused, shop.Name, scraped.Title);
        }

        var cheapest = kept.MinBy(o => o.Price)!;
        var daily = await db.PriceSnapshots
            .Where(s => s.ProductId == product.Id && s.Currency == cheapest.Currency)
            .GroupBy(s => s.ObservedOn)
            .Select(g => g.Min(s => s.Price))
            .ToListAsync(cancellationToken);

        return new PageCaptureResult(
            PageCaptureStatus.Saved,
            shop.Name,
            scraped.Title,
            cheapest.Price,
            cheapest.ListPrice,
            cheapest.Currency,
            daily.Min(),
            daily.Count,
            kept.Where(o => o.SizeLabel is not null).Select(o => new CapturedSize(o.SizeLabel!, o.Price, o.InStock)).ToList());
    }
}
