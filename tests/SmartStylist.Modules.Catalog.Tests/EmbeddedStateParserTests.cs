using SmartStylist.Modules.Catalog.Collection;

namespace SmartStylist.Modules.Catalog.Tests;

public class EmbeddedStateParserTests
{
    private static readonly Uri PageUrl = new("https://shop.example.com/p/oxford-shirt");

    [Fact]
    public void Reads_sizes_colour_and_was_price_from_next_data()
    {
        const string html = """
            <html><head><title>Oxford</title></head><body>
            <script id="__NEXT_DATA__" type="application/json">
            {"props":{"pageProps":{"navigation":{"name":"Menu"},"product":{
              "id":"OX-100","name":"Oxford Shirt","brand":{"name":"Example"},"color":"Light Blue",
              "price":{"current":{"value":49.5},"currency":"USD"},"originalPrice":{"value":89},
              "currency":"USD",
              "variants":[
                {"sku":"OX-100-LB-M","size":"M","inStock":true},
                {"sku":"OX-100-LB-L","size":"L","inStock":false},
                {"sku":"OX-100-LB-XL","size":"XL","stock":3,"price":"45.00"}
              ]}}}}
            </script></body></html>
            """;

        var product = JsonLdProductParser.Parse(html, PageUrl, "EUR");

        Assert.NotNull(product);
        Assert.Equal("Oxford Shirt", product.Title);
        Assert.Equal("OX-100", product.ExternalId);
        Assert.Equal("Example", product.Brand);
        Assert.Equal(new[] { "M", "L", "XL" }, product.Offers.Select(o => o.SizeLabel));
        Assert.Equal(new[] { 49.5m, 49.5m, 45m }, product.Offers.Select(o => o.Price));
        Assert.All(product.Offers, o => Assert.Equal(89m, o.ListPrice));
        Assert.Equal(new bool?[] { true, false, true }, product.Offers.Select(o => o.InStock));
        Assert.All(product.Offers, o => Assert.Equal("Light Blue", o.Colour));
        Assert.All(product.Offers, o => Assert.Equal("USD", o.Currency));
    }

    [Fact]
    public void Reads_window_initial_state_with_cent_amounts()
    {
        const string html = """
            <script>window.__INITIAL_STATE__ = {"pdp":{"product":{"productId":"9001","title":"Chino \"Slim\" {navy}","price":{"centAmount":7990},
            "wasPrice":{"centAmount":9990},"sizes":[{"label":"32","available":true},{"label":"34","available":false}]}}};
            window.other = 1;</script>
            """;

        var product = JsonLdProductParser.Parse(html, PageUrl, "GBP");

        Assert.NotNull(product);
        Assert.Equal("Chino \"Slim\" {navy}", product.Title);
        Assert.Equal(new[] { "32", "34" }, product.Offers.Select(o => o.SizeLabel));
        Assert.All(product.Offers, o => Assert.Equal(79.90m, o.Price));
        Assert.All(product.Offers, o => Assert.Equal(99.90m, o.ListPrice));
        Assert.All(product.Offers, o => Assert.Equal("GBP", o.Currency));
    }

    [Fact]
    public void Json_ld_still_wins_when_present()
    {
        const string html = """
            <script type="application/ld+json">{"@type":"Product","name":"From JSON-LD","sku":"A1","offers":{"@type":"Offer","price":"10","priceCurrency":"USD"}}</script>
            <script id="__NEXT_DATA__" type="application/json">{"product":{"name":"From state","price":20}}</script>
            """;

        Assert.Equal("From JSON-LD", JsonLdProductParser.Parse(html, PageUrl, "USD")?.Title);
    }

    [Fact]
    public void A_page_without_a_product_gives_nothing()
    {
        const string html = """<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"menu":[{"name":"Men"}]}}}</script>""";

        Assert.Null(JsonLdProductParser.Parse(html, PageUrl, "USD"));
    }
}
