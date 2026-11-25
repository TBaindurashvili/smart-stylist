using System.Text;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Domain;

namespace SmartStylist.Modules.Catalog.Catalogue;

/// <summary>
/// The ways a price can reach the app, best first. A brand is read by the best one that works at any of its shops.
/// </summary>
public enum PriceRoute
{
    /// <summary>A full product feed every day: Awin, Shopify products.json, the Uniqlo API.</summary>
    Feed = 1,

    /// <summary>Product data on the shop's own pages (JSON-LD), by the crawl or the real-Chrome watch.</summary>
    PageData = 2,

    /// <summary>The shop's sale emails, read by Claude and checked by code. Prices arrive only when a sale is on.</summary>
    SaleEmail = 3,

    /// <summary>The browser extension, while a person looks at the page. The way in for blocked shops.</summary>
    Extension = 4,
}

public enum RouteState
{
    Works,
    Pending,
    NotTried,
    NotSetUp,
    Blocked,
}

/// <param name="Shop">Shop key, or the Awin programme name for a feed.</param>
public sealed record RouteStatus(PriceRoute Route, RouteState State, string Shop, string Detail);

/// <summary>The quality catalogue: which brands matter, who sells them and how their prices can be read.</summary>
public sealed class QualityCatalogue(IOptions<CatalogOptions> options)
{
    public IReadOnlyList<BrandOptions> Brands => options.Value.Brands;

    /// <summary>
    /// The catalogue brand a product belongs to, from its brand field or else a brand name as a whole word in its
    /// title. Null = not a catalogue brand, so not something to recommend.
    /// </summary>
    public BrandOptions? FindBrand(string? brand, string? title)
    {
        var names = options.Value.Brands
            .SelectMany(b => b.Aliases.Prepend(b.Name).Select(n => (Brand: b, Name: Normalize(n))))
            .Where(n => n.Name.Length > 0)
            .OrderByDescending(n => n.Name.Length)
            .ToList();

        if (!string.IsNullOrWhiteSpace(brand))
        {
            var normalized = Normalize(brand);
            var exact = names.FirstOrDefault(n => n.Name == normalized).Brand;
            if (exact is not null)
            {
                return exact;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var words = $" {Normalize(title)} ";
        return names.FirstOrDefault(n => words.Contains($" {n.Name} ", StringComparison.Ordinal)).Brand;
    }

    /// <summary>Every way the brand's prices can be read today, one line per shop and route, best route first.</summary>
    /// <param name="knownStrategy">The collector that last worked for a shop (from the database), if any.</param>
    /// <param name="egressReady">Whether a named egress has a proxy configured.</param>
    public static IReadOnlyList<RouteStatus> Routes(
        BrandOptions brand,
        IReadOnlyList<ShopOptions> shops,
        Func<string, CollectorStrategy?> knownStrategy,
        Func<string, bool> egressReady)
    {
        var routes = new List<RouteStatus>();
        foreach (var programme in brand.Awin)
        {
            var state = programme.Status switch
            {
                ProgrammeStatus.Joined => RouteState.Works,
                ProgrammeStatus.Declined => RouteState.Blocked,
                _ => RouteState.Pending,
            };
            routes.Add(new RouteStatus(PriceRoute.Feed, state, programme.Programme, $"Awin programme {programme.Status.ToString().ToLowerInvariant()}"));
        }

        foreach (var key in brand.Shops)
        {
            var shop = shops.FirstOrDefault(s => s.Key == key);
            if (shop is null || shop.Dropped)
            {
                continue;
            }

            routes.AddRange(ShopRoutes(shop, knownStrategy(key), egressReady));
        }

        return routes.OrderBy(r => r.Route).ThenBy(r => r.State).ToList();
    }

    /// <summary>The best route that works, or null when the brand has no working price source yet.</summary>
    public static RouteStatus? Best(IEnumerable<RouteStatus> routes) =>
        routes.Where(r => r.State == RouteState.Works).OrderBy(r => r.Route).FirstOrDefault();

    private static IEnumerable<RouteStatus> ShopRoutes(ShopOptions shop, CollectorStrategy? known, Func<string, bool> egressReady)
    {
        var strategy = shop.Strategy == CollectorStrategy.Auto ? known : shop.Strategy;
        var waitsForEgress = !string.IsNullOrWhiteSpace(shop.Egress) && !egressReady(shop.Egress);
        var crawlState = waitsForEgress ? RouteState.Pending : RouteState.Works;
        var egressNote = waitsForEgress ? $", waits for the {shop.Egress} egress" : "";

        if (shop.Enabled && strategy is CollectorStrategy.Shopify or CollectorStrategy.UniqloApi)
        {
            yield return new RouteStatus(PriceRoute.Feed, crawlState, shop.Key,
                (strategy == CollectorStrategy.Shopify ? "Shopify products.json" : "Uniqlo API") + egressNote);
        }
        else if (shop.BrowserWatch)
        {
            yield return new RouteStatus(PriceRoute.PageData, RouteState.Works, shop.Key, "watched pages in Chrome");
        }
        else if (shop.Enabled)
        {
            yield return strategy is null
                ? new RouteStatus(PriceRoute.PageData, waitsForEgress ? RouteState.Pending : RouteState.NotTried, shop.Key, "crawl has not run yet" + egressNote)
                : new RouteStatus(PriceRoute.PageData, crawlState, shop.Key, "page JSON-LD crawl" + egressNote);
        }
        else
        {
            yield return shop.BlockedBy is null
                ? new RouteStatus(PriceRoute.PageData, RouteState.NotTried, shop.Key, "not tried: run detect and probe, then turn it on")
                : new RouteStatus(PriceRoute.PageData, RouteState.Blocked, shop.Key, $"blocked: {shop.BlockedBy}");
        }

        yield return string.IsNullOrWhiteSpace(shop.NewsletterInbox)
            ? new RouteStatus(PriceRoute.SaleEmail, RouteState.NotSetUp, shop.Key, "newsletter not signed up")
            : new RouteStatus(PriceRoute.SaleEmail, RouteState.Works, shop.Key, $"newsletter in the {shop.NewsletterInbox} inbox");

        yield return shop.ExtensionWorks switch
        {
            true => new RouteStatus(PriceRoute.Extension, RouteState.Works, shop.Key, "extension saves prices"),
            false => new RouteStatus(PriceRoute.Extension, RouteState.Blocked, shop.Key, "extension finds no price"),
            null => new RouteStatus(PriceRoute.Extension, RouteState.NotTried, shop.Key, "extension not tried"),
        };
    }

    /// <summary>Mistakes in the catalogue that would give wrong data. The app refuses to start on any.</summary>
    public static IReadOnlyList<string> Problems(CatalogOptions options)
    {
        var problems = new List<string>();
        problems.AddRange(Duplicates(options.Shops.Select(s => s.Key)).Select(k => $"Shop key '{k}' is used twice."));
        problems.AddRange(Duplicates(options.Brands.Select(b => b.Key)).Select(k => $"Brand key '{k}' is used twice."));

        // Pages are matched to a shop by host, so two shops on one host would price one's pages in the other's currency.
        problems.AddRange(Duplicates(options.Shops.Select(s => SiteHost(s.BaseUrl)))
            .Select(h => $"Two shops share the site {h}; the extension could not tell them apart."));

        var shops = options.Shops.ToDictionary(s => s.Key, StringComparer.Ordinal);
        foreach (var shop in options.Shops.Where(s => s.NewsletterInbox is not (null or "personal" or "shared")))
        {
            problems.Add($"Shop '{shop.Key}': NewsletterInbox must be \"personal\" or \"shared\", not \"{shop.NewsletterInbox}\".");
        }

        foreach (var brand in options.Brands)
        {
            if (brand.For.Count == 0)
            {
                problems.Add($"Brand '{brand.Key}' says nobody it is for (For: Men, Women).");
            }

            if (brand.Shops.Count == 0 && brand.Awin.Count == 0)
            {
                problems.Add($"Brand '{brand.Key}' has no shop and no Awin programme, so no way to read a price.");
            }

            foreach (var key in brand.Shops)
            {
                if (!shops.TryGetValue(key, out var shop))
                {
                    problems.Add($"Brand '{brand.Key}' lists shop '{key}', which is not in Catalog:Shops.");
                }
                else if (shop.Dropped)
                {
                    problems.Add($"Brand '{brand.Key}' lists shop '{key}', which is dropped.");
                }
            }
        }

        return problems;
    }

    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (c is not ('\'' or '’' or '.'))
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string SiteHost(string baseUrl)
    {
        var host = new Uri(baseUrl).Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
}

/// <summary>Stops the app at start when the catalogue has a mistake, and names each one.</summary>
internal sealed class CatalogueValidation : IValidateOptions<CatalogOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogOptions options)
    {
        var problems = QualityCatalogue.Problems(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
