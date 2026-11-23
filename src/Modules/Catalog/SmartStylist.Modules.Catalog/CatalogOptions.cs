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
