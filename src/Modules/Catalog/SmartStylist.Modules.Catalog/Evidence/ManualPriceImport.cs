using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Affiliates;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Evidence;

public sealed record ManualImportResult(int Rows, int PricesAdded, IReadOnlyList<string> Skipped);

/// <summary>
/// Imports dated prices written down elsewhere, such as the stylist's Recommended and Near-misses tables in the
/// Wardrobe doc. Takes a CSV with a header row, or the Markdown tables pasted as they are (several tables, headings
/// and other text in between are fine). Columns are found by name: date, url (or a link in any cell), price
/// ("~~$98~~ $54" and "$54 (was $98)" give both prices), and optional title/item, shop, currency, was/list_price,
/// size, colour, brand, model, style_code. Without a title, brand + model + colour + style code make one. A store that
/// is not one of the shops ("REI", "END. UK") gets its own disabled shop ("manual-rei") so its prices still count;
/// "Levi's US" matches the shop named Levi's with the same currency. price_after_code stays in the file only: it
/// depends on a code or cart condition, not the shelf price. Stored as PriceSource.ManualHistory, the least certain
/// outside evidence.
/// </summary>
public sealed partial class ManualPriceImport
{
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy/MM/dd", "MMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy", "MMMM d yyyy", "d MMM yyyy", "M/d/yyyy", "dd.MM.yyyy"];

    private static readonly string[] DateFormatsWithoutYear = ["MMM d", "MMMM d", "d MMM", "M/d"];

    /// <summary>What people put in a table cell that has nothing to say.</summary>
    private static readonly HashSet<string> Blank = new(StringComparer.OrdinalIgnoreCase) { "-", "–", "—", "n/a", "na", "?" };

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["date"] = "date", ["seen"] = "date", ["seen_on"] = "date", ["as_of"] = "date", ["checked"] = "date", ["observed"] = "date",
        ["when"] = "date", ["day"] = "date", ["date_seen"] = "date",
        ["url"] = "url", ["link"] = "url", ["page"] = "url", ["product_url"] = "url",
        ["title"] = "title", ["item"] = "title", ["product"] = "title", ["name"] = "title", ["pick"] = "title",
        ["price"] = "price", ["now"] = "price", ["sale"] = "price", ["sale_price"] = "price", ["current"] = "price",
        ["current_price"] = "price", ["deal"] = "price", ["deal_price"] = "price", ["price_now"] = "price", ["final_price"] = "price",
        ["list_price"] = "list_price", ["was"] = "list_price", ["list"] = "list_price", ["original"] = "list_price",
        ["original_price"] = "list_price", ["compare_at"] = "list_price", ["msrp"] = "list_price", ["full_price"] = "list_price",
        ["regular"] = "list_price", ["regular_price"] = "list_price", ["retail"] = "list_price",
        ["shop"] = "shop", ["store"] = "shop", ["retailer"] = "shop", ["site"] = "shop",
        ["size"] = "size", ["colour"] = "colour", ["color"] = "colour", ["currency"] = "currency",
        ["brand"] = "brand", ["model"] = "model", ["style_code"] = "style_code", ["style"] = "style_code", ["sku"] = "style_code",
        ["price_after_code"] = "price_after_code",
    };

    private readonly CatalogDbContext _db;
    private readonly PriceSanityCheck _sanity;
    private readonly TimeProvider _time;

    public ManualPriceImport(CatalogDbContext db, PriceSanityCheck sanity, TimeProvider time)
    {
        _db = db;
        _sanity = sanity;
        _time = time;
    }

    /// <param name="defaultDate">The day for rows without a date (the day the tables were written).</param>
    public async Task<ManualImportResult> ImportAsync(TextReader input, CancellationToken cancellationToken, DateOnly? defaultDate = null)
    {
        var text = await input.ReadToEndAsync(cancellationToken);
        var rows = LooksLikeMarkdown(text) ? ReadMarkdown(text) : await ReadCsvAsync(text, cancellationToken);

        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var shops = await _db.Shops.ToListAsync(cancellationToken);
        var skipped = new List<string>();
        int count = 0, added = 0;

        foreach (var row in rows)
        {
            string? Get(string name) =>
                row.Cells.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) && !Blank.Contains(value.Trim()) ? value.Trim() : null;

            count++;
            var line = $"line {row.Line}";
            var rawDate = Get("date");
            DateOnly day;
            if (rawDate is null && defaultDate is { } fallback)
            {
                day = fallback;
            }
            else if (!TryDate(rawDate, today, out day))
            {
                skipped.Add($"{line}: date '{rawDate}' not understood (use 2026-10-14, or pass --date for rows without one)");
                continue;
            }

            var (title, linkInTitle) = SplitLink(Get("title"));
            title ??= ComposeTitle(Get("brand"), Get("model"), Get("colour"), Get("style_code"));
            var url = Link(Get("url")) ?? linkInTitle ?? row.Cells.Values.Select(Link).FirstOrDefault(u => u is not null);

            var currencyCell = Get("currency")?.ToUpperInvariant();
            var shopKey = ShopKey(shops, Get("shop"), currencyCell);
            Shop? shop = null;
            if (url is not null)
            {
                shop = await OutsideEvidenceWriter.FindShopAsync(_db, url, shopKey, cancellationToken);
            }
            else if (shopKey is { } key)
            {
                shop = shops.First(s => s.Key == key);
            }

            if (shop is null && Get("shop") is { } store)
            {
                shop = await AddStoreShopAsync(shops, StripMarkup(store), url, currencyCell ?? "USD", cancellationToken);
            }

            if (shop is null)
            {
                skipped.Add(url is null
                    ? $"{line}: no page address and no store"
                    : $"{line}: {url.Host} is not one of the shops in catalogue.json");
                continue;
            }

            if (url is null && title is null)
            {
                skipped.Add($"{line}: no page address and no title, so the product cannot be told apart");
                continue;
            }

            var (price, struck) = ParseMoney(Get("price"));
            if (price is null)
            {
                skipped.Add($"{line}: price '{Get("price")}' not understood");
                continue;
            }

            var listPrice = ParseMoney(Get("list_price")).Price ?? struck;
            var currency = Get("currency")?.ToUpperInvariant() ?? shop.Currency;
            var size = Get("size");
            var colour = Get("colour");
            var variant = string.Join('/', new[] { colour, size }.Where(v => v is not null));

            var product = url is not null
                ? await OutsideEvidenceWriter.FindOrAddProductAsync(_db, shop, url.ToString(), title, now, cancellationToken)
                : await FindOrAddByTitleAsync(shop, title!, now, cancellationToken);
            var (written, refused) = await OutsideEvidenceWriter.AddAsync(_db, _sanity, product, day,
                [new ScrapedOffer(variant, size, price.Value, listPrice > price ? listPrice : null, currency, null, colour)],
                shop.Currency, PriceSource.ManualHistory, now, cancellationToken);
            added += written;
            if (refused > 0)
            {
                skipped.Add($"{line}: {price:0.00} {currency} refused by the price sanity check");
            }
        }

        return new ManualImportResult(count, added, skipped);
    }

    internal sealed record Row(int Line, IReadOnlyDictionary<string, string> Cells);

    internal static bool LooksLikeMarkdown(string text) =>
        text.Split('\n').Count(l => l.TrimStart().StartsWith('|')) >= 2;

    /// <summary>Every row of every Markdown table, keyed by the column names of its own table.</summary>
    internal static IReadOnlyList<Row> ReadMarkdown(string text)
    {
        var rows = new List<Row>();
        string[]? header = null;
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|'))
            {
                header = null;
                continue;
            }

            var cells = SplitMarkdownRow(line);
            if (header is null)
            {
                header = cells.Select(Column).ToArray();
                continue;
            }

            if (cells.All(c => SeparatorCell().IsMatch(c)))
            {
                continue;
            }

            rows.Add(ToRow(i + 1, header, cells));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<Row>> ReadCsvAsync(string text, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        string[]? header = null;
        var line = 0;
        await foreach (var cells in CsvReader.ReadAsync(new StringReader(text), cancellationToken))
        {
            line++;
            if (header is null)
            {
                header = cells.Select(Column).ToArray();
                if (!header.Contains("price"))
                {
                    throw new FormatException($"The CSV needs a 'price' column (has: {string.Join(", ", cells)}).");
                }

                continue;
            }

            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows.Add(ToRow(line, header, cells.ToArray()));
        }

        return rows;
    }

    private static Row ToRow(int line, string[] header, IReadOnlyList<string> cells)
    {
        var values = new Dictionary<string, string>();
        for (var c = 0; c < header.Length && c < cells.Count; c++)
        {
            // The first column of a name wins: "Price" before a later "Price (GEL)".
            values.TryAdd(header[c], cells[c]);
        }

        return new Row(line, values);
    }

    private static string[] SplitMarkdownRow(string line)
    {
        var inner = line.Trim().Trim('|');
        // A pipe inside a link or escaped as \| is not a cell border.
        return EscapedPipe().Replace(inner, "\u0001").Split('|').Select(c => c.Replace('\u0001', '|').Trim()).ToArray();
    }

    private static string Column(string name)
    {
        var key = NonWord().Replace(StripMarkup(name).ToLowerInvariant(), "_").Trim('_');
        return Aliases.TryGetValue(key, out var alias) ? alias
            : Aliases.FirstOrDefault(a => key.StartsWith(a.Key + "_", StringComparison.Ordinal)).Value ?? key;
    }

    private static string StripMarkup(string value) => value.Replace("*", "").Replace("`", "").Trim();

    private static Uri? Link(string? cell)
    {
        if (cell is null)
        {
            return null;
        }

        var match = MarkdownLink().Match(cell);
        var raw = match.Success ? match.Groups["url"].Value : BareUrl().Match(cell) is { Success: true } bare ? bare.Value : null;
        return raw is not null && Uri.TryCreate(raw.TrimEnd('.', ',', ';'), UriKind.Absolute, out var url) ? url : null;
    }

    /// <summary>"[Levi's 541](https://...)" gives the title and the address.</summary>
    private static (string? Title, Uri? Url) SplitLink(string? cell)
    {
        if (cell is null)
        {
            return (null, null);
        }

        var match = MarkdownLink().Match(cell);
        var title = StripMarkup(match.Success ? cell.Replace(match.Value, match.Groups["text"].Value) : cell);
        return (title.Length == 0 ? null : title, Link(cell));
    }

    /// <summary>The price paid and the crossed-out price, from "~~$98~~ $54.99", "$54.99 (was $98)" or "54.99".</summary>
    internal static (decimal? Price, decimal? Struck) ParseMoney(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return (null, null);
        }

        decimal? struck = null;
        var strike = Strikethrough().Match(cell);
        if (strike.Success)
        {
            struck = PriceParser.Parse(strike.Groups["was"].Value);
            cell = cell.Remove(strike.Index, strike.Length);
        }

        var was = WasPrice().Match(cell);
        if (was.Success)
        {
            struck ??= PriceParser.Parse(was.Groups["was"].Value);
            cell = cell.Remove(was.Index, was.Length);
        }

        var amount = Amount().Match(cell);
        return (amount.Success ? PriceParser.Parse(amount.Value) : null, struck);
    }

    private static string? ShopKey(IReadOnlyList<Shop> shops, string? name, string? currency = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = StripMarkup(name);
        var exact = shops.FirstOrDefault(s => string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(s.Key, $"manual-{Slug(name)}", StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact.Key;
        }

        // "Levi's US", "Clarks UK": the shop's name plus a country, when the currency agrees.
        var space = name.LastIndexOf(' ');
        if (space <= 0 || name.Length - space - 1 is < 2 or > 3)
        {
            return null;
        }

        var bare = name[..space];
        return shops.FirstOrDefault(s =>
            (string.Equals(s.Name, bare, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Key, bare, StringComparison.OrdinalIgnoreCase))
            && (currency is null || s.Currency == currency))?.Key;
    }

    private static string? ComposeTitle(string? brand, string? model, string? colour, string? styleCode)
    {
        var parts = new[] { brand, model, colour, styleCode }.Where(p => p is not null).Select(p => StripMarkup(p!)).ToArray();
        return parts.Length == 0 || (brand is null && model is null) ? null : string.Join(' ', parts);
    }

    internal static string Slug(string value) => NonWord().Replace(value.ToLowerInvariant(), "-").Trim('-');

    /// <summary>A store the app does not collect from: its own disabled shop, so its prices still count as history.</summary>
    private async Task<Shop> AddStoreShopAsync(List<Shop> shops, string store, Uri? url, string currency, CancellationToken cancellationToken)
    {
        var shop = new Shop
        {
            Key = $"manual-{Slug(store)}".Truncate(64),
            Name = store.Truncate(200),
            BaseUrl = url is null ? $"https://{Slug(store)}.invalid" : $"{url.Scheme}://{url.Host}",
            Currency = currency.Length == 3 ? currency : "USD",
            Enabled = false,
        };
        _db.Shops.Add(shop);
        await _db.SaveChangesAsync(cancellationToken);
        shops.Add(shop);
        return shop;
    }

    /// <summary>A product without a page address: the shop's product with the same title, or a new one keyed by the title.</summary>
    private async Task<Product> FindOrAddByTitleAsync(Shop shop, string title, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lower = title.ToLowerInvariant();
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ShopId == shop.Id && p.Title.ToLower() == lower, cancellationToken);
        if (product is not null)
        {
            return product;
        }

        product = new Product
        {
            ShopId = shop.Id,
            ExternalId = $"manual:{lower}".Truncate(500),
            Url = shop.BaseUrl,
            Title = title.Truncate(500),
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        var existing = await _db.Products.FirstOrDefaultAsync(p => p.ShopId == shop.Id && p.ExternalId == product.ExternalId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        _db.Products.Add(product);
        await _db.SaveChangesAsync(cancellationToken);
        return product;
    }

    /// <summary>A date as people write it. Without a year it is the latest such day not after today.</summary>
    internal static bool TryDate(string? value, DateOnly today, out DateOnly day)
    {
        value = value is null ? null : StripMarkup(value);
        if (DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out day))
        {
            return true;
        }

        if (DateTime.TryParseExact(value, DateFormatsWithoutYear, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var noYear))
        {
            day = new DateOnly(today.Year, noYear.Month, noYear.Day);
            if (day > today)
            {
                day = day.AddYears(-1);
            }

            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex SeparatorCell();

    [GeneratedRegex(@"\\\|")]
    private static partial Regex EscapedPipe();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\((?<url>https?://[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"https?://[^\s)|>\]]+")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"~~(?<was>[^~]+)~~")]
    private static partial Regex Strikethrough();

    [GeneratedRegex(@"\(?\b(?:was|reg\.?|regular|orig\.?|list)\b:?\s*(?<was>[$€£₾]?\s*\d[\d.,]*)\)?", RegexOptions.IgnoreCase)]
    private static partial Regex WasPrice();

    [GeneratedRegex(@"\d[\d,]*(?:\.\d{1,2})?")]
    private static partial Regex Amount();
}
