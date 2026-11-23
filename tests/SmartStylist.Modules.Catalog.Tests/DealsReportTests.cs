using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Modules.Catalog.Tests;

public class DealsReportTests
{
    private static List<decimal> Days(int count, decimal price) => Enumerable.Repeat(price, count).ToList();

    [Fact]
    public void Too_little_history_is_still_learning_but_keeps_the_claimed_discount()
    {
        var a = DealsReport.Assess(Days(5, 100m), 70m, 100m);

        Assert.Equal(DealVerdict.StillLearning, a.Verdict);
        Assert.Equal(0.30m, a.ClaimedDiscount);
        Assert.Null(a.RealDiscount);
        Assert.Equal(6, a.Days);
    }

    [Fact]
    public void Lowest_price_well_below_usual_is_best_price()
    {
        var a = DealsReport.Assess(Days(20, 100m), 80m, 120m);

        Assert.Equal(DealVerdict.BestPrice, a.Verdict);
        Assert.Equal(0.20m, a.RealDiscount);
        Assert.Equal(100m, a.UsualPrice);
    }

    [Fact]
    public void Below_usual_but_lower_before_is_good_price()
    {
        var history = Days(20, 100m);
        history.Add(70m);

        var a = DealsReport.Assess(history, 85m, null);

        Assert.Equal(DealVerdict.GoodPrice, a.Verdict);
        Assert.Equal(70m, a.LowestBefore);
    }

    [Fact]
    public void Was_price_on_the_usual_price_is_a_fake_sale()
    {
        // Always "on sale" at 60: the crossed-out 100 was never the real price.
        var a = DealsReport.Assess(Days(30, 60m), 60m, 100m);

        Assert.Equal(DealVerdict.FakeSale, a.Verdict);
        Assert.Equal(0.40m, a.ClaimedDiscount);
        Assert.Equal(0m, a.RealDiscount);
    }

    [Fact]
    public void Usual_price_without_a_sale_is_not_a_deal()
    {
        Assert.Equal(DealVerdict.UsualPrice, DealsReport.Assess(Days(30, 60m), 60m, null).Verdict);
    }

    [Fact]
    public async Task Lists_a_saved_product_that_dropped_below_its_usual_price()
    {
        var shop = new ShopOptions { Key = "levis", Name = "Levi's", BaseUrl = "https://www.levi.com/US/en_US", Currency = "USD", Enabled = false };
        await using var host = new TestHost(shop);
        await host.InitializeAsync();

        const string url = "https://www.levi.com/US/en_US/jeans/p/045415";
        Task Capture(decimal price) => host.InScopeAsync(sp => sp.GetRequiredService<PageCaptureService>().CaptureAsync(new PageCapture(url, $$"""
            <link rel="canonical" href="{{url}}">
            <script type="application/ld+json">
            {"@type":"Product","sku":"045415","name":"541 Jeans","offers":{"@type":"Offer","price":"{{price}}","priceCurrency":"USD"} }
            </script>
            """), CancellationToken.None));

        for (var day = 0; day < DealsReport.MinHistoryDays; day++)
        {
            await Capture(100m);
            host.Time.Advance(TimeSpan.FromDays(1));
        }

        await Capture(75m);

        var rows = await host.InScopeAsync(sp => sp.GetRequiredService<DealsReport>().BuildAsync(CancellationToken.None));

        var row = Assert.Single(rows);
        Assert.Equal("541 Jeans", row.Title);
        Assert.Equal(75m, row.Price);
        Assert.Equal(PriceSource.Extension, row.Source);
        Assert.Equal(DealVerdict.BestPrice, row.Assessment.Verdict);
        Assert.Equal(0.25m, row.Assessment.RealDiscount);
    }
}
