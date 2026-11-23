using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Reads Uniqlo prices from Uniqlo's own commerce API instead of the page's embedded data, which carries
/// placeholder prices. One call per product gives every colour and size (an "l2") with base price, promo price and stock.
/// </summary>
internal sealed partial class UniqloApiCollector(
    HttpPageFetcher http,
    SitemapReader sitemapReader,
    ILogger<UniqloApiCollector> logger) : IShopCollector
{
    private const int ProbeProducts = 5;

    /// <summary>Uniqlo's size codes. 001-008 are letter sizes; anything else is kept as the code.</summary>
    private static readonly Dictionary<string, string> Sizes = new()
    {
        ["001"] = "XXS", ["002"] = "XS", ["003"] = "S", ["004"] = "M", ["005"] = "L", ["006"] = "XL", ["007"] = "XXL", ["008"] = "3XL",
    };

    public Domain.CollectorStrategy Strategy => Domain.CollectorStrategy.UniqloApi;

    public async Task<CollectorResult> CollectAsync(ShopRequest request, CancellationToken cancellationToken)
    {
        var fetcher = request.Http ?? http;
        var ids = request.KnownProductUrls.Select(ProductId).OfType<string>().Distinct().ToList();
        if (ids.Count < request.MaxProducts)
        {
            var pattern = string.IsNullOrWhiteSpace(request.ProductUrlPattern) ? null : new Regex(request.ProductUrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var sitemap = await sitemapReader.GetProductUrlsAsync(fetcher, request.ShopRoot, request.BaseUrl, request.SitemapUrl, pattern, request.MaxProducts * 2, cancellationToken);
            ids.AddRange(sitemap.Urls.Select(ProductId).OfType<string>().Except(ids).Take(request.MaxProducts - ids.Count));
        }

        if (ids.Count == 0)
        {
            throw new CollectorUnusableException("UniqloApi: no product ids (E123456-000) found in known URLs or the sitemap");
        }

        var products = new List<ScrapedProduct>();
        var failed = 0;
        string? lastError = null;
        foreach (var id in ids.Take(request.MaxProducts))
        {
            if (products.Count == 0 && failed >= ProbeProducts)
            {
                throw new CollectorUnusableException($"UniqloApi: first {ProbeProducts} products gave no price. Last error: {lastError}");
            }

            try
            {
                var prices = await fetcher.GetPageAsync(PricesUrl(request.BaseUrl, id), cancellationToken);
                string? details = null;
                try
                {
                    details = await fetcher.GetPageAsync(DetailsUrl(request.BaseUrl, id), cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogDebug("Uniqlo details for {Id} failed: {Error}", id, ex.Message);
                }

                var product = Parse(id, prices, details, request.BaseUrl, request.Currency);
                if (product is { Offers.Count: > 0 })
                {
                    products.Add(product);
                }
                else
                {
                    failed++;
                    lastError = "no prices in the API answer";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failed++;
                lastError = SitemapReader.FirstLine(ex.Message);
            }

            await Task.Delay(request.DelayMs, cancellationToken);
        }

        if (products.Count == 0)
        {
            throw new CollectorUnusableException($"UniqloApi: none of {ids.Count} products gave a price. Last error: {lastError}");
        }

        return new CollectorResult(products, failed);
    }

    /// <summary>"E422990-000" from a page address such as /us/en/products/E422990-000/00 or /products/E422990-001/00.</summary>
    internal static string? ProductId(string url)
    {
        var match = ProductIdPattern().Match(url);
        return match.Success ? $"{match.Groups[1].Value.ToUpperInvariant()}-000" : null;
    }

    /// <summary>The API root for the shop's region and language, e.g. https://www.uniqlo.com/us/api/commerce/v5/en.</summary>
    internal static string ApiRoot(Uri baseUrl)
    {
        var segments = baseUrl.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var region = segments.Length > 0 ? segments[0] : "us";
        var language = segments.Length > 1 ? segments[1] : "en";
        return $"{baseUrl.GetLeftPart(UriPartial.Authority)}/{region}/api/commerce/v5/{language}";
    }

    internal static Uri PricesUrl(Uri baseUrl, string id) =>
        new($"{ApiRoot(baseUrl)}/products/{id}/price-groups/00/l2s?withPrices=true&withStocks=true&httpFailure=true");

    internal static Uri DetailsUrl(Uri baseUrl, string id) =>
        new($"{ApiRoot(baseUrl)}/products/{id}/price-groups/00/details?httpFailure=true");

    internal static ScrapedProduct? Parse(string id, string pricesJson, string? detailsJson, Uri baseUrl, string fallbackCurrency)
    {
        using var prices = JsonDocument.Parse(pricesJson);
        if (!prices.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("l2s", out var l2s) || l2s.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        result.TryGetProperty("prices", out var priceMap);
        result.TryGetProperty("stocks", out var stockMap);

        string title = id;
        var colourNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? image = null;
        if (detailsJson is not null)
        {
            using var details = JsonDocument.Parse(detailsJson);
            if (details.RootElement.TryGetProperty("result", out var d))
            {
                title = Text(d, "name") ?? id;
                if (d.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in colors.EnumerateArray())
                    {
                        if (Text(c, "displayCode") is { } code && Text(c, "name") is { } name)
                        {
                            colourNames[code] = name;
                        }
                    }
                }

                if (d.TryGetProperty("images", out var images) && images.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.Object)
                {
                    image = main.EnumerateObject().Select(p => Text(p.Value, "image")).FirstOrDefault(i => i is not null);
                }
            }
        }

        var offers = new List<ScrapedOffer>();
        foreach (var l2 in l2s.EnumerateArray())
        {
            var l2Id = Text(l2, "l2Id");
            if (l2Id is null || priceMap.ValueKind != JsonValueKind.Object || !priceMap.TryGetProperty(l2Id, out var price))
            {
                continue;
            }

            var basePrice = Amount(price, "base");
            var promo = Amount(price, "promo");
            var now = promo ?? basePrice;
            if (now is null)
            {
                continue;
            }

            var currency = price.TryGetProperty("base", out var b) && b.TryGetProperty("currency", out var cur) ? Text(cur, "code") : null;
            var sizeCode = l2.TryGetProperty("size", out var size) ? Text(size, "displayCode") : null;
            var colourCode = l2.TryGetProperty("color", out var color) ? Text(color, "displayCode") : null;
            bool? inStock = null;
            if (stockMap.ValueKind == JsonValueKind.Object && stockMap.TryGetProperty(l2Id, out var stock))
            {
                inStock = Text(stock, "statusCode") is { } status
                    ? !status.Contains("OUT", StringComparison.OrdinalIgnoreCase)
                    : stock.TryGetProperty("quantity", out var q) && q.TryGetDecimal(out var n) ? n > 0 : null;
            }

            offers.Add(new ScrapedOffer(
                l2Id,
                sizeCode is null ? null : Sizes.GetValueOrDefault(sizeCode, sizeCode),
                now.Value,
                basePrice > now ? basePrice : null,
                currency ?? fallbackCurrency,
                inStock,
                colourCode is null ? null : colourNames.GetValueOrDefault(colourCode, colourCode)));
        }

        var url = $"{baseUrl.GetLeftPart(UriPartial.Authority)}{baseUrl.AbsolutePath.TrimEnd('/')}/products/{id}/00";
        return new ScrapedProduct(id, url, title, "Uniqlo", image, offers);
    }

    private static decimal? Amount(JsonElement price, string name) =>
        price.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("value", out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : PriceParser.Parse(v.ToString())
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

    [GeneratedRegex(@"/products/(E?\d{6})-\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex ProductIdPattern();
}
