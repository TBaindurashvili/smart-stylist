namespace SmartStylist.Modules.Catalog.Affiliates;

public sealed class AwinOptions
{
    public const string SectionName = "Awin";

    /// <summary>
    /// Product-feed API key from Awin (Toolbox → Create-a-Feed). Empty turns feed import off.
    /// Keep it in user-secrets or the Awin__ApiKey environment variable, not in appsettings.json.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Feed ids to import. Empty means every feed of a programme you have joined (from the feed list).</summary>
    public List<int> FeedIds { get; set; } = [];

    /// <summary>Cron (UTC) for the import. Awin refreshes most feeds once a day.</summary>
    public string Cron { get; set; } = "0 6 * * *";

    /// <summary>Most products saved per feed, so a 100k-product store doesn't fill the database.</summary>
    public int MaxProductsPerFeed { get; set; } = 5000;

    public string BaseUrl { get; set; } = "https://productdata.awin.com";
}
