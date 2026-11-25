using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Tests;

public class PriceWriterTests
{
    private static readonly ShopOptions Example = new() { Key = "example", Name = "Example", BaseUrl = "https://shop.example.com", Currency = "USD" };

    private static ScrapedProduct Jacket(string id, string url, decimal price) =>
        new(id, url, "Wool Jacket", "Example", null, [new ScrapedOffer($"{id}-M", "M", price, null, "USD", true)]);

    private static Task<PriceWriteResult> WriteAsync(TestHost host, PriceSource source, params ScrapedProduct[] products) =>
        host.InScopeAsync(async sp =>
        {
            var shop = await sp.GetRequiredService<CatalogDbContext>().Shops.SingleAsync();
            return await sp.GetRequiredService<PriceWriter>().WriteAsync(shop, products, source, CancellationToken.None);
        });

    [Fact]
    public async Task Every_source_gets_the_sanity_check()
    {
        await using var host = new TestHost(Example);
        await host.InitializeAsync();

        // $4 is under the $20 outerwear floor: a feed's placeholder price is refused like a crawled one.
        var result = await WriteAsync(host, PriceSource.AffiliateFeed, Jacket("J1", "https://shop.example.com/j1", 4m));

        Assert.Empty(result.Saved);
        Assert.Equal(1, result.PricesRefused);
        Assert.Empty(await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().Products.ToListAsync()));
    }

    [Fact]
    public async Task Two_items_on_one_page_keep_one_price_per_variant_and_day()
    {
        await using var host = new TestHost(Example);
        await host.InitializeAsync();
        await WriteAsync(host, PriceSource.Newsletter, Jacket("https://shop.example.com/j1", "https://shop.example.com/j1", 120m));

        // A feed sends the product under its own id; it joins the one saved under its page address.
        var result = await WriteAsync(host, PriceSource.AffiliateFeed, Jacket("https://shop.example.com/j1", "https://shop.example.com/j1", 99m),
            Jacket("J1", "https://shop.example.com/j1", 99m));

        Assert.Equal(2, result.Saved.Count);
        var snapshots = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().PriceSnapshots.ToListAsync());
        Assert.Single(await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().Products.ToListAsync()));
        Assert.All(snapshots, s => Assert.Equal(99m, s.Price));
        Assert.Equal(snapshots.Count, snapshots.Select(s => (s.ProductId, s.VariantKey, s.ObservedOn)).Distinct().Count());
    }
}
