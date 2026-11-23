using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace SmartStylist.Modules.Catalog.Collection;

/// <summary>
/// Reads a product from the app state that JavaScript front ends embed in the page: Next.js __NEXT_DATA__,
/// window.__INITIAL_STATE__, __PRELOADED_STATE__, __APOLLO_STATE__ and JSON __NUXT__ data. The shape differs per shop,
/// so it looks for the object that best resembles a product: a name, a price, and ideally a list of sized variants.
/// </summary>
internal static partial class EmbeddedStateParser
{
    private static readonly HtmlParser HtmlParser = new();

    private static readonly string[] NameKeys = ["name", "title", "productName", "displayName"];
    private static readonly string[] PriceKeys = ["salePrice", "currentPrice", "finalPrice", "price", "sellingPrice", "priceValue"];
    private static readonly string[] ListPriceKeys = ["listPrice", "originalPrice", "compareAtPrice", "compare_at_price", "regularPrice", "wasPrice", "fullPrice", "initialPrice"];
    private static readonly string[] VariantListKeys = ["variants", "skus", "sizes", "variantList", "items", "sizeList"];
    private static readonly string[] SizeKeys = ["size", "sizeLabel", "sizeName", "label", "displaySize", "option1", "name", "title"];
    private static readonly string[] IdKeys = ["sku", "styleCode", "productId", "id", "code", "styleNumber"];
    private static readonly string[] ColourKeys = ["color", "colour", "colorName", "colourName", "colorDescription"];
    private static readonly string[] StockKeys = ["available", "inStock", "isAvailable", "isInStock", "availability", "stock", "quantity", "inventory"];

    [GeneratedRegex(@"(?:window\.)?(__INITIAL_STATE__|__PRELOADED_STATE__|__APOLLO_STATE__|__NUXT__|__STATE__)\s*=\s*", RegexOptions.CultureInvariant)]
    private static partial Regex StateAssignment();

    public static ScrapedProduct? Parse(string html, Uri pageUrl, string fallbackCurrency)
    {
        foreach (var json in StateBlobs(html))
        {
            using (json)
            {
                var best = Candidates(json.RootElement, depth: 0)
                    .Select(c => (Element: c, Score: Score(c)))
                    .Where(c => c.Score > 0)
                    .OrderByDescending(c => c.Score)
                    .Select(c => Map(c.Element, pageUrl, fallbackCurrency))
                    .FirstOrDefault(p => p is not null && p.Offers.Count > 0);
                if (best is not null)
                {
                    return best;
                }
            }
        }

        return null;
    }

    private static IEnumerable<JsonDocument> StateBlobs(string html)
    {
        var document = HtmlParser.ParseDocument(html);
        if (document.QuerySelector("script#__NEXT_DATA__")?.TextContent is { Length: > 0 } nextData && TryParse(nextData) is { } next)
        {
            yield return next;
        }

        foreach (var script in document.QuerySelectorAll("script:not([src])"))
        {
            var text = script.TextContent;
            foreach (Match match in StateAssignment().Matches(text))
            {
                var start = match.Index + match.Length;
                if (start < text.Length && text[start] == '{' && ExtractObject(text, start) is { } objectText && TryParse(objectText) is { } state)
                {
                    yield return state;
                }
            }
        }
    }

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The balanced {...} starting at <paramref name="start"/>, skipping braces inside strings.</summary>
    private static string? ExtractObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return text[start..(i + 1)];
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Candidates(JsonElement element, int depth)
    {
        if (depth > 40)
        {
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var child in Candidates(property.Value, depth + 1))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in Candidates(item, depth + 1))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>How product-like an object is. Zero means not a product.</summary>
    private static int Score(JsonElement e)
    {
        if (FirstString(e, NameKeys) is null || ReadPrice(e, PriceKeys) is null && Variants(e).Count == 0)
        {
            return 0;
        }

        var score = 1;
        if (ReadPrice(e, PriceKeys) is not null) score += 2;
        if (ReadPrice(e, ListPriceKeys) is not null) score += 1;
        if (FirstString(e, IdKeys) is not null) score += 1;
        if (FirstString(e, ["brand", "brandName", "vendor"]) is not null || e.TryGetProperty("brand", out _)) score += 1;
        var variants = Variants(e);
        if (variants.Count > 1) score += 3 + Math.Min(variants.Count, 10);
        return score;
    }

    private static ScrapedProduct? Map(JsonElement e, Uri pageUrl, string fallbackCurrency)
    {
        var title = FirstString(e, NameKeys);
        if (title is null)
        {
            return null;
        }

        var currency = FirstString(e, ["currency", "currencyCode", "priceCurrency"]) ?? fallbackCurrency;
        var productPrice = ReadPrice(e, PriceKeys);
        var productList = ReadPrice(e, ListPriceKeys);
        var productColour = ReadColour(e);
        var offers = new List<ScrapedOffer>();

        foreach (var variant in Variants(e))
        {
            var price = ReadPrice(variant, PriceKeys) ?? productPrice;
            if (price is null)
            {
                continue;
            }

            var list = ReadPrice(variant, ListPriceKeys) ?? productList;
            var size = FirstString(variant, SizeKeys);
            var key = FirstString(variant, IdKeys) ?? size ?? offers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            offers.Add(new ScrapedOffer(key, size, price.Value, list > price ? list : null, currency.ToUpperInvariant(),
                ReadStock(variant), ReadColour(variant) ?? productColour));
        }

        if (offers.Count == 0 && productPrice is not null)
        {
            offers.Add(new ScrapedOffer("", null, productPrice.Value, productList > productPrice ? productList : null,
                currency.ToUpperInvariant(), ReadStock(e), productColour));
        }

        var brand = FirstString(e, ["brandName", "vendor"])
            ?? (e.TryGetProperty("brand", out var b) ? b.ValueKind == JsonValueKind.String ? b.GetString() : FirstString(b, NameKeys) : null);
        var image = FirstString(e, ["image", "imageUrl", "thumbnail", "primaryImage"]);
        return new ScrapedProduct(FirstString(e, IdKeys) ?? pageUrl.ToString(), pageUrl.ToString(), title, brand,
            Uri.TryCreate(pageUrl, image, out var imageUri) && image is not null ? imageUri.ToString() : null, offers);
    }

    private static List<JsonElement> Variants(JsonElement e)
    {
        foreach (var key in VariantListKeys)
        {
            if (e.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var items = list.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.Object && (ReadPrice(v, PriceKeys) is not null || FirstString(v, SizeKeys) is not null))
                    .ToList();
                if (items.Count > 0)
                {
                    return items;
                }
            }
        }

        return [];
    }

    private static decimal? ReadPrice(JsonElement e, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!e.TryGetProperty(key, out var value))
            {
                continue;
            }

            var price = value.ValueKind == JsonValueKind.Object ? PriceObject(value) : PriceParser.Parse(value);
            if (price is not null)
            {
                return price;
            }
        }

        return null;
    }

    /// <summary>{ "value": 59.4 }, { "amount": "59.40" }, { "centAmount": 5940 }, { "current": { "value": 59.4 } }.</summary>
    private static decimal? PriceObject(JsonElement value)
    {
        if (value.TryGetProperty("centAmount", out var cents) && cents.TryGetDecimal(out var c) && c > 0)
        {
            return decimal.Round(c / 100m, 2);
        }

        foreach (var key in new[] { "value", "amount", "current", "sale", "price", "formatted", "raw" })
        {
            if (value.TryGetProperty(key, out var inner))
            {
                var price = inner.ValueKind == JsonValueKind.Object ? PriceObject(inner) : PriceParser.Parse(inner);
                if (price is not null)
                {
                    return price;
                }
            }
        }

        return null;
    }

    private static bool? ReadStock(JsonElement e)
    {
        foreach (var key in StockKeys)
        {
            if (!e.TryGetProperty(key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number when value.TryGetDecimal(out var count):
                    return count > 0;
                case JsonValueKind.String:
                    var text = value.GetString() ?? "";
                    if (text.Contains("OutOfStock", StringComparison.OrdinalIgnoreCase) || text.Contains("SoldOut", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (text.Contains("InStock", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("available", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    break;
            }
        }

        return null;
    }

    private static string? ReadColour(JsonElement e)
    {
        foreach (var key in ColourKeys)
        {
            if (e.TryGetProperty(key, out var value))
            {
                var colour = value.ValueKind == JsonValueKind.String ? value.GetString() : FirstString(value, ["name", "label", "value"]);
                if (!string.IsNullOrWhiteSpace(colour))
                {
                    return colour.Trim();
                }
            }
        }

        return null;
    }

    private static string? FirstString(JsonElement e, string[] keys)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (e.TryGetProperty(key, out var value))
            {
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return null;
    }
}
