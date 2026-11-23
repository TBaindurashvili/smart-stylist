using SmartStylist.Modules.Catalog.Domain;

namespace SmartStylist.Modules.Catalog.Collection;

internal interface IShopCollector
{
    CollectorStrategy Strategy { get; }

    /// <summary>
    /// Reads up to <see cref="ShopRequest.MaxProducts"/> products. Throws <see cref="CollectorUnusableException"/>
    /// when this strategy clearly does not work for the shop, so Auto can move on to the next one.
    /// </summary>
    Task<CollectorResult> CollectAsync(ShopRequest request, CancellationToken cancellationToken);
}

internal sealed record ShopRequest(
    Uri ShopRoot,
    Uri BaseUrl,
    string Currency,
    string? SitemapUrl,
    string? ProductUrlPattern,
    IReadOnlyList<string> KnownProductUrls,
    int MaxProducts)
{
    /// <summary>Plain-HTTP fetcher for this shop when it differs from the default, e.g. one that can switch to a US egress.</summary>
    public IPageFetcher? Http { get; init; }

    /// <summary>Pause between requests to this shop.</summary>
    public int DelayMs { get; init; }
}

internal sealed record CollectorResult(IReadOnlyList<ScrapedProduct> Products, int FailedRequests);

internal sealed class CollectorUnusableException(string message, Exception? inner = null) : Exception(message, inner);
