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

    /// <summary>How long ago an archived copy may be, for the Wayback Machine seeding.</summary>
    public int ArchiveLookbackDays { get; set; } = 365;

    public BrowserOptions Browser { get; set; } = new();

    /// <summary>
    /// Other ways out to the internet, by name, e.g. "us" = a proxy or small VPS in the US. A shop uses one through
    /// ShopOptions.Egress. Geo-restricted shops block or redirect visitors from Georgia; from a US address they load.
    /// </summary>
    public Dictionary<string, EgressOptions> Egresses { get; set; } = [];

    /// <summary>
    /// The egress to retry through when a shop redirects to its country site (brooksbrothers.com to brooksbrothers.eu).
    /// Empty = report the redirect and do not retry.
    /// </summary>
    public string? GeoRetryEgress { get; set; }

    public PriceSanityOptions PriceSanity { get; set; } = new();

    public BrowserWatchOptions BrowserWatch { get; set; } = new();

    /// <summary>
    /// Long-running products sold for years (Levi's 541, Patagonia Better Sweater). Their archived pages give real
    /// price history, so the Wayback seeding starts with them.
    /// </summary>
    public List<CoreProductOptions> CoreProducts { get; set; } = [];

    public List<ShopOptions> Shops { get; set; } = [];
}

public sealed class EgressOptions
{
    /// <summary>
    /// http://host:port, http://user:password@host:port or socks5://host:port (e.g. "ssh -D 1080 my-us-vps" gives
    /// socks5://127.0.0.1:1080). Keep credentials out of appsettings.json: set Catalog__Egresses__us__ProxyUrl instead.
    /// </summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Least time between two requests through this egress, across all its shops.</summary>
    public int MinIntervalMs { get; set; } = 5000;
}

public sealed class PriceSanityOptions
{
    /// <summary>A price under this share of the SKU's usual (median) price or of its own "was" price is refused.</summary>
    public decimal MinShareOfUsual { get; set; } = 0.15m;

    /// <summary>Currency the floors are written in. Prices in other currencies are only checked against their own history.</summary>
    public string FloorCurrency { get; set; } = "USD";

    /// <summary>Floor for items no category below matches.</summary>
    public decimal DefaultFloor { get; set; } = 3m;

    /// <summary>The first category whose keyword is in the product title sets the floor.</summary>
    public List<PriceFloorOptions> Floors { get; set; } = [];
}

public sealed class PriceFloorOptions
{
    public required string Category { get; set; }

    /// <summary>Whole words looked for in the title, case-insensitive, e.g. "tee", "t-shirt".</summary>
    public List<string> Keywords { get; set; } = [];

    public decimal Min { get; set; }
}

public sealed class BrowserWatchOptions
{
    /// <summary>Cron (UTC) for the visits. A few times a day at most.</summary>
    public string Cron { get; set; } = "0 7,13,19 * * *";

    /// <summary>A watched page is not visited again sooner than this, however often the watch runs.</summary>
    public int MinHoursBetweenVisits { get; set; } = 4;

    /// <summary>Most pages per shop per run.</summary>
    public int MaxPagesPerShop { get; set; } = 15;
}

public sealed class CoreProductOptions
{
    public required string Shop { get; set; }

    /// <summary>The product page today; archived prices are saved on this product.</summary>
    public required string Url { get; set; }

    public string? Title { get; set; }

    /// <summary>Older addresses of the same product (the shop renamed the page), also looked up in the archive.</summary>
    public List<string> ArchiveUrls { get; set; } = [];
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
    /// Name of a Catalog:Egresses entry the shop's plain HTTP requests go through, e.g. "us" for shops that block or
    /// redirect visitors from Georgia. Empty = straight from this machine.
    /// </summary>
    public string? Egress { get; set; }

    /// <summary>Not a place to buy (e.g. it cancels forwarder orders): no collection, no sale emails, no deals.</summary>
    public bool Dropped { get; set; }

    /// <summary>
    /// Watched product pages at this shop are visited in the real Chrome window a few times a day (Catalog:BrowserWatch).
    /// For shops whose pages load in Chrome but not over plain HTTP; the shop is never crawled in the browser.
    /// </summary>
    public bool BrowserWatch { get; set; }

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
