using System.Net;
using System.Text.Json;
using SmartStylist.Modules.Catalog.Domain;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>What one request to a shop returned: enough to recognise the platform.</summary>
public sealed record PlatformProbe(string Html, IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> CookieNames);

/// <param name="Evidence">Why we think so, e.g. "x-shopid header" or "/products.json answers".</param>
public sealed record PlatformDetection(ShopPlatform Platform, string Evidence);

/// <summary>
/// Recognises a shop's platform from its home page, headers and cookies, plus a check of /products.json
/// for Shopify, which is the cleanest source when it answers.
/// </summary>
public sealed class PlatformDetector(IHttpClientFactory httpClients, EgressRouter egress)
{
    public async Task<PlatformDetection> DetectAsync(Uri shopRoot, CancellationToken cancellationToken)
    {
        var http = httpClients.CreateClient(HttpPageFetcher.ClientName);
        var route = egress.EgressFor(shopRoot);
        using var response = route is null ? await http.GetAsync(shopRoot, cancellationToken) : await egress.GetAsync(route, shopRoot, cancellationToken);
        if (GeoRedirect.Detect(shopRoot, response.RequestMessage?.RequestUri) is { } landed)
        {
            return new PlatformDetection(ShopPlatform.Unknown, $"redirected to {landed.Host}{landed.AbsolutePath}: blocked by geography, needs a US egress");
        }

        var headers = response.Headers.Concat(response.Content.Headers)
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key.ToLowerInvariant(), g => string.Join(", ", g.SelectMany(h => h.Value)));
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? setCookies.Select(c => c.Split('=', 2)[0].Trim()).ToList()
            : [];
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var wall = BotWall.Detect((int)response.StatusCode, headers, html);
        if (wall is not null || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or (HttpStatusCode)418)
        {
            // The block page often still names the platform (Demandware paths, Shopify CDN), so try anyway.
            var behind = Detect(new PlatformProbe(html, headers, cookies));
            return new PlatformDetection(behind.Platform,
                $"home page answered {(int)response.StatusCode}, blocked by {wall ?? "an unnamed bot check"}" +
                (behind.Platform == ShopPlatform.Unknown ? "" : $"; {behind.Evidence}"));
        }

        var detection = Detect(new PlatformProbe(html, headers, cookies));
        if (route is null && detection.Platform is ShopPlatform.Unknown or ShopPlatform.Shopify && await HasShopifyFeedAsync(http, shopRoot, cancellationToken))
        {
            return new PlatformDetection(ShopPlatform.Shopify, "/products.json answers");
        }

        return detection;
    }

    /// <summary>Signs in the page itself. Most specific first: a Next.js front end on Shopify is still Shopify.</summary>
    public static PlatformDetection Detect(PlatformProbe probe)
    {
        var html = probe.Html;
        bool Header(string name) => probe.Headers.ContainsKey(name);
        bool Cookie(string prefix) => probe.CookieNames.Any(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        bool Has(string text) => html.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Header("x-shopid") || Header("x-shopify-stage") || Cookie("_shopify") || Has("cdn.shopify.com") || Has("Shopify.theme"))
        {
            return new PlatformDetection(ShopPlatform.Shopify, "Shopify header, cookie or CDN in the page");
        }

        if (Has("/on/demandware.store/") || Has("demandware.static") || Has("dwvar_") || Cookie("dwsid") || Cookie("dwanonymous"))
        {
            return new PlatformDetection(ShopPlatform.SalesforceCommerceCloud, "Demandware paths or cookies");
        }

        if (Has("Magento_") || Has("mage/cookies") || Has("\"Magento\"") || Cookie("mage-") || Header("x-magento-tags") || Header("x-magento-cache-debug"))
        {
            return new PlatformDetection(ShopPlatform.Magento, "Magento scripts, cookies or headers");
        }

        if (Has("woocommerce") || Has("/wp-json/wc/"))
        {
            return new PlatformDetection(ShopPlatform.WooCommerce, "WooCommerce in the page");
        }

        if (Has("cdn11.bigcommerce.com") || Has("BCData") || Header("x-bc-storefront"))
        {
            return new PlatformDetection(ShopPlatform.BigCommerce, "BigCommerce CDN or data");
        }

        if (Has("prestashop") || Cookie("PrestaShop-"))
        {
            return new PlatformDetection(ShopPlatform.PrestaShop, "PrestaShop in the page or cookies");
        }

        if (Has("id=\"__NEXT_DATA__\"") || Has("/_next/static/"))
        {
            return new PlatformDetection(ShopPlatform.NextJs, "Next.js app (__NEXT_DATA__)");
        }

        if (Has("window.__NUXT__") || Has("/_nuxt/"))
        {
            return new PlatformDetection(ShopPlatform.Nuxt, "Nuxt app (__NUXT__)");
        }

        return new PlatformDetection(ShopPlatform.Unknown, "no known platform signs");
    }

    private static async Task<bool> HasShopifyFeedAsync(HttpClient http, Uri shopRoot, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(shopRoot, "/products.json?limit=1"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }
}
