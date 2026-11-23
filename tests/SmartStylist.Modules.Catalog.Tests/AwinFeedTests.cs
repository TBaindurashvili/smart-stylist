using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Affiliates;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Tests;

public class AwinFeedTests
{
    private const string FeedList = """
        Advertiser ID,Advertiser Name,Primary Region,Membership Status,Feed ID,Feed Name,Language,Vertical,Last Imported,Last Checked,No of products,URL
        7001,Lacoste UK,GB,active,90001,Lacoste UK Product Feed,en,Retail,2026-11-23,2026-11-23,2,https://example.com/x
        7002,Some Shop,US,not joined,90002,Some Shop Feed,en,Retail,2026-11-23,2026-11-23,10,https://example.com/y
        """;

    // Two sizes of one polo (one row each, tied by parent_product_id), and a shoe with a quoted, comma-holding name.
    private const string Products = """
        aw_product_id,merchant_product_id,parent_product_id,merchant_id,merchant_name,product_name,brand_name,aw_deep_link,merchant_deep_link,aw_image_url,merchant_image_url,search_price,rrp_price,store_price,currency,in_stock,Fashion:size
        1,PH4012-S,PH4012,7001,Lacoste UK,Classic Polo,Lacoste,https://www.awin1.com/a?p=1,https://www.lacoste.com/gb/polo-PH4012.html,,https://img/p.jpg,62.50,95.00,,GBP,1,S
        2,PH4012-M,PH4012,7001,Lacoste UK,Classic Polo,Lacoste,https://www.awin1.com/a?p=2,https://www.lacoste.com/gb/polo-PH4012.html,,https://img/p.jpg,62.50,95.00,,GBP,0,M
        3,SH77,,7001,Lacoste UK,"Carnaby Pro, leather trainers",Lacoste,https://www.awin1.com/a?p=3,https://www.lacoste.com/gb/carnaby-SH77.html,,,"80.00",80.00,,GBP,1,
        4,BROKEN,,7001,Lacoste UK,No price,Lacoste,,https://www.lacoste.com/gb/x.html,,,,,,GBP,1,
        """;

    [Fact]
    public async Task Feed_list_marks_joined_programmes()
    {
        var feeds = await AwinFeed.ParseFeedListAsync(new StringReader(FeedList), CancellationToken.None);

        Assert.Equal(2, feeds.Count);
        Assert.True(feeds[0].Joined);
        Assert.Equal(90001, feeds[0].FeedId);
        Assert.Equal(7001, feeds[0].AdvertiserId);
        Assert.False(feeds[1].Joined);
    }

    [Fact]
    public async Task Rows_of_one_product_become_sizes()
    {
        var products = await AwinFeed.ParseProductsAsync(new StringReader(Products), 100, CancellationToken.None);

        Assert.Equal(2, products.Count);
        var polo = products[0].Product;
        Assert.Equal("PH4012", polo.ExternalId);
        Assert.Equal(new string?[] { "S", "M" }, polo.Offers.Select(o => o.SizeLabel));
        Assert.All(polo.Offers, o => Assert.Equal(95.00m, o.ListPrice));
        Assert.Equal(new bool?[] { true, false }, polo.Offers.Select(o => o.InStock));

        var shoe = products[1].Product;
        Assert.Equal("Carnaby Pro, leather trainers", shoe.Title);
        Assert.Null(Assert.Single(shoe.Offers).ListPrice); // rrp equal to the price is no discount
    }

    [Fact]
    public async Task Stops_at_the_product_limit_but_keeps_sizes_of_products_it_took()
    {
        var products = await AwinFeed.ParseProductsAsync(new StringReader(Products), 1, CancellationToken.None);

        Assert.Equal(2, Assert.Single(products).Product.Offers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imports_joined_feeds_into_a_shop_for_the_advertiser(bool gzip)
    {
        await using var host = new TestHost();
        await host.InitializeAsync();

        var results = await host.InScopeAsync(async sp =>
        {
            var awin = sp.GetRequiredService<AwinFeedImportService>();
            host.Web.Serve("https://productdata.awin.com/datafeed/list/apikey/test-key", FeedList, "text/csv");
            var url = new Uri(awin.DownloadUrl(90001)).ToString();
            if (gzip)
            {
                host.Web.Serve(url, Gzip(Products));
            }
            else
            {
                host.Web.Serve(url, Products, "text/csv");
            }

            return await awin.ImportAsync(CancellationToken.None);
        });

        var result = Assert.Single(results);
        Assert.Null(result.Error);
        Assert.Equal("awin-7001", result.ShopKey);
        Assert.Equal(2, result.Products);
        Assert.Equal(3, result.Prices);

        var snapshots = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().PriceSnapshots
            .Include(s => s.Product).ThenInclude(p => p.Shop).ToListAsync());
        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, s => Assert.Equal(PriceSource.AffiliateFeed, s.Source));
        Assert.All(snapshots, s => Assert.Equal("Lacoste UK", s.Product.Shop.Name));
        Assert.Equal("https://www.lacoste.com", snapshots[0].Product.Shop.BaseUrl);
        Assert.False(snapshots[0].Product.Shop.Enabled);
    }

    [Fact]
    public async Task A_second_import_on_the_same_day_updates_prices()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();

        async Task ImportAsync(string feed) => await host.InScopeAsync(async sp =>
        {
            var awin = sp.GetRequiredService<AwinFeedImportService>();
            host.Web.Serve("https://productdata.awin.com/datafeed/list/apikey/test-key", FeedList, "text/csv");
            host.Web.Serve(new Uri(awin.DownloadUrl(90001)).ToString(), feed, "text/csv");
            return await awin.ImportAsync(CancellationToken.None);
        });

        await ImportAsync(Products);
        await ImportAsync(Products.Replace("62.50", "55.00"));

        var prices = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().PriceSnapshots
            .Where(s => s.Product.ExternalId == "PH4012").Select(s => s.Price).ToListAsync());
        Assert.Equal(new[] { 55.00m, 55.00m }, prices);
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }
}
