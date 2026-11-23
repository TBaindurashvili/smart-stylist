using System.Globalization;
using SmartStylist.Modules.Catalog.Collection;

namespace SmartStylist.Modules.Catalog.Affiliates;

/// <summary>One feed from Awin's feed list.</summary>
public sealed record AwinFeedInfo(int AdvertiserId, string AdvertiserName, string Region, string MembershipStatus, int FeedId,
    string FeedName, string Language, int Products, string? DownloadUrl)
{
    /// <summary>Awin writes "active" (or "joined") for programmes that accepted you.</summary>
    public bool Joined => MembershipStatus.Equals("active", StringComparison.OrdinalIgnoreCase)
        || MembershipStatus.Equals("joined", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A product from an Awin feed, all sizes together, with the advertiser it belongs to.</summary>
public sealed record AwinProduct(int AdvertiserId, string AdvertiserName, string? AffiliateUrl, ScrapedProduct Product);

/// <summary>Reads Awin's CSV feed list and product feeds. Columns are found by header name, so their order doesn't matter.</summary>
public static class AwinFeed
{
    /// <summary>Columns asked for in the product feed download.</summary>
    public static readonly string[] Columns =
    [
        "aw_product_id", "merchant_product_id", "parent_product_id", "merchant_id", "merchant_name", "product_name",
        "brand_name", "aw_deep_link", "merchant_deep_link", "aw_image_url", "merchant_image_url", "search_price",
        "rrp_price", "store_price", "currency", "in_stock", "Fashion:size", "colour",
    ];

    public static async Task<IReadOnlyList<AwinFeedInfo>> ParseFeedListAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var feeds = new List<AwinFeedInfo>();
        Header? header = null;
        await foreach (var row in CsvReader.ReadAsync(reader, cancellationToken))
        {
            if (header is null)
            {
                header = new Header(row);
                continue;
            }

            if (!int.TryParse(header.Get(row, "Feed ID"), CultureInfo.InvariantCulture, out var feedId))
            {
                continue;
            }

            feeds.Add(new AwinFeedInfo(
                Int(header.Get(row, "Advertiser ID")),
                header.Get(row, "Advertiser Name") ?? "",
                header.Get(row, "Primary Region") ?? "",
                header.Get(row, "Membership Status") ?? "",
                feedId,
                header.Get(row, "Feed Name") ?? "",
                header.Get(row, "Language") ?? "",
                Int(header.Get(row, "No of products")),
                header.Get(row, "URL")));
        }

        return feeds;
    }

    /// <summary>
    /// Groups feed rows into products. Fashion feeds often have one row per size, tied together by parent_product_id.
    /// Stops taking new products after <paramref name="maxProducts"/>.
    /// </summary>
    public static async Task<IReadOnlyList<AwinProduct>> ParseProductsAsync(TextReader reader, int maxProducts, CancellationToken cancellationToken)
    {
        var products = new Dictionary<string, (AwinProduct First, List<ScrapedOffer> Offers)>();
        Header? header = null;
        await foreach (var row in CsvReader.ReadAsync(reader, cancellationToken))
        {
            if (header is null)
            {
                header = new Header(row);
                continue;
            }

            var mapped = MapRow(header, row);
            if (mapped is null)
            {
                continue;
            }

            var (product, offer) = mapped.Value;
            if (products.TryGetValue(product.Product.ExternalId, out var existing))
            {
                existing.Offers.Add(offer);
            }
            else if (products.Count < maxProducts)
            {
                products[product.Product.ExternalId] = (product, new List<ScrapedOffer> { offer });
            }
        }

        return products.Values
            .Select(p => p.First with { Product = p.First.Product with { Offers = p.Offers } })
            .ToList();
    }

    private static (AwinProduct, ScrapedOffer)? MapRow(Header header, IReadOnlyList<string> row)
    {
        var id = header.Get(row, "merchant_product_id") ?? header.Get(row, "aw_product_id");
        var price = Money(header.Get(row, "search_price")) ?? Money(header.Get(row, "store_price"));
        var url = header.Get(row, "merchant_deep_link") ?? header.Get(row, "aw_deep_link");
        var title = header.Get(row, "product_name");
        if (id is null || price is not > 0m || url is null || title is null)
        {
            return null;
        }

        var rrp = Money(header.Get(row, "rrp_price"));
        var size = header.Get(row, "Fashion:size");
        var inStock = header.Get(row, "in_stock") switch
        {
            "1" or "yes" or "true" => true,
            "0" or "no" or "false" => false,
            _ => (bool?)null,
        };

        var offer = new ScrapedOffer(id, size, price.Value, rrp > price ? rrp : null,
            (header.Get(row, "currency") ?? "").ToUpperInvariant(), inStock, header.Get(row, "colour"));
        var scraped = new ScrapedProduct(
            header.Get(row, "parent_product_id") ?? id,
            url,
            title,
            header.Get(row, "brand_name"),
            header.Get(row, "merchant_image_url") ?? header.Get(row, "aw_image_url"),
            [offer]);
        return (new AwinProduct(Int(header.Get(row, "merchant_id")), header.Get(row, "merchant_name") ?? "", header.Get(row, "aw_deep_link"), scraped), offer);
    }

    private static decimal? Money(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;

    private static int Int(string? value) => int.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private sealed class Header(IReadOnlyList<string> names)
    {
        private readonly Dictionary<string, int> _index = names
            .Select((name, i) => (Name: name.Trim().TrimStart('﻿'), i))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);

        /// <summary>The trimmed value, or null when the column is missing or empty.</summary>
        public string? Get(IReadOnlyList<string> row, string column) =>
            _index.TryGetValue(column, out var i) && i < row.Count && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;
    }
}
