using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Collection;

namespace SmartStylist.Modules.Catalog.Tests;

public class HttpPageFetcherTests
{
    private static readonly EgressRouter NoEgress = new(Options.Create(new CatalogOptions()));

    /// <summary>Answers 200 and reports the request as having landed on another address (a redirect).</summary>
    private sealed class Landing(string? landedOn, string body = "page") : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            var landed = new HttpRequestMessage(HttpMethod.Get, landedOn ?? request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = landed });
        }
    }

    private static CatalogOptions Shops(string? egress, string? proxy, string? geoRetry = null) => new()
    {
        Shops =
        [
            new ShopOptions { Key = "brooksbrothers", Name = "Brooks Brothers", BaseUrl = "https://www.brooksbrothers.com", Egress = egress },
        ],
        Egresses = proxy is null ? [] : new() { ["us"] = new EgressOptions { ProxyUrl = proxy, MinIntervalMs = 0 } },
        GeoRetryEgress = geoRetry,
    };

    [Fact]
    public async Task A_redirect_to_the_country_site_is_reported_as_geo_blocking()
    {
        var direct = new Landing("https://www.brooksbrothers.eu/?utm_source=brooksbrothers.com&utm_medium=redirect");
        var fetcher = new HttpPageFetcher(new HttpClient(direct), new EgressRouter(Options.Create(Shops(null, null))));

        var error = await Assert.ThrowsAsync<GeoRedirectedException>(() =>
            fetcher.GetPageAsync(new Uri("https://www.brooksbrothers.com/Oxford/MG01372,default,pd.html"), CancellationToken.None));

        Assert.Equal("www.brooksbrothers.eu", error.Landed.Host);
        Assert.Contains("geography", error.Message);
    }

    [Fact]
    public async Task A_geo_redirect_is_retried_once_through_the_us_egress()
    {
        var direct = new Landing("https://www.brooksbrothers.eu/");
        var us = new Landing(null, "us page");
        var router = new EgressRouter(Options.Create(Shops(null, "http://proxy.test:3128", geoRetry: "us")), (_, _) => us);
        var fetcher = new HttpPageFetcher(new HttpClient(direct), router);

        var html = await fetcher.GetPageAsync(new Uri("https://www.brooksbrothers.com/p/1"), CancellationToken.None);

        Assert.Equal("us page", html);
        Assert.Single(direct.Requested);
        Assert.Single(us.Requested);
    }

    [Fact]
    public async Task A_shop_with_an_egress_always_goes_through_it_and_never_direct()
    {
        var direct = new Landing(null, "direct");
        var us = new Landing(null, "us page");
        var router = new EgressRouter(Options.Create(Shops("us", "socks5://127.0.0.1:1080")), (_, _) => us);
        var fetcher = new HttpPageFetcher(new HttpClient(direct), router);

        Assert.Equal("us page", await fetcher.GetPageAsync(new Uri("https://brooksbrothers.com/p/1"), CancellationToken.None));
        Assert.Equal("direct", await fetcher.GetPageAsync(new Uri("https://www.taylorstitch.com/p/1"), CancellationToken.None));
        Assert.Single(direct.Requested);
    }

    [Fact]
    public async Task An_egress_without_a_proxy_says_how_to_set_it_up()
    {
        var fetcher = new HttpPageFetcher(new HttpClient(new Landing(null)), new EgressRouter(Options.Create(Shops("us", null))));

        var error = await Assert.ThrowsAsync<EgressNotConfiguredException>(() =>
            fetcher.GetPageAsync(new Uri("https://www.brooksbrothers.com/p/1"), CancellationToken.None));

        Assert.Contains("Catalog__Egresses__us__ProxyUrl", error.Message);
    }

    [Theory]
    [InlineData("https://www.brooksbrothers.com/p/1", "https://www.brooksbrothers.eu/", true)]
    [InlineData("https://www.patagonia.com/product/x/25528.html", "https://eu.patagonia.com/ge/en/product/x/25528.html", true)]
    [InlineData("https://www.uniqlo.com/us/en/products/E1-000/00", "https://www.uniqlo.com/ge/en/", true)]
    [InlineData("https://www.levi.com/US/en_US/p/1", "https://www.levi.com/US/en_US/p/1?x=1", false)]
    [InlineData("https://shop.example.com/p/1", "https://login.othervendor.com/", false)]
    [InlineData("https://www.taylorstitch.com/products/a", "https://taylorstitch.com/products/a", false)]
    public void Recognises_a_country_site_redirect(string requested, string landed, bool geo) =>
        Assert.Equal(geo, GeoRedirect.Detect(new Uri(requested), new Uri(landed)) is not null);

    private sealed class Sequence(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var code = codes[Math.Min(Calls++, codes.Length - 1)];
            var response = new HttpResponseMessage(code) { Content = new StringContent("ok") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Retries_once_after_429()
    {
        var handler = new Sequence(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var fetcher = new HttpPageFetcher(new HttpClient(handler), NoEgress);

        Assert.Equal("ok", await fetcher.GetPageAsync(new Uri("https://shop.example.com/p/1"), CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Gives_up_after_a_second_429()
    {
        var handler = new Sequence(HttpStatusCode.TooManyRequests);
        var fetcher = new HttpPageFetcher(new HttpClient(handler), NoEgress);

        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.GetPageAsync(new Uri("https://shop.example.com/p/1"), CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }
}
