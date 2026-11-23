using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Domain;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Prices Uniqlo products from the shop's own commerce API instead of the page: the page's structured data shows a
/// $1.90 placeholder on sold-out items. Product codes come from the page addresses (sitemap or earlier runs):
/// /us/en/products/E422990-000/00 is product E422990-000, price group 00.
/// </summary>
internal sealed partial class UniqloApiCollector(
    HttpPageFetcher fetcher,
    SitemapReader sitemapReader,
    IOptions<CatalogOptions> options,
    ILogger<UniqloApiCollector> logger) : IShopCollector
{
    private const int ProbeProducts = 5;

    public CollectorStrategy Strategy => CollectorStrategy.UniqloApi;

    public async Task<CollectorResult> CollectAsync(ShopRequest request, CancellationToken cancellationToken)
    {
        var pattern = string.IsNullOrWhiteSpace(request.ProductUrlPattern)
            ? null
            : new Regex(request.ProductUrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        var pages = request.KnownProductUrls.Select(u => ProductPage.From(u)).OfType<ProductPage>().ToList();
        string? sitemapError = null;
        if (pages.Count < request.MaxProducts)
        {
            var fromSitemap = await sitemapReader.GetProductUrlsAsync(
                fetcher, request.ShopRoot, request.BaseUrl, request.SitemapUrl, pattern, request.MaxProducts * 2, cancellationToken);
            pages.AddRange(fromSitemap.Urls.Select(u => ProductPage.From(u)).OfType<ProductPage>());
            sitemapError = fromSitemap.LastError;
        }

        pages = pages.DistinctBy(p => (p.Code, p.PriceGroup)).Take(request.MaxProducts).ToList();
        if (pages.Count == 0)
        {
            throw new CollectorUnusableException(
                $"UniqloApi: no /products/<code>/<group> addresses in the sitemap or earlier runs ({sitemapError ?? "set SitemapUrl"})");
        }

        var (region, language) = Locale(request.BaseUrl);
        var products = new List<ScrapedProduct>();
        var failed = 0;
        string? lastError = null;
        foreach (var page in pages)
        {
            if (products.Count == 0 && failed >= ProbeProducts)
            {
                throw new CollectorUnusableException($"UniqloApi: first {ProbeProducts} products gave no price. Last error: {lastError}");
            }

            var productApi = $"/{region}/api/commerce/v5/{language}/products/{page.Code}/price-groups/{page.PriceGroup}";
            try
            {
                var json = await fetcher.GetPageAsync(new Uri(request.ShopRoot, $"{productApi}/l2s?withPrices=true&withStocks=true"), cancellationToken);
                // The name and colour names; also what the price sanity check needs to know the category.
                UniqloDetails? details = null;
                try
                {
                    await Task.Delay(options.Value.RequestDelayMs, cancellationToken);
                    details = UniqloL2sParser.ParseDetails(await fetcher.GetPageAsync(
                        new Uri(request.ShopRoot, $"{productApi}/details?includeModelSize=false&httpFailure=true"), cancellationToken));
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogDebug("Uniqlo details failed for {Code}: {Error}", page.Code, SitemapReader.FirstLine(ex.Message));
                }

                var product = UniqloL2sParser.Parse(json, page.Code, page.Url, request.Currency, details);
                if (product is { Offers.Count: > 0 })
                {
                    products.Add(product);
                }
                else
                {
                    failed++;
                    lastError = "no sizes with a price in the answer";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failed++;
                lastError = SitemapReader.FirstLine(ex.Message);
                logger.LogDebug("Uniqlo API failed for {Code}: {Error}", page.Code, lastError);
            }

            await Task.Delay(options.Value.RequestDelayMs, cancellationToken);
        }

        if (products.Count == 0)
        {
            throw new CollectorUnusableException($"UniqloApi: none of {pages.Count} products gave a price. Last error: {lastError}");
        }

        return new CollectorResult(products, failed);
    }

    /// <summary>"/us/en" in https://www.uniqlo.com/us/en gives region "us" and language "en".</summary>
    private static (string Region, string Language) Locale(Uri baseUrl)
    {
        var segments = baseUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments.ElementAtOrDefault(0) ?? "us", segments.ElementAtOrDefault(1) ?? "en");
    }

    internal sealed record ProductPage(string Code, string PriceGroup, string Url)
    {
        public static ProductPage? From(string url)
        {
            var match = ProductPath().Match(url);
            return match.Success
                ? new ProductPage(match.Groups["code"].Value.ToUpperInvariant(), match.Groups["group"].Success ? match.Groups["group"].Value : "00",
                    url.Split('?')[0])
                : null;
        }
    }

    [GeneratedRegex(@"/products/(?<code>[A-Za-z]\d{6}-\d{3})(?:/(?<group>\d{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex ProductPath();
}

/// <summary>A product's name and colour names (display code to name, "09" to "BLACK").</summary>
internal sealed record UniqloDetails(string Name, IReadOnlyDictionary<string, string> Colours);

/// <summary>
/// Reads the answer of Uniqlo's l2s endpoint: one "l2" per colour and size, with prices and stock keyed by its l2Id.
/// A promo price is today's price and the base price becomes the crossed-out one.
/// </summary>
internal static class UniqloL2sParser
{
    /// <summary>Uniqlo's size display codes. Codes not listed keep the code itself.</summary>
    private static readonly Dictionary<string, string> Sizes = new()
    {
        ["001"] = "XXS", ["002"] = "XS", ["003"] = "S", ["004"] = "M", ["005"] = "L", ["006"] = "XL", ["007"] = "XXL", ["008"] = "3XL",
        ["009"] = "4XL",
    };

    public static ScrapedProduct? Parse(string json, string productCode, string pageUrl, string fallbackCurrency, UniqloDetails? details = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = root.TryGetProperty("result", out var r) ? r : root;
        if (!result.TryGetProperty("l2s", out var l2s) || l2s.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var prices = result.TryGetProperty("prices", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var stocks = result.TryGetProperty("stocks", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

        var offers = new List<ScrapedOffer>();
        foreach (var l2 in l2s.EnumerateArray())
        {
            var id = Text(l2, "l2Id");
            var colour = Code(l2, "color");
            var sizeCode = Code(l2, "size");
            var price = ById(prices, id) ?? (l2.TryGetProperty("prices", out var inline) ? inline : default);
            var basePrice = Amount(price, "base");
            var promoPrice = Amount(price, "promo");
            var today = promoPrice ?? basePrice;
            if (today is null)
            {
                continue;
            }

            var size = sizeCode is null ? null : Sizes.GetValueOrDefault(sizeCode, sizeCode);
            offers.Add(new ScrapedOffer(
                VariantKey: id ?? $"{colour}-{sizeCode}",
                SizeLabel: size,
                Price: today.Value,
                ListPrice: promoPrice is not null && basePrice > promoPrice ? basePrice : null,
                Currency: CurrencyOf(price) ?? fallbackCurrency,
                InStock: InStock(ById(stocks, id) ?? (l2.TryGetProperty("stock", out var st) ? st : default)),
                Colour: colour is not null && details?.Colours.GetValueOrDefault(colour) is { } name ? $"{colour} {name}" : colour));
        }

        return offers.Count == 0 ? null : new ScrapedProduct(productCode, pageUrl, Title: details?.Name ?? "", Brand: "UNIQLO", ImageUrl: null, offers);
    }

    /// <summary>The details endpoint: {"result": {"name": "...", "colors": [{"displayCode": "09", "name": "BLACK"}]}}.</summary>
    public static UniqloDetails? ParseDetails(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement.TryGetProperty("result", out var r) ? r : document.RootElement;
        var name = Text(result, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var colours = new Dictionary<string, string>();
        if (result.TryGetProperty("colors", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var colour in list.EnumerateArray())
            {
                if (Text(colour, "displayCode") is { } code && Text(colour, "name") is { } colourName)
                {
                    colours[code] = colourName;
                }
            }
        }

        return new UniqloDetails(name.Trim(), colours);
    }

    private static JsonElement? ById(JsonElement map, string? id) =>
        id is not null && map.ValueKind == JsonValueKind.Object && map.TryGetProperty(id, out var value) ? value : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>{"displayCode": "004", "code": "SMA004"} gives "004".</summary>
    private static string? Code(JsonElement l2, string name) =>
        l2.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, "displayCode") ?? Text(value, "code")
            : null;

    /// <summary>{"base": {"value": 29.9, "currency": {"code": "USD"}}}; a null promo means no promo.</summary>
    private static decimal? Amount(JsonElement price, string name) =>
        price.ValueKind == JsonValueKind.Object && price.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
                                                && value.TryGetProperty("value", out var amount)
            ? PriceParser.Parse(amount)
            : null;

    private static string? CurrencyOf(JsonElement price)
    {
        if (price.ValueKind != JsonValueKind.Object || !price.TryGetProperty("base", out var basePrice) || basePrice.ValueKind != JsonValueKind.Object
            || !basePrice.TryGetProperty("currency", out var currency))
        {
            return null;
        }

        var code = currency.ValueKind == JsonValueKind.Object ? Text(currency, "code") : currency.GetString();
        return code is { Length: 3 } ? code.ToUpperInvariant() : null;
    }

    private static bool? InStock(JsonElement stock)
    {
        if (stock.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var status = Text(stock, "statusCode");
        if (status is not null)
        {
            return !status.Contains("OUT", StringComparison.OrdinalIgnoreCase);
        }

        return stock.TryGetProperty("quantity", out var quantity) && quantity.TryGetInt32(out var count) ? count > 0 : null;
    }
}
