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

    /// <summary>Outside evidence: a dated price written down elsewhere (the stylist's notes), imported from a CSV file.</summary>
    Manual,
}

public static class PriceSourceExtensions
{
    /// <summary>
    /// Prices the app did not see itself on the shop. They help judge a sale before two weeks of own history exist,
    /// but a verdict that leans on them is marked as less certain.
    /// </summary>
    public static bool IsOutsideEvidence(this PriceSource source) => source is PriceSource.Archive or PriceSource.Manual;
}
