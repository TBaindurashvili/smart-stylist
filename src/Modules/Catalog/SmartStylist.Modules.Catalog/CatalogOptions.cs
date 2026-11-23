using SmartStylist.Modules.Catalog.Domain;

namespace SmartStylist.Modules.Catalog;

public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    /// <summary>Cron (UTC) for the daily collection. Each shop gets its own recurring job.</summary>
    public string DailyCron { get; set; } = "0 5 * * *";

    /// <summary>Most products to price per shop per day. Keeps the data test polite and fast.</summary>
    public int MaxProductsPerShop { get; set; } = 300;

    /// <summary>Pause between page requests to the same shop.</summary>
    public int RequestDelayMs { get; set; } = 1500;

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36";

    /// <summary>Pause between requests to the Wayback Machine. archive.org asks callers to stay well under one request a second.</summary>
    public int ArchiveDelayMs { get; set; } = 4000;

    public BrowserOptions Browser { get; set; } = new();

    /// <summary>Ways out through other countries, by name (e.g. "us"), for shops that block or redirect visitors from Georgia.</summary>
    public Dictionary<string, Collection.EgressOptions> Egress { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Watched product pages opened in the visible Chrome a few times a day (Catalog:Shops:*:WatchUrls).</summary>
    public WatchOptions Watch { get; set; } = new();

    /// <summary>Long-running products whose Wayback Machine history is seeded first (`wayback core`).</summary>
    public List<string> CoreProducts { get; set; } = [];

    /// <summary>Rejects prices that cannot be real (misread placeholders) before they are stored.</summary>
    public Collection.PriceSanityOptions PriceSanity { get; set; } = new();

    public List<ShopOptions> Shops { get; set; } = [];
}

public sealed class ShopOptions
{
    public required string Key { get; set; }

    public required string Name { get; set; }

    public required string BaseUrl { get; set; }

    public string Currency { get; set; } = "USD";

    public CollectorStrategy Strategy { get; set; } = CollectorStrategy.Auto;

    public bool Enabled { get; set; } = true;

    /// <summary>Sitemap to start from. When empty, the Sitemap lines in robots.txt are used.</summary>
    public string? SitemapUrl { get; set; }

    /// <summary>Regex a product URL must match, e.g. to keep one locale or the men's section.</summary>
    public string? ProductUrlPattern { get; set; }

    /// <summary>
    /// Domains the shop's sale emails come from, e.g. "e.levi.com". When empty, the BaseUrl domain is used,
    /// which already matches subdomains such as news.reebok.com for reebok.com.
    /// </summary>
    public List<string> EmailDomains { get; set; } = [];

    /// <summary>Awin advertiser id, so the shop's Awin product feed saves prices here instead of in a separate "awin-{id}" shop.</summary>
    public int? AwinAdvertiserId { get; set; }

    /// <summary>Ships to a forwarder address without cancelling (learned from real orders). Null = not known yet.</summary>
    public bool? AcceptsForwarder { get; set; }

    /// <summary>Accepts a foreign card. Null = not known yet.</summary>
    public bool? AcceptsForeignCard { get; set; }

    /// <summary>Checkout tips learned from real orders.</summary>
    public string? CheckoutNote { get; set; }

    /// <summary>
    /// Name of a Catalog:Egress entry (e.g. "us"). The plain-HTTP tier tries the home connection first and switches to
    /// this egress when the shop redirects to another country's site or blocks the page.
    /// </summary>
    public string? Egress { get; set; }

    /// <summary>
    /// Open this shop's watched product pages in the browser (Catalog:Browser, e.g. a visible Chrome) a few times a day,
    /// for shops that only answer a real browser. Watched = WatchUrls plus products saved at this shop with the extension.
    /// </summary>
    public bool WatchInBrowser { get; set; }

    /// <summary>Product pages to watch in the browser, in addition to products saved with the extension.</summary>
    public List<string> WatchUrls { get; set; } = [];

    /// <summary>Free text for people reading the config, e.g. why a shop is disabled. Not used by code.</summary>
    public string? Note { get; set; }
}

public sealed class BrowserOptions
{
    public bool Headless { get; set; } = true;

    /// <summary>Optional path to an installed Chromium. Leave empty to use the Playwright-managed one.</summary>
    public string? ExecutablePath { get; set; }

    public int NavigationTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// "chrome" drives the Google Chrome installed on the machine instead of the bundled Chromium.
    /// Shops' bot checks trust a real Chrome more than Playwright's build.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// A profile folder the browser keeps between runs (cookies, history), like a person's browser.
    /// Use a separate folder from your everyday Chrome profile: Chrome locks a profile while it is open.
    /// </summary>
    public string? UserDataDir { get; set; }

    /// <summary>Extra pause after each page, on top of Catalog:RequestDelayMs, so a visible browser moves at a person's pace.</summary>
    public int PageSettleMs { get; set; } = 1500;
}

public sealed class WatchOptions
{
    /// <summary>Cron (UTC) for opening watched pages; a few times a day at most. 05:00, 11:00, 17:00 UTC = 9, 15, 21 in Tbilisi.</summary>
    public string Cron { get; set; } = "0 5,11,17 * * *";

    /// <summary>Most watched pages per shop per run.</summary>
    public int MaxPagesPerShop { get; set; } = 15;

    /// <summary>Pause between watched pages, at a person's pace.</summary>
    public int DelayMs { get; set; } = 20000;
}
