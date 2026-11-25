namespace SmartStylist.Modules.Catalog;

/// <summary>Who a brand dresses.</summary>
public enum Audience
{
    Men,
    Women,
}

/// <summary>Where an affiliate programme application stands.</summary>
public enum ProgrammeStatus
{
    Pending,
    Joined,
    Declined,
}

/// <summary>
/// One brand in the quality catalogue. Being listed means the brand makes clothes worth owning; cheap is never a
/// reason to be here. The brand's own store and the retailers that carry it are entries in Catalog:Shops.
/// </summary>
public sealed class BrandOptions
{
    /// <summary>Stable short key, e.g. "levis". Often the same as its own store's shop key.</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public List<Audience> For { get; set; } = [];

    /// <summary>What the brand is good at, e.g. "denim", "knitwear", "boots".</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>Style words the style quiz will match on, e.g. "classic", "minimal", "outdoor", "workwear".</summary>
    public List<string> Styles { get; set; } = [];

    /// <summary>One line on why the quality is real, for the person reading a pick.</summary>
    public string? Why { get; set; }

    /// <summary>Other names products and emails use, e.g. "Levi Strauss" or "Arc'teryx Veilance".</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Catalog:Shops keys that sell the brand: its own store first, then retailers.</summary>
    public List<string> Shops { get; set; } = [];

    /// <summary>Awin programmes applied for, often a country store (Lacoste UK). A joined one feeds prices daily.</summary>
    public List<AwinProgrammeOptions> Awin { get; set; } = [];
}

public sealed class AwinProgrammeOptions
{
    /// <summary>The programme's name on Awin, e.g. "Lacoste UK".</summary>
    public required string Programme { get; set; }

    public ProgrammeStatus Status { get; set; } = ProgrammeStatus.Pending;

    /// <summary>Awin advertiser id, once known. Its feed then lands in shop "awin-{id}" or a shop with the same AwinAdvertiserId.</summary>
    public int? AdvertiserId { get; set; }
}
