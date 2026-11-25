using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Catalogue;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Modules.Catalog.Tests;

public class CatalogueTests
{
    private static CatalogOptions Shipped()
    {
        var configuration = new ConfigurationBuilder().AddQualityCatalogue().Build();
        return configuration.GetSection(CatalogOptions.SectionName).Get<CatalogOptions>()!;
    }

    private static ShopOptions Shop(string key, string url, bool enabled = false, string? inbox = null, string? blockedBy = null) =>
        new() { Key = key, Name = key, BaseUrl = url, Enabled = enabled, NewsletterInbox = inbox, BlockedBy = blockedBy };

    private static BrandOptions Brand(string key, string name, params string[] shops) =>
        new() { Key = key, Name = name, For = [Audience.Men], Shops = [.. shops] };

    [Fact]
    public void Shipped_catalogue_has_no_mistakes_and_dresses_men_and_women()
    {
        var catalogue = Shipped();

        Assert.Empty(QualityCatalogue.Problems(catalogue));
        Assert.True(catalogue.Brands.Count(b => b.For.Contains(Audience.Women)) >= 20);
        Assert.True(catalogue.Brands.Count(b => b.For.Contains(Audience.Men)) >= 20);
        Assert.All(catalogue.Brands, b => Assert.False(string.IsNullOrWhiteSpace(b.Why)));
    }

    [Fact]
    public void Extension_runs_on_every_shop_in_the_catalogue()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "SmartStylist.sln")))
        {
            root = root.Parent!;
        }

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "extension", "manifest.json")));
        var hosts = manifest.RootElement.GetProperty("content_scripts")[0].GetProperty("matches").EnumerateArray()
            .Select(m => new Uri(m.GetString()!.Replace("*.", "wildcard.", StringComparison.Ordinal)).Host)
            .ToList();
        bool Covered(string host) => hosts.Any(h => h.StartsWith("wildcard.", StringComparison.Ordinal)
            ? host == h[9..] || host.EndsWith("." + h[9..], StringComparison.Ordinal)
            : host == h);

        var uncovered = Shipped().Shops.Select(s => new Uri(s.BaseUrl).Host).Where(h => !Covered(h)).ToList();
        Assert.True(uncovered.Count == 0, $"Add to extension/manifest.json matches: {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void Problems_name_missing_shops_shared_sites_and_unknown_inboxes()
    {
        var options = new CatalogOptions
        {
            Shops = [Shop("lacoste", "https://www.lacoste.com/us"), Shop("lacoste-uk", "https://www.lacoste.com/gb", inbox: "work")],
            Brands = [Brand("lacoste", "Lacoste", "lacoste", "lacoste-fr"), new BrandOptions { Key = "nobody", Name = "Nobody" }],
        };

        var problems = QualityCatalogue.Problems(options);

        Assert.Contains(problems, p => p.Contains("share the site lacoste.com"));
        Assert.Contains(problems, p => p.Contains("'lacoste-fr', which is not in Catalog:Shops"));
        Assert.Contains(problems, p => p.Contains("not \"work\""));
        Assert.Contains(problems, p => p.Contains("'nobody' says nobody"));
        Assert.Contains(problems, p => p.Contains("'nobody' has no shop"));
    }

    [Theory]
    [InlineData(null, "Levi's 541 Athletic Taper Men's Jeans", "levis")]
    [InlineData("Levi Strauss", "Some jeans", "levis")]
    [InlineData("ARC'TERYX", "Beta jacket", "arcteryx")]
    [InlineData(null, "Veilance Monitor Coat", "arcteryx")]
    [InlineData(null, "The North Face Nuptse", "thenorthface")]
    [InlineData(null, "Generic cotton tee", null)]
    [InlineData("Shein", "Cosy knit", null)]
    public void FindBrand_matches_brand_field_or_whole_words_in_the_title(string? brand, string title, string? expected)
    {
        var catalogue = new QualityCatalogue(Options.Create(Shipped()));

        Assert.Equal(expected, catalogue.FindBrand(brand, title)?.Key);
    }

    [Fact]
    public void Blocked_shop_with_a_newsletter_is_read_by_sale_emails()
    {
        var shops = new[] { Shop("lacoste", "https://www.lacoste.com/us", inbox: "personal", blockedBy: "403") };
        var brand = Brand("lacoste", "Lacoste", "lacoste");
        brand.Awin = [new AwinProgrammeOptions { Programme = "Lacoste UK" }];

        var routes = QualityCatalogue.Routes(brand, shops, _ => null, _ => false);

        Assert.Equal(PriceRoute.SaleEmail, QualityCatalogue.Best(routes)?.Route);
        Assert.Contains(routes, r => r is { Route: PriceRoute.Feed, State: RouteState.Pending, Shop: "Lacoste UK" });
        Assert.Contains(routes, r => r is { Route: PriceRoute.PageData, State: RouteState.Blocked, Detail: "blocked: 403" });
        Assert.Contains(routes, r => r is { Route: PriceRoute.Extension, State: RouteState.NotTried });
    }

    [Fact]
    public void Shopify_shop_is_a_feed_but_waits_while_its_egress_is_not_set()
    {
        var shopify = Shop("taylorstitch", "https://www.taylorstitch.com", enabled: true);
        var geo = Shop("levis", "https://www.levi.com/US/en_US", enabled: true);
        geo.Egress = "us";

        var feed = QualityCatalogue.Routes(Brand("ts", "Taylor Stitch", "taylorstitch"), [shopify], _ => CollectorStrategy.Shopify, _ => false);
        var waiting = QualityCatalogue.Routes(Brand("levis", "Levi's", "levis"), [geo], _ => CollectorStrategy.SitemapJsonLd, _ => false);

        Assert.Equal(new RouteStatus(PriceRoute.Feed, RouteState.Works, "taylorstitch", "Shopify products.json"), QualityCatalogue.Best(feed));
        Assert.Null(QualityCatalogue.Best(waiting));
        Assert.Contains(waiting, r => r is { Route: PriceRoute.PageData, State: RouteState.Pending });
    }

    [Fact]
    public async Task Report_counts_priced_products_per_brand_and_source_at_multi_brand_shops()
    {
        var mrporter = Shop("mrporter", "https://www.mrporter.com/en-us", inbox: "personal");
        var brands = new[] { Brand("sunspel", "Sunspel", "mrporter"), Brand("barbour", "Barbour", "mrporter") };
        await using var host = new TestHost(brands, mrporter);
        await host.InitializeAsync();
        await Capture(host, "https://www.mrporter.com/en-us/mens/product/sunspel/1", "Sunspel Riviera Polo");
        await Capture(host, "https://www.mrporter.com/en-us/mens/product/sunspel/2", "Sunspel Classic Tee");
        await Capture(host, "https://www.mrporter.com/en-us/mens/product/other/3", "Unknown Label Shirt");

        var report = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogueReport>().BuildAsync(CancellationToken.None));

        var sunspel = report.Single(r => r.Brand.Key == "sunspel");
        Assert.Equal(2, sunspel.ProductsPriced[PriceSource.Extension]);
        Assert.Equal(PriceRoute.SaleEmail, sunspel.Best?.Route);
        Assert.Empty(report.Single(r => r.Brand.Key == "barbour").ProductsPriced);
    }

    private static Task<PageCaptureResult> Capture(TestHost host, string url, string title) =>
        host.InScopeAsync(sp => sp.GetRequiredService<PageCaptureService>().CaptureAsync(new PageCapture(url, $$$"""
            <script type="application/ld+json">
            {"@type":"Product","sku":"{{{url.GetHashCode()}}}","name":"{{{title}}}","offers":{"@type":"Offer","price":"95","priceCurrency":"USD"}}
            </script>
            """), CancellationToken.None));
}
