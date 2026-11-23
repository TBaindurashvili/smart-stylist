using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;

namespace SmartStylist.Modules.Catalog.Tests;

public class PlatformDetectorTests
{
    private static PlatformDetection Detect(string html, Dictionary<string, string>? headers = null, params string[] cookies) =>
        PlatformDetector.Detect(new PlatformProbe(html, headers ?? [], cookies));

    [Theory]
    [InlineData("<script src=\"//cdn.shopify.com/s/files/theme.js\"></script>", ShopPlatform.Shopify)]
    [InlineData("<a href=\"/on/demandware.store/Sites-US-Site/en_US/Cart-Show\">cart</a>", ShopPlatform.SalesforceCommerceCloud)]
    [InlineData("<script type=\"text/x-magento-init\">{\"*\":{\"Magento_Ui/js/core/app\":{}}}</script>", ShopPlatform.Magento)]
    [InlineData("<body class=\"woocommerce-page\">", ShopPlatform.WooCommerce)]
    [InlineData("<script src=\"https://cdn11.bigcommerce.com/s-abc/theme.js\"></script>", ShopPlatform.BigCommerce)]
    [InlineData("<script id=\"__NEXT_DATA__\" type=\"application/json\">{}</script>", ShopPlatform.NextJs)]
    [InlineData("<script>window.__NUXT__=(function(a){return {}})(1)</script>", ShopPlatform.Nuxt)]
    [InlineData("<html><body>Hello</body></html>", ShopPlatform.Unknown)]
    public void Recognises_the_platform_from_the_page(string html, ShopPlatform expected) => Assert.Equal(expected, Detect(html).Platform);

    [Fact]
    public void Shopify_wins_over_a_nextjs_front_end()
    {
        var html = "<script id=\"__NEXT_DATA__\" type=\"application/json\">{}</script>";

        Assert.Equal(ShopPlatform.Shopify, Detect(html, new() { ["x-shopid"] = "12345" }).Platform);
    }

    [Fact]
    public void Cookies_count_too() => Assert.Equal(ShopPlatform.SalesforceCommerceCloud, Detect("<html></html>", null, "dwsid").Platform);
}
