using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Catalog.Collection;

namespace SmartStylist.Modules.Catalog.Tests;

public class BotWallTests
{
    private static readonly Dictionary<string, string> NoHeaders = [];

    [Theory]
    [InlineData(403, "server", "AkamaiGHost", "<H1>Access Denied</H1> Reference #18.5c1f2d17", "Akamai")]
    [InlineData(403, "x-datadome", "protected", "<html></html>", "DataDome")]
    [InlineData(403, "cf-ray", "8f1c", "<title>Just a moment...</title>", "Cloudflare")]
    [InlineData(200, "server", "nginx", "<div id=\"px-captcha\"></div>", "PerimeterX (HUMAN)")]
    [InlineData(403, "server", "nginx", "<script src=\"/_Incapsula_Resource?SWJIYLWA=1\"></script>", "Imperva (Incapsula)")]
    [InlineData(429, "server", "nginx", "Please verify you are human", "an unnamed bot check")]
    public void Names_the_vendor_of_a_block_page(int status, string header, string value, string html, string vendor)
    {
        Assert.Equal(vendor, BotWall.Detect(status, new Dictionary<string, string> { [header] = value }, html));
    }

    [Theory]
    [InlineData(200, "<html><script type=\"application/ld+json\">{\"@type\":\"Product\"}</script></html>")]
    [InlineData(404, "<h1>Page not found</h1>")]
    public void Leaves_normal_pages_alone(int status, string html)
    {
        Assert.Null(BotWall.Detect(status, NoHeaders, html));
    }

    [Fact]
    public void A_cloudflare_shop_that_answers_normally_is_not_blocked()
    {
        Assert.Null(BotWall.Detect(200, new Dictionary<string, string> { ["server"] = "cloudflare", ["cf-ray"] = "1" }, "<html>shop</html>"));
    }

    [Fact]
    public async Task The_http_fetcher_says_who_blocked_it()
    {
        await using var host = new TestHost();
        host.Web.Serve("https://www.kohls.com/product/1", HttpStatusCode.Forbidden,
            "<H1>Access Denied</H1>You don't have permission. Reference #18.abc", ("Server", "AkamaiGHost"));

        await host.InitializeAsync();
        var ex = await Assert.ThrowsAsync<BlockedByBotWallException>(() => host.InScopeAsync(sp =>
            sp.GetRequiredService<HttpPageFetcher>().GetPageAsync(new Uri("https://www.kohls.com/product/1"), CancellationToken.None)));

        Assert.Equal("Akamai", ex.Vendor);
        Assert.Contains("Not solving it", ex.Message);
    }

    [Fact]
    public async Task Detect_reports_the_bot_wall_and_the_platform_behind_it()
    {
        await using var host = new TestHost();
        host.Web.Serve("https://www.adidas.com/us", HttpStatusCode.Forbidden,
            "<H1>Access Denied</H1> Reference #18.1 <img src=\"https://www.adidas.com/on/demandware.static/x.png\">", ("Server", "AkamaiGHost"));

        await host.InitializeAsync();
        var detection = await host.InScopeAsync(sp =>
            sp.GetRequiredService<PlatformDetector>().DetectAsync(new Uri("https://www.adidas.com/us"), CancellationToken.None));

        Assert.Equal(Domain.ShopPlatform.SalesforceCommerceCloud, detection.Platform);
        Assert.Contains("blocked by Akamai", detection.Evidence);
    }
}
