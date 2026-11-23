using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Modules.Catalog.Tests;

public class DealsReportTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);

    private static List<PricePoint> Days(int count, decimal price, bool onSale = false) =>
        Enumerable.Range(0, count).Select(i => new PricePoint(Start.AddDays(i), price, onSale)).ToList();

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
    public void Never_been_cheaper_is_buy_now_whatever_the_percentage()
    {
        // Only 5% off: no percentage threshold, the history decides.
        var a = DealsReport.Assess(Days(20, 100m), 95m, null, "USD");

        Assert.Equal(DealVerdict.BuyNow, a.Verdict);
        Assert.StartsWith("Buy now: never been this cheap", a.Reason);
        Assert.Contains("100.00 USD", a.Reason);
    }

    [Fact]
    public void Has_been_cheaper_on_an_item_often_on_sale_is_wait()
    {
        // On sale at 70 every other day: 85 now is below usual, but 70 comes back.
        var history = Enumerable.Range(0, 20)
            .Select(i => new PricePoint(Start.AddDays(i), i % 2 == 0 ? 100m : 70m, i % 2 == 1))
            .ToList();

        var a = DealsReport.Assess(history, 75m, 100m, "USD");

        Assert.Equal(DealVerdict.Wait, a.Verdict);
        Assert.Equal(70m, a.LowestBefore);
        Assert.Equal(Start.AddDays(19), a.LowestOn);
        Assert.StartsWith("Wait: it was 70.00 USD on Oct 20", a.Reason);
    }

    [Fact]
    public void Rare_sale_on_a_usually_full_price_item_is_buy_now_even_if_it_was_cheaper_once()
    {
        var history = Days(30, 100m);
        history[3] = history[3] with { Price = 60m, OnSale = true }; // one flash sale a month ago

        var a = DealsReport.Assess(history, 80m, 100m);

        Assert.Equal(DealVerdict.BuyNow, a.Verdict);
        Assert.StartsWith("Buy now: rarely discounted (on sale 1 of 30 days)", a.Reason);
    }

    [Fact]
    public void Was_price_on_the_usual_price_is_a_fake_sale()
    {
        // Always "on sale" at 60: the crossed-out 100 was never the real price.
        var a = DealsReport.Assess(Days(30, 60m, onSale: true), 60m, 100m);

        Assert.Equal(DealVerdict.FakeSale, a.Verdict);
        Assert.Equal(0.40m, a.ClaimedDiscount);
        Assert.Equal(0m, a.RealDiscount);
        Assert.StartsWith("Skip:", a.Reason);
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
        Assert.Equal(DealVerdict.BuyNow, row.Assessment.Verdict);
        Assert.Equal(0.25m, row.Assessment.RealDiscount);
    }
}
