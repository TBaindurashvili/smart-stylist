using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Tests;

public class UniqloApiTests
{
    private const string Root = "https://www.uniqlo.com";

    private const string Api = Root + "/us/api/commerce/v5/en/products/{0}/price-groups/00/l2s?withPrices=true&withStocks=true";

    /// <summary>The l2s answer: one l2 per colour and size, prices and stock keyed by l2Id.</summary>
    private static string L2s(decimal basePrice, decimal? promo)
    {
        var promoJson = promo is null ? "null" : $"{{\"value\":{promo}}}";
        var price = $"{{\"base\":{{\"currency\":{{\"code\":\"USD\",\"symbol\":\"$\"}},\"value\":{basePrice}}},\"promo\":{promoJson}}}";
        return $$$"""
            {"status":"ok","result":{
              "l2s":[
                {"l2Id":"07623587","color":{"code":"COL09","displayCode":"09"},"size":{"code":"SMA004","displayCode":"004"}},
                {"l2Id":"07623588","color":{"code":"COL09","displayCode":"09"},"size":{"code":"SMA008","displayCode":"008"}}
              ],
              "prices":{"07623587":{{{price}}},"07623588":{{{price}}}},
              "stocks":{
                "07623587":{"statusCode":"IN_STOCK","quantity":12},
                "07623588":{"statusCode":"STOCK_OUT","quantity":0}
              }
            }}
            """;
    }

    [Fact]
    public void A_promo_price_is_todays_price_and_the_base_price_is_crossed_out()
    {
        var product = UniqloL2sParser.Parse(L2s(39.90m, 29.90m), "E450535-000", $"{Root}/us/en/products/E450535-000/00", "USD")!;

        Assert.Equal("E450535-000", product.ExternalId);
        var m = product.Offers.Single(o => o.SizeLabel == "M");
        Assert.Equal(29.90m, m.Price);
        Assert.Equal(39.90m, m.ListPrice);
        Assert.Equal("09", m.Colour);
        Assert.True(m.InStock);
        var xxxl = product.Offers.Single(o => o.SizeLabel == "3XL");
        Assert.False(xxxl.InStock);
    }

    [Fact]
    public void The_details_give_the_name_and_colour_names()
    {
        var details = UniqloL2sParser.ParseDetails("""
            {"status":"ok","result":{"name":"Fluffy Yarn Fleece Full-Zip Long-Sleeve Jacket","productId":"E408699-000",
              "colors":[{"code":"COL09","displayCode":"09","name":"BLACK"}]}}
            """)!;

        var product = UniqloL2sParser.Parse(L2s(39.90m, null), "E408699-000", "u", "USD", details)!;

        Assert.Equal("Fluffy Yarn Fleece Full-Zip Long-Sleeve Jacket", product.Title);
        Assert.All(product.Offers, o => Assert.Equal("09 BLACK", o.Colour));
    }

    [Fact]
    public void Without_a_promo_there_is_no_crossed_out_price()
    {
        var product = UniqloL2sParser.Parse(L2s(39.90m, null), "E450535-000", "u", "USD")!;

        Assert.All(product.Offers, o => Assert.Null(o.ListPrice));
        Assert.All(product.Offers, o => Assert.Equal(39.90m, o.Price));
    }

    [Theory]
    [InlineData("https://www.uniqlo.com/us/en/products/E422990-001/00?colorDisplayCode=61", "E422990-001", "00")]
    [InlineData("https://www.uniqlo.com/us/en/products/e450535-000", "E450535-000", "00")]
    [InlineData("https://www.uniqlo.com/us/en/products/E429066-000/01", "E429066-000", "01")]
    public void Reads_the_product_code_and_price_group_from_the_page_address(string url, string code, string group)
    {
        var page = UniqloApiCollector.ProductPage.From(url)!;

        Assert.Equal(code, page.Code);
        Assert.Equal(group, page.PriceGroup);
    }

    [Fact]
    public async Task Collects_from_the_api_and_refuses_the_sold_out_placeholder()
    {
        await using var host = new TestHost(new ShopOptions
        {
            Key = "uniqlo", Name = "Uniqlo", BaseUrl = Root + "/us/en", Currency = "USD", Strategy = CollectorStrategy.UniqloApi,
            SitemapUrl = Root + "/us/en/sitemap.xml", ProductUrlPattern = "/us/en/products/",
        });
        await host.InitializeAsync();
        host.Web.Serve(Root + "/us/en/sitemap.xml", $"""
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>{Root}/us/en/products/E450535-000/00</loc></url>
              <url><loc>{Root}/us/en/products/E422990-001/00</loc></url>
            </urlset>
            """, "application/xml");
        host.Web.Serve(string.Format(Api, "E450535-000"), L2s(39.90m, 29.90m), "application/json");
        host.Web.Serve(string.Format(Api, "E422990-001"), L2s(3.90m, null), "application/json");
        host.Web.Serve(Root + "/us/api/commerce/v5/en/products/E422990-001/price-groups/00/details?includeModelSize=false&httpFailure=true",
            """{"result":{"name":"Fluffy Yarn Fleece Full-Zip Jacket","colors":[]}}""", "application/json");

        var run = await host.InScopeAsync(sp => sp.GetRequiredService<PriceCollectionService>().CollectAsync("uniqlo", null, CancellationToken.None));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(CollectorStrategy.UniqloApi, run.Strategy);
        Assert.Equal(2, run.SnapshotsWritten);
        Assert.Equal(2, run.PricesRejected);
        var prices = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().PriceSnapshots.Select(s => s.Price).ToListAsync());
        Assert.All(prices, p => Assert.Equal(29.90m, p));
        Assert.DoesNotContain(host.Web.Requested, r => r.Contains("/products/E450535-000/00", StringComparison.Ordinal) && !r.Contains("/api/", StringComparison.Ordinal));
    }
}
