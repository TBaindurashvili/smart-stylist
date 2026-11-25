using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SmartStylist.Modules.Catalog;
using SmartStylist.Modules.Catalog.Catalogue;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;
using SmartStylist.Modules.Catalog.Reporting;
using SmartStylist.Modules.Picks.Persistence;
using SmartStylist.Modules.Picks.Telegram;
using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Persistence;
using SmartStylist.Modules.Wardrobe.Style;

namespace SmartStylist.Modules.Picks.Tests;

/// <summary>The daily run end to end: Catalog prices + a Wardrobe profile in, one Telegram message out, once.</summary>
public sealed class DailyPicksTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 11, 25);

    private readonly SqliteConnection _catalogDb = new("DataSource=:memory:");
    private readonly SqliteConnection _wardrobeDb = new("DataSource=:memory:");
    private readonly SqliteConnection _picksDb = new("DataSource=:memory:");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 25, 6, 30, 0, TimeSpan.Zero));
    private readonly FakeTelegram _telegram = new();

    private CatalogDbContext _catalog = null!;
    private WardrobeDbContext _wardrobe = null!;
    private PicksDbContext _picks = null!;

    public async Task InitializeAsync()
    {
        foreach (var connection in new[] { _catalogDb, _wardrobeDb, _picksDb })
        {
            connection.Open();
        }

        _catalog = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(_catalogDb).UseSnakeCaseNamingConvention().Options);
        _wardrobe = new WardrobeDbContext(new DbContextOptionsBuilder<WardrobeDbContext>().UseSqlite(_wardrobeDb).UseSnakeCaseNamingConvention().Options);
        _picks = new PicksDbContext(new DbContextOptionsBuilder<PicksDbContext>().UseSqlite(_picksDb).UseSnakeCaseNamingConvention().Options);
        await _catalog.Database.EnsureCreatedAsync();
        await _wardrobe.Database.EnsureCreatedAsync();
        await _picks.Database.EnsureCreatedAsync();

        var shop = new Shop { Key = "uniqlo", Name = "Uniqlo", BaseUrl = "https://www.uniqlo.com/us/en/", Currency = "USD" };
        _catalog.Shops.Add(shop);
        AddProduct(shop, "Extra Fine Merino Crew Neck Sweater", "Uniqlo", usual: 49.90m, today: 29.90m);          // new low: buy now
        AddProduct(shop, "Supima Cotton Crew Neck T-Shirt", "Uniqlo", usual: 14.90m, today: 9.90m, lowEarlier: 7.90m); // was cheaper: wait
        AddProduct(shop, "Logo Hoodie", "NoName", usual: 40m, today: 10m);                                          // not a quality brand
        await _catalog.SaveChangesAsync();

        _wardrobe.StyleProfiles.Add(new StyleProfile { Name = "Temo", ShopsFor = ShopsFor.Men, LikedStyles = ["minimal"], TopSize = "M" });
        await _wardrobe.SaveChangesAsync();
    }

    [Fact]
    public async Task Links_the_chat_tells_the_buy_now_pick_once_and_never_a_wait_or_unknown_brand()
    {
        var bot = Bot();
        var profileId = (await _wardrobe.StyleProfiles.SingleAsync()).Id;
        var link = await bot.LinkAsync(profileId, CancellationToken.None);
        Assert.StartsWith("https://t.me/stylist_bot?start=", link);
        _telegram.Incoming.Add($"/start {link!.Split('=')[1]}");

        var first = await Picks(bot).SendAsync(CancellationToken.None);

        var sent = Assert.Single(first);
        Assert.Equal(1, sent.Told);
        var message = _telegram.Sent.Last();
        Assert.Contains("Extra Fine Merino", message);
        Assert.Contains("Size: M is their usual tops size", message);
        Assert.DoesNotContain("Supima", message);
        Assert.DoesNotContain("Logo Hoodie", message);

        var messagesBefore = _telegram.Sent.Count;
        var second = await Picks(Bot()).SendAsync(CancellationToken.None);

        Assert.Equal(0, Assert.Single(second).Told);
        Assert.Equal(1, second[0].AlreadyTold);
        Assert.Equal(messagesBefore, _telegram.Sent.Count);
    }

    [Fact]
    public async Task Preview_explains_why_the_others_are_held_back()
    {
        var profileId = (await _wardrobe.StyleProfiles.SingleAsync()).Id;

        var picks = await Picks(Bot()).PreviewAsync(profileId, CancellationToken.None);

        Assert.Equal(2, picks.Count); // the unknown brand never reaches the rule
        Assert.True(picks[0].Judgement.Tell);
        Assert.StartsWith("Price: Wait", picks[1].Judgement.HeldBack);
    }

    [Fact]
    public async Task Buy_now_is_right_when_the_price_did_not_go_lower_within_30_days()
    {
        var products = await _catalog.Products.ToDictionaryAsync(p => p.Title, p => p.Id);
        SentPick(products["Extra Fine Merino Crew Neck Sweater"], 45m, daysAgo: 35); // stayed at 49.90: right
        SentPick(products["Supima Cotton Crew Neck T-Shirt"], 14.90m, daysAgo: 35);  // 7.90 later: wrong
        SentPick(999, 20m, daysAgo: 35);                                             // never seen again
        SentPick(products["Extra Fine Merino Crew Neck Sweater"], 29.90m, daysAgo: 0); // too new to judge
        await _picks.SaveChangesAsync();

        var outcome = await new PickOutcomes(_picks, new PriceHistoryService(_catalog), _time).SummarizeAsync(null, CancellationToken.None);

        Assert.Equal(new OutcomeSummary(4, 1, 1, 1, 1), outcome);
        Assert.StartsWith("Buy now was right 1 of 2 times", outcome.Describe());
    }

    private void SentPick(long productId, decimal price, int daysAgo) => _picks.SentPicks.Add(new Domain.SentPick
    {
        ProfileId = 1, ProductId = productId, Title = "x", Url = "x", Price = price, Currency = "USD", SentOn = Today.AddDays(-daysAgo),
    });

    private void AddProduct(Shop shop, string title, string brand, decimal usual, decimal today, decimal? lowEarlier = null)
    {
        var product = new Product { Shop = shop, ExternalId = title, Url = $"https://www.uniqlo.com/{title.Replace(' ', '-')}", Title = title, Brand = brand };
        for (var day = 30; day >= 1; day--)
        {
            var price = day is >= 5 and <= 14 && lowEarlier is { } low ? low : usual; // on sale a third of the time
            product.Snapshots.Add(Snapshot(Today.AddDays(-day), price, null));
        }

        product.Snapshots.Add(Snapshot(Today, today, usual));
        _catalog.Products.Add(product);
    }

    private static PriceSnapshot Snapshot(DateOnly day, decimal price, decimal? listPrice) =>
        new() { VariantKey = "", ObservedOn = day, Price = price, ListPrice = listPrice, Currency = "USD" };

    private TelegramBot Bot() => new(new HttpClient(_telegram),
        Options.Create(new PicksOptions { Telegram = new TelegramOptions { BotToken = "test-token", BotName = "stylist_bot" } }),
        _picks, _time, NullLogger<TelegramBot>.Instance);

    private DailyPicks Picks(TelegramBot bot)
    {
        var catalogue = new QualityCatalogue(Options.Create(new CatalogOptions
        {
            Brands = [new BrandOptions { Key = "uniqlo", Name = "Uniqlo", For = [Audience.Men, Audience.Women], Styles = ["minimal", "basics"], Categories = ["knitwear", "basics"] }],
        }));
        return new DailyPicks(new DealsReport(_catalog, _time), catalogue, new StyleProfiles(_wardrobe, _time), new FitAdvisor(_wardrobe),
            _picks, bot, _time, NullLogger<DailyPicks>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _catalog.DisposeAsync();
        await _wardrobe.DisposeAsync();
        await _picks.DisposeAsync();
        foreach (var connection in new[] { _catalogDb, _wardrobeDb, _picksDb })
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>The Bot API: getUpdates hands out <see cref="Incoming"/> until confirmed; sendMessage is recorded.</summary>
    private sealed class FakeTelegram : HttpMessageHandler
    {
        private long _nextUpdate = 1;

        public List<string> Incoming { get; } = [];

        public List<string> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
            object? result = null;
            if (request.RequestUri!.AbsolutePath.EndsWith("/getUpdates", StringComparison.Ordinal))
            {
                if (body.TryGetProperty("offset", out _))
                {
                    Incoming.Clear();
                }

                result = Incoming.Select(text => new { update_id = _nextUpdate++, message = new { chat = new { id = 42L }, text } }).ToArray();
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                Sent.Add(body.GetProperty("text").GetString()!);
                result = new { chat = new { id = body.GetProperty("chat_id").GetInt64() }, text = "" };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
