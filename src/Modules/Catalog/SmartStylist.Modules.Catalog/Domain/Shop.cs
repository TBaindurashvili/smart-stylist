namespace SmartStylist.Modules.Catalog.Domain;

public sealed class Shop
{
    public int Id { get; set; }

    /// <summary>Stable short key used in config, jobs and URLs, e.g. "levis".</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public required string BaseUrl { get; set; }

    /// <summary>ISO 4217 currency assumed when the shop data does not state one.</summary>
    public required string Currency { get; set; }

    public CollectorStrategy ConfiguredStrategy { get; set; } = CollectorStrategy.Auto;

    /// <summary>The strategy that last worked when <see cref="ConfiguredStrategy"/> is Auto.</summary>
    public CollectorStrategy? DetectedStrategy { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The platform found by the detector; picks the adapter.</summary>
    public ShopPlatform? Platform { get; set; }

    public string? PlatformEvidence { get; set; }

    public DateTimeOffset? PlatformCheckedAt { get; set; }

    /// <summary>Ships to a forwarder address without cancelling. Null = not known yet.</summary>
    public bool? AcceptsForwarder { get; set; }

    /// <summary>Accepts a card issued outside the shop's country. Null = not known yet.</summary>
    public bool? AcceptsForeignCard { get; set; }

    /// <summary>Checkout tips learned from real orders, e.g. "no digits in the name field".</summary>
    public string? CheckoutNote { get; set; }

    /// <summary>
    /// Not a place to buy (e.g. it cancels forwarder orders): never collected, its sale emails are not priced and
    /// its products are left off the deals page.
    /// </summary>
    public bool Dropped { get; set; }
}
