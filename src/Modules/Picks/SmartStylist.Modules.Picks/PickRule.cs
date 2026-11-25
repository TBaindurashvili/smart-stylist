using SmartStylist.Modules.Catalog.Reporting;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Style;

namespace SmartStylist.Modules.Picks;

/// <param name="Score">0-10: price 0-4, style 0-3, size 0-3. Orders the picks; it does not decide them.</param>
/// <param name="Tell">Worth a message to the person.</param>
/// <param name="HeldBack">Why it is not told, in one line; null when told.</param>
public sealed record PickJudgement(int Score, bool Tell, string? HeldBack);

/// <summary>
/// When an item is worth telling a person about. No percentage and no score threshold: all three must say yes.
/// <list type="bullet">
/// <item>Price: the deal rule says "buy now" (at or below its realistic low, or a rare sale on an item that is
/// usually full price). "Wait", fake sales and too little history are never told.</item>
/// <item>Style: <see cref="StyleQuiz.Match"/> finds something of theirs in it (a loved brand, a style word they
/// like, a kind of item they want) and nothing that excludes it.</item>
/// <item>Size: their fit ledger or their usual size names a size to buy.</item>
/// </list>
/// Quality is checked before this: only quality-catalogue brands reach the rule at all.
/// </summary>
public static class PickRule
{
    public static PickJudgement Judge(DealAssessment deal, decimal price, StyleMatch style, SizeAdvice size)
    {
        if (style.Excluded)
        {
            return new PickJudgement(0, false, style.Reason);
        }

        var score = PricePoints(deal, price) + Math.Clamp(style.Score, 0, 3) + SizePoints(size.Confidence);
        var heldBack = deal.Verdict != DealVerdict.BuyNow ? $"Price: {deal.Reason}"
            : style.Score < 1 ? $"Style: nothing in it is clearly theirs ({style.Reason})"
            : size.Size is null ? $"Size: {size.Reason}"
            : null;
        return new PickJudgement(score, heldBack is null, heldBack);
    }

    /// <summary>A new low beats matching the low or a rare sale; leaning on outside evidence costs a point.</summary>
    private static int PricePoints(DealAssessment deal, decimal price)
    {
        if (deal.Verdict != DealVerdict.BuyNow)
        {
            return 0;
        }

        var points = price < deal.LowestBefore ? 4 : 3;
        return deal.LowConfidence ? points - 1 : points;
    }

    private static int SizePoints(FitConfidence confidence) => confidence switch
    {
        FitConfidence.High => 3,
        FitConfidence.Medium => 2,
        FitConfidence.Low => 1,
        _ => 0,
    };
}
