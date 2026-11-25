using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Evidence;
using SmartStylist.Modules.Catalog.Persistence;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Modules.Catalog.Tests;

public class ManualHistoryAndWatchTests
{
    private static readonly ShopOptions ReigningChamp = new() { Key = "reigningchamp", Name = "Reigning Champ", BaseUrl = "https://reigningchamp.com", Currency = "USD" };

    private static readonly ShopOptions JCrew = new() { Key = "jcrew", Name = "J.Crew", BaseUrl = "https://www.jcrew.com", Currency = "USD", Enabled = false, BrowserWatch = true };

    private static readonly ShopOptions UnderArmour = new()
    {
        Key = "underarmour", Name = "Under Armour", BaseUrl = "https://www.underarmour.com/en-us", Currency = "USD", Dropped = true,
    };

    private const string WardrobeTables = """
        ## Recommended

        | Item | Shop | Size | Price | Seen |
        |---|---|---|---|---|
        | [Midweight Terry Slim Crewneck](https://reigningchamp.com/products/midweight-terry-crewneck-hgrey) | Reigning Champ | M | ~~$98~~ $68.60 | 2026-11-20 |
        | Round-buckle belt | J.Crew | - | $58.80 (was $98) | Nov 18 |

        Some notes in between.

        ## Near-misses

        | Product | Store | Price now | Date |
        |:--|:--|--:|--|
        | Midweight Terry Slim Crewneck | reigningchamp | $78.40 | 2026-11-10 |
        | Something at a shop we do not know | Mystery Shop | $10 | 2026-11-10 |
        | Supima tee | J.Crew | $1.90 | 2026-11-10 |
        """;

    [Fact]
    public async Task Imports_the_wardrobe_docs_markdown_tables_as_manual_history()
    {
        await using var host = new TestHost(ReigningChamp, JCrew);
        await host.InitializeAsync();
        host.Time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero));

        var result = await host.InScopeAsync(sp => sp.GetRequiredService<ManualPriceImport>().ImportAsync(new StringReader(WardrobeTables), CancellationToken.None));

        Assert.Equal(5, result.Rows);
        // A store the app doesn't collect from gets its own disabled shop, so its price still counts.
        Assert.Equal(4, result.PricesAdded);
        Assert.DoesNotContain(result.Skipped, s => s.Contains("Mystery Shop"));
        Assert.True(await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().Shops.AnyAsync(s => s.Key == "manual-mystery-shop" && !s.Enabled)));
        Assert.Contains(result.Skipped, s => s.Contains("sanity check"));

        var snapshots = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().PriceSnapshots.Include(s => s.Product)
            .OrderBy(s => s.ObservedOn).ToListAsync());
        Assert.All(snapshots, s => Assert.Equal(PriceSource.ManualHistory, s.Source));

        // The table row without a link lands on the product the linked row created, found by its title.
        var crewneck = snapshots.Where(s => s.Product.Url.Contains("midweight-terry-crewneck-hgrey")).ToList();
        Assert.Equal([78.40m, 68.60m], crewneck.Select(s => s.Price));
        Assert.Equal(98m, crewneck[1].ListPrice);
        Assert.Equal("M", crewneck[1].SizeLabel);

        var belt = snapshots.Single(s => s.Product.Title == "Round-buckle belt");
        Assert.Equal(new DateOnly(2026, 11, 18), belt.ObservedOn);
        Assert.Equal(98m, belt.ListPrice);
    }

    [Theory]
    [InlineData("~~$98~~ $68.60", 68.60, 98.0)]
    [InlineData("$58.80 (was $98)", 58.80, 98.0)]
    [InlineData("1,299.00", 1299.0, null)]
    [InlineData("**$54**", 54.0, null)]
    public void Reads_prices_as_written_in_the_doc(string cell, double price, double? struck)
    {
        var (p, s) = ManualPriceImport.ParseMoney(cell);

        Assert.Equal((decimal)price, p);
        Assert.Equal(struck is null ? null : (decimal)struck.Value, s);
    }

    [Fact]
    public void Notes_weigh_less_than_archived_pages()
    {
        DateOnly Day(int i) => new DateOnly(2026, 8, 1).AddDays(i * 15);
        var notes = Enumerable.Range(0, 3).Select(i => new PricePoint(Day(i), 128m, false, Outside: true, PriceSource.ManualHistory.Confidence())).ToList();
        var archived = Enumerable.Range(0, 3).Select(i => new PricePoint(Day(i), 128m, false, Outside: true, PriceSource.Archive.Confidence())).ToList();

        Assert.Equal(DealVerdict.StillLearning, DealsReport.Assess(notes, 98m, 128m).Verdict);
        Assert.Equal(DealVerdict.BuyNow, DealsReport.Assess(archived, 98m, 128m).Verdict);

        var moreNotes = Enumerable.Range(0, 5).Select(i => new PricePoint(Day(i), 128m, false, Outside: true, PriceSource.ManualHistory.Confidence())).ToList();
        Assert.Equal(DealVerdict.BuyNow, DealsReport.Assess(moreNotes, 98m, 128m).Verdict);
    }

    [Theory]
    [InlineData("https://www.patagonia.com/product/mens-better-sweater-fleece-jacket/25528.html?dwvar_25528_color=NENA", true)]
    [InlineData("http://patagonia.com/product/mens-better-sweater-fleece-jacket/25528.html", true)]
    [InlineData("https://www.patagonia.com/product/mens-better-sweater-fleece-jacket/25528.html.js", false)]
    [InlineData("https://www.patagonia.com/product/mens-better-sweater-fleece-vest/25882.html", false)]
    public void Archived_copies_with_a_query_string_are_the_same_page(string archived, bool same) =>
        Assert.Equal(same, WaybackSeedService.SamePage(archived, new Uri("https://www.patagonia.com/product/mens-better-sweater-fleece-jacket/25528.html")));

    [Fact]
    public async Task The_browser_watch_takes_only_shops_set_up_for_it()
    {
        await using var host = new TestHost(ReigningChamp, JCrew);
        await host.InitializeAsync();

        var (accepted, refused, watched) = await host.InScopeAsync(async sp =>
        {
            var watch = sp.GetRequiredService<BrowserWatchService>();
            var ok = await watch.AddAsync(new Uri("https://www.jcrew.com/p/mens/CY231?color=black"), "M", CancellationToken.None);
            var no = await watch.AddAsync(new Uri("https://reigningchamp.com/products/x"), null, CancellationToken.None);
            return (ok, no, await watch.ListAsync(CancellationToken.None));
        });

        Assert.StartsWith("Watching https://www.jcrew.com/p/mens/CY231", accepted);
        Assert.Contains("not a browser-watch shop (those are: jcrew)", refused);
        Assert.Equal("https://www.jcrew.com/p/mens/CY231", Assert.Single(watched).Url);
    }

    [Fact]
    public async Task A_dropped_shop_is_never_collected()
    {
        await using var host = new TestHost(UnderArmour);
        await host.InitializeAsync();

        var run = await host.InScopeAsync(sp => sp.GetRequiredService<PriceCollectionService>().CollectAsync("underarmour", null, CancellationToken.None));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("Dropped", run.Error);
        Assert.Empty(host.Web.Requested);
        var shop = await host.InScopeAsync(sp => sp.GetRequiredService<CatalogDbContext>().Shops.SingleAsync());
        Assert.False(shop.Enabled);
        Assert.True(shop.Dropped);
    }
}
