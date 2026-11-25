namespace SmartStylist.Modules.Picks.Domain;

/// <summary>
/// One "buy now" pick told to a person. Kept so the same item is not sent twice at the same price, and to check the
/// mission's "buy now right 8 times out of 10": a pick was right when the price did not go lower afterwards.
/// </summary>
public sealed class SentPick
{
    public long Id { get; set; }

    public long ProfileId { get; set; }

    /// <summary>The Catalog product.</summary>
    public long ProductId { get; set; }

    public required string Title { get; set; }

    public required string Url { get; set; }

    public decimal Price { get; set; }

    public required string Currency { get; set; }

    /// <summary>0-10, see <see cref="PickRule"/>.</summary>
    public int Score { get; set; }

    public DateOnly SentOn { get; set; }
}
