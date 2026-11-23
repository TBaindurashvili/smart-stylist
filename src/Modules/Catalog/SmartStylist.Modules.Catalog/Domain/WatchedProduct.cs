namespace SmartStylist.Modules.Catalog.Domain;

/// <summary>
/// A product page Temo wants priced at a shop the collectors cannot read. The browser watch visits it a few times a
/// day in a real Chrome window, and only at shops set up for it (Catalog:Shops:BrowserWatch).
/// </summary>
public sealed class WatchedProduct
{
    public long Id { get; set; }

    public int ShopId { get; set; }

    public Shop Shop { get; set; } = null!;

    public required string Url { get; set; }

    /// <summary>The size Temo wears in it, so the report can say whether that size is in stock.</summary>
    public string? Size { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Why the last visit gave no price, e.g. "Blocked by Akamai". Null after a good visit.</summary>
    public string? LastError { get; set; }
}
