namespace SmartStylist.Modules.Wardrobe.Domain;

/// <summary>Whose clothes a person shops for.</summary>
public enum ShopsFor
{
    Men,
    Women,
    Both,
}

/// <summary>
/// One person's style: what they like and avoid, what they never want, and their usual sizes.
/// Words are the quality catalogue's Styles and Categories words (plus free no-go words), so a brand or item can be
/// matched against it. Everything personal (fits, picks) hangs off a profile.
/// </summary>
public sealed class StyleProfile
{
    public long Id { get; set; }

    public required string Name { get; set; }

    public ShopsFor ShopsFor { get; set; }

    /// <summary>Style words that feel like them, e.g. "minimal", "classic". Detected from loved brands, then edited.</summary>
    public List<string> LikedStyles { get; set; } = [];

    /// <summary>Style words that are not them, e.g. "street".</summary>
    public List<string> AvoidedStyles { get; set; } = [];

    /// <summary>Kinds of item they want to buy, e.g. "knitwear", "boots".</summary>
    public List<string> WantedCategories { get; set; } = [];

    /// <summary>Never suggest these: categories ("suits") or any word in an item ("logo", "polyester", "skinny").</summary>
    public List<string> NoGo { get; set; } = [];

    /// <summary>Catalogue brands they loved in the quiz.</summary>
    public List<string> LovedBrands { get; set; } = [];

    /// <summary>Catalogue brands that are not them.</summary>
    public List<string> SkippedBrands { get; set; } = [];

    /// <summary>Where they want their style to go, in their words. Changing style = retake the quiz.</summary>
    public string? Goal { get; set; }

    /// <summary>Usual sizes, used when the fit ledger has nothing for a brand. "L", "W32 L32", "EU 44".</summary>
    public string? TopSize { get; set; }

    public string? BottomSize { get; set; }

    public string? ShoeSize { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
