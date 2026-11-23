using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Fit;

namespace SmartStylist.Modules.Wardrobe.Tests;

public class FitAdvisorTests
{
    private static long _id;

    private static FitEntry Fit(string brand, string size, FitVerdict verdict, string? model = null, string? category = null, int day = 1) => new()
    {
        Id = ++_id, Brand = brand, Size = size, Verdict = verdict, Model = model, Category = category,
        RecordedOn = new DateOnly(2026, 11, day),
    };

    [Fact]
    public void No_records_means_no_size()
    {
        var advice = FitAdvisor.Suggest([], "Veja", null, null);

        Assert.Null(advice.Size);
        Assert.Equal(FitConfidence.None, advice.Confidence);
    }

    [Fact]
    public void The_same_model_wins_over_the_brand()
    {
        List<FitEntry> ledger =
        [
            Fit("Levi's", "W33 L32", FitVerdict.Perfect, category: "jeans"),
            Fit("Levi's", "W32 L32", FitVerdict.Perfect, model: "541 Athletic Taper", category: "jeans"),
        ];

        var advice = FitAdvisor.Suggest(ledger, "Levi's", "541 athletic taper", "jeans");

        Assert.Equal("W32 L32", advice.Size);
        Assert.Equal(FitConfidence.High, advice.Confidence);
    }

    [Fact]
    public void Category_is_used_when_the_model_is_new()
    {
        List<FitEntry> ledger = [Fit("Uniqlo", "XL", FitVerdict.Perfect, model: "Merino crew", category: "knit"), Fit("Uniqlo", "L", FitVerdict.Perfect, category: "tee")];

        var advice = FitAdvisor.Suggest(ledger, "Uniqlo", "Cashmere crew", "knit");

        Assert.Equal("XL", advice.Size);
        Assert.Equal(FitConfidence.Medium, advice.Confidence);
    }

    [Fact]
    public void Roomy_keeps_the_size_with_a_warning()
    {
        var advice = FitAdvisor.Suggest([Fit("Veja", "EU 45", FitVerdict.Roomy)], "Veja", null, null);

        Assert.Equal("EU 45", advice.Size);
        Assert.Equal(FitConfidence.Low, advice.Confidence);
        Assert.Contains("a little roomy", advice.Reason);
    }

    [Fact]
    public void Too_big_steps_one_letter_size_down()
    {
        var advice = FitAdvisor.Suggest([Fit("Reebok", "XL", FitVerdict.TooBig)], "Reebok", null, null);

        Assert.Equal("L", advice.Size);
        Assert.Contains("too big", advice.Reason);
    }

    [Fact]
    public void The_newest_record_wins()
    {
        List<FitEntry> ledger = [Fit("Fila", "XL", FitVerdict.Perfect, day: 1), Fit("Fila", "L", FitVerdict.Perfect, day: 20)];

        Assert.Equal("L", FitAdvisor.Suggest(ledger, "Fila", null, null).Size);
    }

    [Theory]
    [InlineData("Levi's", "LEVIS")]
    [InlineData("Levi’s", "levis")]
    [InlineData("J.Crew", "jcrew")]
    public void Brand_names_match_loosely(string a, string b) => Assert.Equal(FitAdvisor.Normalize(a), FitAdvisor.Normalize(b));
}
