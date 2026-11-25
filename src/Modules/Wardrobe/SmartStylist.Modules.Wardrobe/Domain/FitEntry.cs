namespace SmartStylist.Modules.Wardrobe.Domain;

/// <summary>How a size of a brand's item fitted. Ordered from smallest to largest.</summary>
public enum FitVerdict
{
    TooSmall,
    Snug,
    Perfect,
    Roomy,
    TooBig,
}

/// <summary>
/// One line of the fit ledger: "Uniqlo merino knit, XL: perfect". Per-brand fit beats any size chart,
/// so size advice comes from these entries first.
/// </summary>
public sealed class FitEntry
{
    public long Id { get; set; }

    /// <summary>Whose fit this is: a <see cref="StyleProfile"/>.</summary>
    public long ProfileId { get; set; }

    public required string Brand { get; set; }

    /// <summary>The model or product line, e.g. "541 Athletic Taper" or "Nano X4". Empty when it applies brand-wide.</summary>
    public string? Model { get; set; }

    /// <summary>What kind of item, e.g. "tee", "jeans", "trainers". Fit often differs between categories of one brand.</summary>
    public string? Category { get; set; }

    /// <summary>The size as the shop labels it: "XL", "W32 L32", "US 12", "EU 45".</summary>
    public required string Size { get; set; }

    public FitVerdict Verdict { get; set; }

    public string? Note { get; set; }

    public DateOnly RecordedOn { get; set; }

    /// <summary>Kept or returned; a returned item still teaches the fit.</summary>
    public bool Kept { get; set; } = true;
}
