using SmartStylist.Modules.Catalog.Reporting;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Style;

namespace SmartStylist.Modules.Picks.Tests;

public class PickRuleTests
{
    private static DealAssessment Deal(DealVerdict verdict, decimal lowestBefore = 50m, bool lowConfidence = false) =>
        new(verdict, $"{verdict}.", 0.3m, 0.2m, 60m, lowestBefore, new DateOnly(2026, 11, 1), 30, lowConfidence);

    private static readonly StyleMatch Theirs = new(3, false, "A brand they love; minimal.");
    private static readonly SizeAdvice KnownSize = new("M", FitConfidence.High, "M: fitted perfectly.", []);

    [Fact]
    public void Buy_now_in_their_style_and_size_is_told_with_a_full_score()
    {
        var judgement = PickRule.Judge(Deal(DealVerdict.BuyNow), 45m, Theirs, KnownSize);

        Assert.True(judgement.Tell);
        Assert.Null(judgement.HeldBack);
        Assert.Equal(10, judgement.Score);
    }

    [Theory]
    [InlineData(DealVerdict.Wait)]
    [InlineData(DealVerdict.FakeSale)]
    [InlineData(DealVerdict.StillLearning)]
    public void Only_buy_now_prices_are_told(DealVerdict verdict)
    {
        var judgement = PickRule.Judge(Deal(verdict), 45m, Theirs, KnownSize);

        Assert.False(judgement.Tell);
        Assert.StartsWith("Price:", judgement.HeldBack);
    }

    [Fact]
    public void A_great_price_on_something_not_clearly_theirs_is_held_back()
    {
        var judgement = PickRule.Judge(Deal(DealVerdict.BuyNow), 45m, new StyleMatch(0, false, "Nothing in their style either way."), KnownSize);

        Assert.False(judgement.Tell);
        Assert.StartsWith("Style:", judgement.HeldBack);
    }

    [Fact]
    public void Excluded_items_are_never_told()
    {
        var judgement = PickRule.Judge(Deal(DealVerdict.BuyNow), 45m, new StyleMatch(0, true, "No-go: logo."), KnownSize);

        Assert.False(judgement.Tell);
        Assert.Equal(0, judgement.Score);
    }

    [Fact]
    public void Without_a_size_to_buy_it_is_held_back()
    {
        var judgement = PickRule.Judge(Deal(DealVerdict.BuyNow), 45m, Theirs, SizeAdvice.Unknown("Uniqlo"));

        Assert.False(judgement.Tell);
        Assert.StartsWith("Size:", judgement.HeldBack);
    }

    [Fact]
    public void Matching_the_low_or_leaning_on_outside_evidence_scores_lower_than_a_new_low()
    {
        var newLow = PickRule.Judge(Deal(DealVerdict.BuyNow), 45m, Theirs, KnownSize).Score;
        var sameLow = PickRule.Judge(Deal(DealVerdict.BuyNow), 50m, Theirs, KnownSize).Score;
        var lessCertain = PickRule.Judge(Deal(DealVerdict.BuyNow, lowConfidence: true), 45m, Theirs, KnownSize).Score;

        Assert.True(newLow > sameLow);
        Assert.True(newLow > lessCertain);
    }
}
