using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Modules.Catalog.Tests;

public class PriceHistoryServiceTests
{
    private const string Url = "https://www.levi.com/US/en_US/jeans/p/045415";

    private static readonly ShopOptions Levis = new() { Key = "levis", Name = "Levi's", BaseUrl = "https://www.levi.com/US/en_US", Currency = "USD", Enabled = false };

    private static Task<PageCaptureResult> CaptureAsync(TestHost host, decimal priceM, bool inStockM = true) =>
        host.InScopeAsync(sp => sp.GetRequiredService<PageCaptureService>().CaptureAsync(new PageCapture(Url, $$"""
            <link rel="canonical" href="{{Url}}">
            <script type="application/ld+json">
            {"@type":"Product","sku":"045415","name":"541 Jeans","offers":[
              {"@type":"Offer","sku":"045415-M","itemOffered":{"size":"M"},"price":"{{priceM}}","priceCurrency":"USD","availability":"https://schema.org/{{(inStockM ? "InStock" : "OutOfStock")}}"},
              {"@type":"Offer","sku":"045415-L","itemOffered":{"size":"L"},"price":"100","priceCurrency":"USD","availability":"https://schema.org/InStock"}]}
            </script>
            """), CancellationToken.None));

    [Fact]
    public async Task History_gives_lows_median_and_levels()
    {
        await using var host = new TestHost(Levis);
        await host.InitializeAsync();
        foreach (var price in new[] { 100m, 100m, 80m, 100m, 90m })
        {
            await CaptureAsync(host, price);
            host.Time.Advance(TimeSpan.FromDays(1));
        }

        var productId = (await host.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<Persistence.CatalogDbContext>().Products.Single().Id)));
        var today = DateOnly.FromDateTime(host.Time.GetUtcNow().UtcDateTime);
        var history = await host.InScopeAsync(sp => sp.GetRequiredService<PriceHistoryService>().GetHistoryAsync(productId, null, null, today, CancellationToken.None));

        Assert.NotNull(history);
        Assert.Equal(5, history.Days);
        Assert.Equal(80m, history.AllTimeLow);
        Assert.Equal(80m, history.Low90Days);
        Assert.Equal(90m, history.Current);
        Assert.Equal(100m, history.Median); // the L size is always 100, so the daily low is 100 on most days
        Assert.Equal([new PriceLevel(80m, 1), new PriceLevel(90m, 1), new PriceLevel(100m, 3)], history.Levels);
    }

    [Fact]
    public async Task Drops_list_new_lows_and_sizes_back_in_stock()
    {
        await using var host = new TestHost(Levis);
        await host.InitializeAsync();
        await CaptureAsync(host, 100m, inStockM: false);
        host.Time.Advance(TimeSpan.FromDays(1));
        var since = DateOnly.FromDateTime(host.Time.GetUtcNow().UtcDateTime);
        await CaptureAsync(host, 100m, inStockM: true);

        var drops = await host.InScopeAsync(sp => sp.GetRequiredService<PriceHistoryService>().GetDropsAsync(since, null, CancellationToken.None));
        var back = Assert.Single(drops);
        Assert.Equal(DropKind.BackInStock, back.Kind);

        host.Time.Advance(TimeSpan.FromDays(1));
        await CaptureAsync(host, 70m);
        var onlyL = await host.InScopeAsync(sp => sp.GetRequiredService<PriceHistoryService>().GetDropsAsync(since, ["L"], CancellationToken.None));
        Assert.Empty(onlyL);
        var low = Assert.Single(await host.InScopeAsync(sp => sp.GetRequiredService<PriceHistoryService>().GetDropsAsync(since, ["m"], CancellationToken.None)));
        Assert.Equal(DropKind.NewLow, low.Kind);
        Assert.Equal(70m, low.Price);
        Assert.Equal(100m, low.PreviousLow);
    }
}
