using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Tests;

public class UniqloApiTests
{
    private const string Prices = """
        {"status":"ok","result":{
          "l2s":[
            {"l2Id":"L2-09-003","color":{"displayCode":"09"},"size":{"displayCode":"003"}},
            {"l2Id":"L2-09-004","color":{"displayCode":"09"},"size":{"displayCode":"004"}},
            {"l2Id":"L2-09-008","color":{"displayCode":"09"},"size":{"displayCode":"008"}}],
          "prices":{
            "L2-09-003":{"base":{"currency":{"code":"USD"},"value":49.9},"promo":{"value":39.9}},
            "L2-09-004":{"base":{"currency":{"code":"USD"},"value":49.9},"promo":null},
            "L2-09-008":{"base":{"currency":{"code":"USD"},"value":49.9},"promo":{"value":39.9}}},
          "stocks":{
            "L2-09-003":{"statusCode":"IN_STOCK","quantity":12},
            "L2-09-004":{"statusCode":"STOCK_OUT","quantity":0},
            "L2-09-008":{"statusCode":"LOW_STOCK","quantity":2}}}}
        """;

    private const string Details = """
        {"status":"ok","result":{"name":"Extra Fine Merino Crew Neck Sweater","colors":[{"displayCode":"09","name":"BLACK"}]}}
        """;

    [Theory]
    [InlineData("https://www.uniqlo.com/us/en/products/E422990-001/00", "E422990-000")]
    [InlineData("https://www.uniqlo.com/us/en/products/E429066-000/01?colorDisplayCode=56", "E429066-000")]
    [InlineData("https://www.uniqlo.com/us/en/men/tops", null)]
    public void Reads_the_product_id_from_a_page_address(string url, string? id) =>
        Assert.Equal(id, UniqloApiCollector.ProductId(url));

    [Fact]
    public void Builds_the_commerce_api_address() =>
        Assert.Equal(
            "https://www.uniqlo.com/us/api/commerce/v5/en/products/E429066-000/price-groups/00/l2s?withPrices=true&withStocks=true&httpFailure=true",
            UniqloApiCollector.PricesUrl(new Uri("https://www.uniqlo.com/us/en"), "E429066-000").ToString());

    [Fact]
    public void Reads_every_colour_and_size_with_promo_price_and_stock()
    {
        var product = UniqloApiCollector.Parse("E429066-000", Prices, Details, new Uri("https://www.uniqlo.com/us/en"), "USD")!;

        Assert.Equal("Extra Fine Merino Crew Neck Sweater", product.Title);
        Assert.Equal("https://www.uniqlo.com/us/en/products/E429066-000/00", product.Url);
        Assert.Equal(["S", "M", "3XL"], product.Offers.Select(o => o.SizeLabel));
        var small = product.Offers[0];
        Assert.Equal(39.9m, small.Price);
        Assert.Equal(49.9m, small.ListPrice);
        Assert.Equal("BLACK", small.Colour);
        Assert.True(small.InStock);
        Assert.Null(product.Offers[1].ListPrice);
        Assert.False(product.Offers[1].InStock);
        Assert.True(product.Offers[2].InStock);
    }

    [Fact]
    public async Task Collects_uniqlo_through_the_api_not_the_page()
    {
        var uniqlo = new ShopOptions { Key = "uniqlo", Name = "Uniqlo", BaseUrl = "https://www.uniqlo.com/us/en", Currency = "USD", Strategy = CollectorStrategy.UniqloApi };
        await using var host = new TestHost(uniqlo);
        await host.InitializeAsync();
        host.Web.Serve("https://www.uniqlo.com/robots.txt", "Sitemap: https://www.uniqlo.com/us/sitemap.xml", "text/plain");
        host.Web.Serve("https://www.uniqlo.com/us/sitemap.xml",
            """<urlset><url><loc>https://www.uniqlo.com/us/en/products/E429066-000/00</loc></url></urlset>""", "application/xml");
        host.Web.Serve(UniqloApiCollector.PricesUrl(new Uri(uniqlo.BaseUrl), "E429066-000").ToString(), Prices, "application/json");
        host.Web.Serve(UniqloApiCollector.DetailsUrl(new Uri(uniqlo.BaseUrl), "E429066-000").ToString(), Details, "application/json");

        var run = await host.InScopeAsync(sp => sp.GetRequiredService<PriceCollectionService>().CollectAsync("uniqlo", null, CancellationToken.None));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(CollectorStrategy.UniqloApi, run.Strategy);
        Assert.Equal(3, run.SnapshotsWritten);
        Assert.DoesNotContain(host.Web.Requested, r => r.EndsWith("/products/E429066-000/00", StringComparison.Ordinal));
    }
}

public class PriceSanityTests
{
    private static readonly PriceSanityOptions Options = new();

    [Theory]
    [InlineData(1.90, 49.90, null, "Extra Fine Merino Sweater", false)]
    [InlineData(1.90, null, 49.90, "Extra Fine Merino Sweater", false)]
    [InlineData(1.90, null, null, "Extra Fine Merino Sweater", false)]
    [InlineData(9.00, null, null, "541 Athletic Taper Jeans", false)]
    [InlineData(29.90, 49.90, 49.90, "Extra Fine Merino Sweater", true)]
    [InlineData(5.90, 14.90, null, "Supima Cotton T-Shirt", true)]
    [InlineData(12.99, 59.50, null, "Chino", true)]
    [InlineData(8.00, 59.50, null, "Chino", false)]
    public void Rejects_prices_that_cannot_be_real(double price, double? list, double? usual, string title, bool real)
    {
        var why = PriceSanity.Check((decimal)price, (decimal?)list, (decimal?)usual, "USD", title, Options);
        Assert.Equal(real, why is null);
    }

    [Fact]
    public void Leaves_other_currencies_to_the_relative_checks() =>
        Assert.Null(PriceSanity.Check(2m, null, null, "GEL", "Jeans", Options));

    [Fact]
    public async Task Collection_skips_a_placeholder_price()
    {
        const string root = "https://shop.example.com";
        await using var host = new TestHost(new ShopOptions { Key = "example", Name = "Example", BaseUrl = root, Currency = "USD", Strategy = CollectorStrategy.SitemapJsonLd });
        await host.InitializeAsync();
        host.Web.Serve($"{root}/robots.txt", $"Sitemap: {root}/sitemap.xml", "text/plain");
        host.Web.Serve($"{root}/sitemap.xml", $"<urlset><url><loc>{root}/p/1</loc></url><url><loc>{root}/p/2</loc></url></urlset>", "application/xml");
        host.Web.Serve($"{root}/p/1", """<script type="application/ld+json">{"@type":"Product","sku":"1","name":"Merino Sweater","offers":{"@type":"Offer","price":"1.90","priceCurrency":"USD"}}</script>""");
        host.Web.Serve($"{root}/p/2", """<script type="application/ld+json">{"@type":"Product","sku":"2","name":"Merino Sweater","offers":{"@type":"Offer","price":"39.90","priceCurrency":"USD"}}</script>""");

        var run = await host.InScopeAsync(sp => sp.GetRequiredService<PriceCollectionService>().CollectAsync("example", null, CancellationToken.None));

        Assert.Equal(1, run.SnapshotsWritten);
        Assert.Contains("Rejected 1 unreal prices", run.Error);
    }
}

public class EgressTests
{
    private static readonly ShopOptions BrooksBrothers = new()
    {
        Key = "brooksbrothers", Name = "Brooks Brothers", BaseUrl = "https://www.brooksbrothers.com", Currency = "USD",
        Strategy = CollectorStrategy.SitemapJsonLd, Egress = "us",
    };

    [Theory]
    [InlineData("https://www.brooksbrothers.com/p/1", "https://www.brooksbrothers.eu/", true)]
    [InlineData("https://www.levi.com/US/en_US/p/1", "https://www.levi.com/GE/en/", true)]
    [InlineData("https://www.levi.com/US/en_US/p/1", "https://www.levi.com/US/en_US/p/1?x=1", false)]
    [InlineData("https://shop.example.com/a", "https://www.shop.example.com/a", false)]
    [InlineData("https://www.kohls.com/product/1", "https://www.kohls.com/product/2", false)]
    public void Recognises_a_geo_redirect(string requested, string landed, bool geo) =>
        Assert.Equal(geo, GeoRedirectException.IsGeoRedirect(new Uri(requested), new Uri(landed)));

    [Fact]
    public async Task The_http_fetcher_reports_a_geo_redirect()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        host.Web.Redirect("https://www.brooksbrothers.com/p/1", "https://www.brooksbrothers.eu/");

        await Assert.ThrowsAsync<GeoRedirectException>(() => host.InScopeAsync(sp =>
            sp.GetRequiredService<HttpPageFetcher>().GetPageAsync(new Uri("https://www.brooksbrothers.com/p/1"), CancellationToken.None)));
    }

    [Fact]
    public async Task A_geo_redirected_shop_switches_to_the_us_egress()
    {
        var us = new FakeWeb();
        await using var host = TestHost.With(o => o.Egress["us"] = new EgressOptions { ProxyUrl = "http://203.0.113.10:3128", MinDelayMs = 0 }, BrooksBrothers);
        await host.InitializeAsync();
        await host.InScopeAsync(sp =>
        {
            sp.GetRequiredService<EgressFetchers>().HandlerFactory = _ => us;
            return Task.FromResult(0);
        });

        const string root = "https://www.brooksbrothers.com";
        host.Web.Redirect($"{root}/robots.txt", "https://www.brooksbrothers.eu/");
        us.Serve($"{root}/robots.txt", $"Sitemap: {root}/sitemap.xml", "text/plain");
        us.Serve($"{root}/sitemap.xml", $"<urlset><url><loc>{root}/p/oxford</loc></url></urlset>", "application/xml");
        us.Serve($"{root}/p/oxford", """<script type="application/ld+json">{"@type":"Product","sku":"ox","name":"Oxford Shirt","offers":{"@type":"Offer","price":"98.00","priceCurrency":"USD"}}</script>""");

        var run = await host.InScopeAsync(sp => sp.GetRequiredService<PriceCollectionService>().CollectAsync("brooksbrothers", null, CancellationToken.None));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.SnapshotsWritten);
        Assert.Single(host.Web.Requested);
    }

    [Fact]
    public async Task Without_a_proxy_it_says_what_to_set()
    {
        await using var host = new TestHost(BrooksBrothers);
        await host.InitializeAsync();
        host.Web.Redirect("https://www.brooksbrothers.com/robots.txt", "https://www.brooksbrothers.eu/");

        var run = await host.InScopeAsync(sp => sp.GetRequiredService<PriceCollectionService>().CollectAsync("brooksbrothers", null, CancellationToken.None));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.True(run.Error!.Contains("Catalog:Egress:us:ProxyUrl", StringComparison.Ordinal), run.Error);
    }
}

public class WatchedPagesTests
{
    private sealed class FakeBrowser(Dictionary<string, Func<string>> pages) : IPageFetcher
    {
        public List<string> Opened { get; } = [];

        public Task<string> GetPageAsync(Uri url, CancellationToken cancellationToken)
        {
            Opened.Add(url.ToString());
            return Task.FromResult(pages[url.ToString()]());
        }

        public Task<byte[]> GetFileAsync(Uri url, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static string Page(string sku, decimal price) =>
        $$$"""<script type="application/ld+json">{"@type":"Product","sku":"{{{sku}}}","name":"Chino {{{sku}}}","offers":{"@type":"Offer","price":"{{{price}}}","priceCurrency":"USD"}}</script>""";

    [Fact]
    public async Task Opens_only_watched_pages_and_stops_at_a_bot_check()
    {
        var jcrew = new ShopOptions
        {
            Key = "jcrew", Name = "J.Crew", BaseUrl = "https://www.jcrew.com", Currency = "USD", Enabled = false, WatchInBrowser = true,
            WatchUrls = ["https://www.jcrew.com/p/a", "https://www.jcrew.com/p/b", "https://www.jcrew.com/p/c"],
        };
        var other = new ShopOptions { Key = "reebok", Name = "Reebok", BaseUrl = "https://www.reebok.com", Currency = "USD", WatchUrls = ["https://www.reebok.com/p/x"] };
        await using var host = TestHost.With(o => o.Watch.DelayMs = 0, jcrew, other);
        await host.InitializeAsync();
        var browser = new FakeBrowser(new()
        {
            ["https://www.jcrew.com/p/a"] = () => Page("a", 58.80m),
            ["https://www.jcrew.com/p/b"] = () => throw new BlockedByBotWallException("Akamai", 403, new Uri("https://www.jcrew.com/p/b")),
            ["https://www.jcrew.com/p/c"] = () => Page("c", 70m),
        });

        var results = await host.InScopeAsync(sp => new WatchedPagesService(
                sp.GetRequiredService<CatalogDbContext>(), browser, sp.GetRequiredService<PageCaptureService>(),
                sp.GetRequiredService<IOptions<CatalogOptions>>(), NullLogger<WatchedPagesService>.Instance)
            .RunAsync([], CancellationToken.None));

        var result = Assert.Single(results);
        Assert.Equal("jcrew", result.ShopKey);
        Assert.Equal(1, result.Saved);
        Assert.Contains("Akamai", result.StoppedBecause);
        Assert.Equal(["https://www.jcrew.com/p/a", "https://www.jcrew.com/p/b"], browser.Opened);
        var snapshot = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().PriceSnapshots.SingleAsync());
        Assert.Equal(PriceSource.Crawl, snapshot.Source);
    }
}
