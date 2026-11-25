using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Style;

namespace SmartStylist.Modules.Wardrobe.Tests;

public class StyleQuizTests
{
    private static readonly BrandCard Uniqlo = new("Uniqlo", ["minimal", "casual"], ["basics", "knitwear", "outerwear"], true, true);
    private static readonly BrandCard Cos = new("COS", ["minimal", "modern"], ["knitwear", "trousers", "coats"], true, true);
    private static readonly BrandCard Adidas = new("adidas", ["sport", "street"], ["sneakers", "activewear"], true, true);
    private static readonly BrandCard Sezane = new("Sézane", ["parisian", "feminine"], ["knitwear", "blouses", "dresses"], false, true);
    private static readonly BrandCard SpierMackay = new("Spier & Mackay", ["classic", "tailoring"], ["shirts", "chinos", "suits"], true, false);

    private static StyleProfile Profile(Action<StyleProfile>? edit = null)
    {
        var p = new StyleProfile { Name = "Temo", ShopsFor = ShopsFor.Men };
        edit?.Invoke(p);
        return p;
    }

    [Fact]
    public void Loved_brands_give_the_style_and_skipped_brands_take_it_away()
    {
        var style = StyleQuiz.Detect([Uniqlo, Cos], [Adidas, new BrandCard("Patagonia", ["outdoor", "casual"], ["fleece"], true, true)]);

        Assert.Equal(["minimal", "modern"], style.Liked); // casual: +1 Uniqlo, -1 Patagonia
        Assert.Equal(["outdoor", "sport", "street"], style.Avoided);
        Assert.Equal("knitwear", style.Categories[0]);
    }

    [Fact]
    public void A_brand_with_liked_words_matches_and_says_why()
    {
        var match = StyleQuiz.Match(Profile(p => { p.LikedStyles = ["minimal"]; p.LovedBrands = ["COS"]; }), Cos);

        Assert.False(match.Excluded);
        Assert.Equal(3, match.Score);
        Assert.Equal("A brand they love; minimal.", match.Reason);
    }

    [Fact]
    public void Avoided_words_weigh_more_than_liked_ones()
    {
        var match = StyleQuiz.Match(Profile(p => { p.LikedStyles = ["street"]; p.AvoidedStyles = ["sport"]; }), Adidas);

        Assert.True(match.Score < 0);
        Assert.Contains("sport is not them", match.Reason);
    }

    [Fact]
    public void Women_only_brands_are_not_suggested_to_men()
    {
        Assert.True(StyleQuiz.Match(Profile(), Sezane).Excluded);
        Assert.False(StyleQuiz.Match(Profile(p => p.ShopsFor = ShopsFor.Both), Sezane).Excluded);
    }

    [Fact]
    public void Skipped_brands_are_never_suggested()
    {
        var match = StyleQuiz.Match(Profile(p => p.SkippedBrands = ["ADIDAS"]), Adidas);

        Assert.True(match.Excluded);
    }

    [Theory]
    [InlineData("suits", null)] // a no-go category of the brand
    [InlineData("suit", null)] // singular matches the plural
    [InlineData("logo", "Logo crew sweatshirt")] // a word in the item
    [InlineData("slim fit", "Slim Fit stretch chinos")] // a phrase in the item
    public void No_go_words_exclude(string noGo, string? item) =>
        Assert.True(StyleQuiz.Match(Profile(p => p.NoGo = [noGo]), SpierMackay, item).Excluded);

    [Fact]
    public void No_go_words_match_whole_words_only()
    {
        Assert.False(StyleQuiz.Match(Profile(p => p.NoGo = ["logo"]), Uniqlo, "Logotype-free merino crew").Excluded);
    }

    [Fact]
    public void A_wanted_kind_of_item_adds_to_the_score()
    {
        var plain = StyleQuiz.Match(Profile(p => p.WantedCategories = ["boots"]), Uniqlo, "Merino crew");
        var wanted = StyleQuiz.Match(Profile(p => p.WantedCategories = ["knitwear"]), Uniqlo, "Merino crew");

        Assert.Equal(plain.Score + 1, wanted.Score);
    }

    [Theory]
    [InlineData("jeans", "W32 L32")]
    [InlineData("trainers", "EU 44")]
    [InlineData("knit", "L")]
    public void With_no_fit_records_the_usual_size_is_used(string category, string size)
    {
        var profile = Profile(p => { p.TopSize = "L"; p.BottomSize = "W32 L32"; p.ShoeSize = "EU 44"; });

        var advice = FitAdvisor.Suggest([], "Levi's", null, category, profile);

        Assert.Equal((size, FitConfidence.Low), (advice.Size, advice.Confidence));
    }

    [Fact]
    public void The_usual_size_needs_a_kind_of_item() =>
        Assert.Null(FitAdvisor.Suggest([], "Levi's", null, null, Profile(p => p.TopSize = "L")).Size);
}
