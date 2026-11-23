using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Affiliates;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Evidence;

public sealed record ManualImportResult(int Rows, int PricesAdded, IReadOnlyList<string> Skipped, int ShopsAdded = 0);

/// <summary>
/// Imports dated prices written down elsewhere, such as the stylist's Recommended and near-miss tables.
/// CSV with a header row. Required: date and a price (sale_price or price). Optional: url, store, shop, brand, model,
/// colour, style_code, title, currency, list_price, price_after_code, size.
/// A row is matched to a shop by its page address, then by store name ("Levi's US" = levis); any other store gets
/// its own disabled shop ("manual-end-clothing-uk") so its prices still count. Rows without an address are keyed by
/// brand + model + style code. Stored as outside evidence (PriceSource.ManualHistory, lower confidence).
/// price_after_code is kept in the CSV only: it depends on a code or cart condition, not the shelf price.
/// </summary>
public sealed class ManualPriceImport
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy/MM/dd", "MMM d yyyy", "MMM d, yyyy", "d MMM yyyy", "M/d/yyyy"];

    private readonly CatalogDbContext _db;
    private readonly TimeProvider _time;

    internal ManualPriceImport(CatalogDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<ManualImportResult> ImportAsync(TextReader csv, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        Dictionary<string, int>? columns = null;
        var skipped = new List<string>();
        int rows = 0, added = 0, line = 0, shopsAdded = 0;
        var shops = await _db.Shops.ToListAsync(cancellationToken);

        await foreach (var row in CsvReader.ReadAsync(csv, cancellationToken))
        {
            line++;
            if (columns is null)
            {
                columns = row.Select((name, i) => (Name: name.Trim().ToLowerInvariant().Replace(' ', '_'), i))
                    .GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().i);
                if (!columns.ContainsKey("date") || !(columns.ContainsKey("price") || columns.ContainsKey("sale_price")))
                {
                    throw new FormatException($"The CSV needs 'date' and 'price' (or 'sale_price') columns (has: {string.Join(", ", columns.Keys)}).");
                }

                continue;
            }

            string? Get(string name) =>
                columns.TryGetValue(name, out var i) && i < row.Count && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;

            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows++;
            if (!DateOnly.TryParseExact(Get("date"), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var day))
            {
                skipped.Add($"line {line}: date '{Get("date")}' not understood (use 2026-10-14)");
                continue;
            }

            Uri? url = Uri.TryCreate(Get("url"), UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? parsed : null;
            var store = Get("store");
            var currency = Get("currency")?.ToUpperInvariant();
            var shop = await FindShopAsync(shops, url, Get("shop"), store, currency);
            if (shop is null && store is not null)
            {
                shop = await AddStoreShopAsync(shops, store, url, currency ?? "USD", cancellationToken);
                shopsAdded++;
            }

            if (shop is null)
            {
                skipped.Add($"line {line}: no page address, shop or store to file it under");
                continue;
            }

            currency ??= shop.Currency;
            var price = PriceParser.Parse(Get("sale_price") ?? Get("price"));
            if (price is null)
            {
                skipped.Add($"line {line}: price '{Get("sale_price") ?? Get("price")}' not understood");
                continue;
            }

            var listPrice = PriceParser.Parse(Get("list_price") ?? Get("was"));
            var size = Get("size");
            var colour = Get("colour") ?? Get("color");
            var variant = string.Join('/', new[] { colour, size }.Where(v => v is not null));

            var title = Get("title") ?? string.Join(' ', new[] { Get("brand"), Get("model") }.Where(v => v is not null));
            var key = url?.ToString() ?? ProductKey(Get("brand"), Get("model"), Get("style_code"));
            if (key is null)
            {
                skipped.Add($"line {line}: no page address and no brand/model to name the product");
                continue;
            }

            var product = await OutsideEvidenceWriter.FindOrAddProductAsync(_db, shop, key, string.IsNullOrWhiteSpace(title) ? null : title, now, cancellationToken);
            if (product.Brand is null && Get("brand") is { } brand)
            {
                product.Brand = brand;
            }

            var count = await OutsideEvidenceWriter.AddAsync(_db, product, day,
                [new ScrapedOffer(variant, size, price.Value, listPrice > price ? listPrice : null, currency, null, colour)],
                shop.Currency, PriceSource.ManualHistory, now, cancellationToken);
            if (count == 0)
            {
                skipped.Add($"line {line}: {title} on {day:yyyy-MM-dd} already has a price for this colour/size, or the price looks unreal");
            }

            added += count;
        }

        return new ManualImportResult(rows, added, skipped, shopsAdded);
    }

    /// <summary>"manual:levi-s-japanese-selvedge-502-taper-a58810000" for rows without a page address.</summary>
    internal static string? ProductKey(string? brand, string? model, string? styleCode)
    {
        var text = string.Join(' ', new[] { brand, model, styleCode }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return text.Length == 0 ? null : $"manual:{Slug(text)}";
    }

    internal static string Slug(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private async Task<Shop?> FindShopAsync(List<Shop> shops, Uri? url, string? shopKey, string? store, string? currency)
    {
        if (!string.IsNullOrWhiteSpace(shopKey))
        {
            return shops.FirstOrDefault(s => s.Key == shopKey);
        }

        if (url is not null && await OutsideEvidenceWriter.FindShopAsync(_db, url, null, CancellationToken.None) is { } byUrl)
        {
            return byUrl;
        }

        if (store is null)
        {
            return null;
        }

        // "Levi's US" is the levis shop (USD); "Clarks UK" is not the US clarks shop.
        var name = store.EndsWith(" US", StringComparison.OrdinalIgnoreCase) ? store[..^3] : store;
        return shops.FirstOrDefault(s =>
            (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase)
             || string.Equals(s.Key, $"manual-{Slug(store)}", StringComparison.Ordinal))
            && (currency is null || s.Currency == currency));
    }

    private async Task<Shop> AddStoreShopAsync(List<Shop> shops, string store, Uri? url, string currency, CancellationToken cancellationToken)
    {
        var shop = new Shop
        {
            Key = OutsideEvidenceWriter.Truncate($"manual-{Slug(store)}", 64),
            Name = OutsideEvidenceWriter.Truncate(store, 200),
            BaseUrl = url is null ? $"https://{Slug(store)}.invalid" : $"{url.Scheme}://{url.Host}",
            Currency = currency.Length == 3 ? currency : "USD",
            Enabled = false,
        };
        _db.Shops.Add(shop);
        await _db.SaveChangesAsync(cancellationToken);
        shops.Add(shop);
        return shop;
    }
}
