using System.Globalization;
using SmartStylist.Modules.Catalog.Affiliates;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Evidence;

public sealed record ManualImportResult(int Rows, int PricesAdded, IReadOnlyList<string> Skipped);

/// <summary>
/// Imports dated prices written down elsewhere, such as the stylist's Recommended and near-miss tables.
/// CSV with a header row: date, url, price are required; title, shop, currency, list_price, size, colour are optional.
/// Stored as outside evidence (PriceSource.ManualHistory).
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
        int rows = 0, added = 0, line = 0;

        await foreach (var row in CsvReader.ReadAsync(csv, cancellationToken))
        {
            line++;
            if (columns is null)
            {
                columns = row.Select((name, i) => (Name: name.Trim().ToLowerInvariant().Replace(' ', '_'), i))
                    .GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().i);
                foreach (var required in new[] { "date", "url", "price" })
                {
                    if (!columns.ContainsKey(required))
                    {
                        throw new FormatException($"The CSV needs a '{required}' column (has: {string.Join(", ", columns.Keys)}).");
                    }
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
            if (!TryDate(Get("date"), out var day))
            {
                skipped.Add($"line {line}: date '{Get("date")}' not understood (use 2026-10-14)");
                continue;
            }

            if (!Uri.TryCreate(Get("url"), UriKind.Absolute, out var url))
            {
                skipped.Add($"line {line}: no page address");
                continue;
            }

            var shop = await OutsideEvidenceWriter.FindShopAsync(_db, url, Get("shop"), cancellationToken);
            if (shop is null)
            {
                skipped.Add($"line {line}: {url.Host} is not one of the shops in appsettings.json");
                continue;
            }

            var currency = Get("currency")?.ToUpperInvariant() ?? shop.Currency;
            var price = PriceParser.Parse(Get("price"));
            if (price is null)
            {
                skipped.Add($"line {line}: price '{Get("price")}' not understood");
                continue;
            }

            var listPrice = PriceParser.Parse(Get("list_price") ?? Get("was"));
            var size = Get("size");
            var colour = Get("colour") ?? Get("color");
            var variant = string.Join('/', new[] { colour, size }.Where(v => v is not null));

            var product = await OutsideEvidenceWriter.FindOrAddProductAsync(_db, shop, url.ToString(), Get("title"), now, cancellationToken);
            added += await OutsideEvidenceWriter.AddAsync(_db, product, day,
                [new ScrapedOffer(variant, size, price.Value, listPrice > price ? listPrice : null, currency, null, colour)],
                shop.Currency, PriceSource.ManualHistory, now, cancellationToken);
        }

        return new ManualImportResult(rows, added, skipped);
    }

    private static bool TryDate(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out day);
}
