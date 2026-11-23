using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Collection;

namespace SmartStylist.Modules.Catalog.Tests;

public class PriceSanityCheckTests
{
    private static readonly PriceSanityCheck Check = new(Options.Create(new CatalogOptions
    {
        PriceSanity = new PriceSanityOptions
        {
            DefaultFloor = 3m,
            Floors =
            [
                new PriceFloorOptions { Category = "tees", Keywords = ["tee", "t-shirt"], Min = 5m },
                new PriceFloorOptions { Category = "jeans", Keywords = ["jean", "jeans"], Min = 15m },
            ],
        },
    }));

    private static ScrapedOffer Offer(decimal price, decimal? list = null, string currency = "USD") =>
        new("v1", "M", price, list, currency, true);

    [Fact]
    public void Refuses_uniqlos_sold_out_placeholder_by_the_category_floor() =>
        Assert.Contains("floor for tees", Check.Reject(Offer(1.90m), "Supima Cotton Crew Neck T-Shirt", usualPrice: null));

    [Fact]
    public void Refuses_a_price_under_15_percent_of_the_usual_one() =>
        Assert.Contains("usual 98.00", Check.Reject(Offer(12m), "Midweight Terry Crewneck", usualPrice: 98m));

    [Fact]
    public void Refuses_a_price_under_15_percent_of_its_own_crossed_out_price() =>
        Assert.Contains("crossed-out", Check.Reject(Offer(9m, list: 98m), "Midweight Terry Crewneck", usualPrice: null));

    [Theory]
    [InlineData(29.90, null, "Supima Cotton Crew Neck T-Shirt")]
    [InlineData(19.99, 98.0, "Hoodie")]
    [InlineData(49.99, null, "541 Athletic Taper Jeans")]
    [InlineData(4.00, null, "Ankle socks")]
    public void Keeps_real_prices_and_deep_but_real_sales(decimal price, double? list, string title) =>
        Assert.Null(Check.Reject(Offer(price, list is null ? null : (decimal)list.Value), title, usualPrice: list is null ? null : (decimal)list.Value));

    [Fact]
    public void Floors_apply_only_to_their_own_currency() =>
        Assert.Null(Check.Reject(Offer(4m, currency: "GEL"), "Cotton T-Shirt", usualPrice: null));

    [Fact]
    public void A_keyword_must_be_a_whole_word() =>
        Assert.Equal("any item", Check.FloorFor("Teeny keychain").Category);
}
