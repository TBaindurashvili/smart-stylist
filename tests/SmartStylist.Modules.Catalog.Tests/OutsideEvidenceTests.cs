using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Evidence;
using SmartStylist.Modules.Catalog.Persistence;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Modules.Catalog.Tests;

public class OutsideEvidenceTests
{
    private const string PageUrl = "https://www.taylorstitch.com/products/the-jack-in-navy";

    private static readonly ShopOptions TaylorStitch = new() { Key = "taylorstitch", Name = "Taylor Stitch", BaseUrl = "https://www.taylorstitch.com", Currency = "USD", Enabled = false };

    private static string Page(decimal price) => $$"""
        <script type="application/ld+json">
        {"@type":"Product","sku":"jack-navy","name":"The Jack in Navy","offers":{"@type":"Offer","sku":"jack-navy-m","price":"{{price}}","priceCurrency":"USD"} }
        </script>
        """;

    private static Task<List<PriceSnapshot>> SnapshotsAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().PriceSnapshots.OrderBy(s => s.ObservedOn).ToListAsync());

    [Fact]
    public void Reads_the_wayback_index_one_copy_per_day()
    {
        var copies = WaybackSeedService.ParseCdx("""
            [["timestamp","original","statuscode"],
             ["20260915101010","https://www.taylorstitch.com/products/the-jack-in-navy","200"],
             ["20260801090000","https://www.taylorstitch.com/products/the-jack-in-navy","200"]]
            """);

        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 15)], copies.Select(c => c.Day));
        Assert.Empty(WaybackSeedService.ParseCdx(""));
    }

    [Fact]
    public void Spreads_the_copies_over_the_period_and_keeps_the_newest()
    {
        var copies = Enumerable.Range(0, 30)
            .Select(i => new WaybackSeedService.ArchivedCopy($"2026{i:0000}", PageUrl, new DateOnly(2026, 1, 1).AddDays(i * 7)))
            .ToList();

        var picked = WaybackSeedService.Spread(copies, 5);

        Assert.Equal(5, picked.Count);
        Assert.Equal(copies[0], picked[0]);
        Assert.Equal(copies[^1], picked[^1]);
    }

    [Fact]
    public async Task Seeds_archived_prices_as_outside_evidence_without_touching_the_shop()
    {
        await using var host = new TestHost(TaylorStitch);
        await host.InitializeAsync();
        host.Web.Serve(
            "https://web.archive.org/cdx/search/cdx?url=https%3A%2F%2Fwww.taylorstitch.com%2Fproducts%2Fthe-jack-in-navy&matchType=exact&output=json&fl=timestamp,original,statuscode&filter=statuscode:200&collapse=timestamp:8&from=20251024&limit=400",
            $$"""[["timestamp","original","statuscode"],["20260601000000","{{PageUrl}}","200"],["20260901000000","{{PageUrl}}","200"]]""",
            "application/json");
        host.Web.Serve($"https://web.archive.org/web/20260601000000id_/{PageUrl}", Page(128m));
        host.Web.Serve($"https://web.archive.org/web/20260901000000id_/{PageUrl}", Page(98m));

        var result = await host.InScopeAsync(sp => sp.GetRequiredService<WaybackSeedService>().SeedUrlAsync(new Uri(PageUrl), null, 12, CancellationToken.None));

        Assert.Equal(2, result.PricesAdded);
        var snapshots = await SnapshotsAsync(host);
        Assert.All(snapshots, s => Assert.Equal(PriceSource.Archive, s.Source));
        Assert.Equal([128m, 98m], snapshots.Select(s => s.Price));
        Assert.DoesNotContain(host.Web.Requested, r => r.StartsWith("https://www.taylorstitch.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Imports_written_down_prices_and_never_overwrites_own_prices()
    {
        await using var host = new TestHost(TaylorStitch);
        await host.InitializeAsync();
        var csv = $"""
            date,url,title,price,list_price,size
            2026-10-02,{PageUrl},The Jack in Navy,$128.00,,M
            2026-10-20,{PageUrl},The Jack in Navy,98,128,M
            2026-10-20,{PageUrl},The Jack in Navy,97,128,M
            yesterday,{PageUrl},The Jack in Navy,90,,M
            2026-10-21,https://example.com/x,Other,10,,
            """;

        var result = await host.InScopeAsync(sp => sp.GetRequiredService<ManualPriceImport>().ImportAsync(new StringReader(csv), CancellationToken.None));

        Assert.Equal(5, result.Rows);
        Assert.Equal(2, result.PricesAdded);
        Assert.Equal(2, result.Skipped.Count);
        var snapshots = await SnapshotsAsync(host);
        Assert.Equal([128m, 98m], snapshots.Select(s => s.Price));
        Assert.Equal(128m, snapshots[1].ListPrice);
        Assert.All(snapshots, s => Assert.Equal(PriceSource.ManualHistory, s.Source));
    }

    [Fact]
    public void Outside_evidence_gives_a_less_certain_verdict_before_two_weeks_of_own_prices()
    {
        var earlier = new List<PricePoint>
        {
            new(new DateOnly(2026, 8, 1), 128m, false, Outside: true),
            new(new DateOnly(2026, 9, 1), 128m, false, Outside: true),
            new(new DateOnly(2026, 10, 1), 128m, false, Outside: true),
            new(new DateOnly(2026, 11, 20), 128m, false),
        };

        var a = DealsReport.Assess(earlier, 98m, 128m, "USD");

        Assert.Equal(DealVerdict.BuyNow, a.Verdict);
        Assert.True(a.LowConfidence);
        Assert.Contains("Less certain", a.Reason);
    }

    [Fact]
    public void A_few_outside_prices_close_together_are_not_enough()
    {
        var earlier = new List<PricePoint>
        {
            new(new DateOnly(2026, 11, 1), 128m, false, Outside: true),
            new(new DateOnly(2026, 11, 5), 128m, false, Outside: true),
            new(new DateOnly(2026, 11, 9), 128m, false, Outside: true),
        };

        Assert.Equal(DealVerdict.StillLearning, DealsReport.Assess(earlier, 98m, 128m).Verdict);
    }

    [Fact]
    public void Two_weeks_of_own_prices_is_a_normal_verdict_even_with_outside_evidence()
    {
        var earlier = Enumerable.Range(1, 14).Select(i => new PricePoint(new DateOnly(2026, 11, i), 128m, false))
            .Append(new PricePoint(new DateOnly(2026, 6, 1), 128m, false, Outside: true))
            .ToList();

        var a = DealsReport.Assess(earlier, 98m, 128m);

        Assert.False(a.LowConfidence);
        Assert.DoesNotContain("Less certain", a.Reason);
    }
}
