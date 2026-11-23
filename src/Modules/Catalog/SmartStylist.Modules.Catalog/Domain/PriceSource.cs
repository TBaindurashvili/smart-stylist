namespace SmartStylist.Modules.Catalog.Domain;

/// <summary>Where a price snapshot came from.</summary>
public enum PriceSource
{
    /// <summary>Read from the shop's own site or feed by a collector.</summary>
    Crawl,

    /// <summary>Read from a sale email the shop sent.</summary>
    Newsletter,

    /// <summary>Read from a product page the user opened, by the browser extension.</summary>
    Extension,

    /// <summary>Read from an affiliate network's product feed, such as Awin.</summary>
    AffiliateFeed,

    /// <summary>Outside evidence: an old copy of the product page in the Wayback Machine (archive.org).</summary>
    Archive,

    /// <summary>
    /// Outside evidence: a dated price written down elsewhere (the stylist's Recommended and near-miss tables),
    /// imported from a CSV file or pasted Markdown tables. The least certain source: nobody re-checked the page.
    /// </summary>
    ManualHistory,
}

public static class PriceSourceExtensions
{
    /// <summary>
    /// Prices the app did not see itself on the shop. They help judge a sale before two weeks of own history exist,
    /// but a verdict that leans on them is marked as less certain.
    /// </summary>
    public static bool IsOutsideEvidence(this PriceSource source) => source is PriceSource.Archive or PriceSource.ManualHistory;

    /// <summary>
    /// How much a price from this source is trusted: the shop's own data 1, an archived copy of the shop's page 0.6,
    /// a written-down price 0.4 (the page was never re-read).
    /// </summary>
    public static decimal Confidence(this PriceSource source) => source switch
    {
        PriceSource.Archive => 0.6m,
        PriceSource.ManualHistory => 0.4m,
        _ => 1m,
    };
}
