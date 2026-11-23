using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Affiliates;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Evidence;
using SmartStylist.Modules.Catalog.Newsletters;
using SmartStylist.Modules.Catalog.Persistence;
using SmartStylist.Modules.Catalog.Reporting;
using SmartStylist.Modules.Wardrobe.Persistence;

namespace SmartStylist.Worker;

internal sealed record CliCommand(string Name, IReadOnlyList<string> ShopKeys, int? Limit, bool UseBrowser, string? Folder)
{
    private static readonly string[] Commands = ["collect", "report", "migrate", "install-browser", "probe", "newsletters", "awin", "awin-feeds", "detect", "wayback", "import-prices", "watch"];

    public static CliCommand? Parse(string[] args)
    {
        if (args.Length == 0 || !Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var shops = new List<string>();
        int? limit = null;
        var useBrowser = false;
        string? folder = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--limit" && i + 1 < args.Length)
            {
                limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] is "--folder" && i + 1 < args.Length)
            {
                folder = args[++i];
            }
            else if (args[i] is "--browser")
            {
                useBrowser = true;
            }
            else if (!args[i].StartsWith('-'))
            {
                shops.Add(args[i]);
            }
        }

        return new CliCommand(args[0].ToLowerInvariant(), shops, limit, useBrowser, folder);
    }

    public async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (Name == "install-browser")
        {
            // Downloads the Chromium build that matches the Microsoft.Playwright package. No PowerShell needed.
            return Microsoft.Playwright.Program.Main(["install", "chromium"]);
        }

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var runLog = provider.GetRequiredService<RunLogFile>();

        if (Name == "probe")
        {
            return await ProbeAsync(provider.GetRequiredService<ProductPageProbe>(), cancellationToken);
        }

        if (Name is "migrate" or "collect" or "newsletters" or "awin" or "detect" or "wayback" or "import-prices" or "watch")
        {
            await provider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync(cancellationToken);
            await provider.GetRequiredService<WardrobeDbContext>().Database.MigrateAsync(cancellationToken);
            await provider.GetRequiredService<ShopCatalogSync>().SyncAsync(cancellationToken);
        }

        if (Name == "collect")
        {
            var db = provider.GetRequiredService<CatalogDbContext>();
            var keys = ShopKeys.Count > 0
                ? ShopKeys
                : await db.Shops.Where(s => s.Enabled).OrderBy(s => s.Key).Select(s => s.Key).ToListAsync(cancellationToken);

            foreach (var key in keys)
            {
                // A fresh scope per shop keeps each DbContext small and isolated.
                await using var shopScope = services.CreateAsyncScope();
                var run = await shopScope.ServiceProvider.GetRequiredService<PriceCollectionService>()
                    .CollectAsync(key, Limit, cancellationToken);
                var line = $"{key,-16} {run.Status,-10} {run.Strategy?.ToString() ?? "-",-14} {run.ProductsSeen,5} products {run.SnapshotsWritten,6} prices";
                Console.WriteLine(line);
                runLog.WriteLine(line);
            }
        }

        if (Name == "newsletters")
        {
            await ImportNewslettersAsync(provider, runLog, cancellationToken);
        }

        if (Name == "detect")
        {
            await DetectAsync(provider, runLog, cancellationToken);
        }

        if (Name == "watch")
        {
            foreach (var r in await provider.GetRequiredService<WatchedPagesService>().RunAsync(ShopKeys, cancellationToken))
            {
                var line = $"{r.ShopKey,-16} {r.Pages,3} watched pages {r.Saved,3} saved" + (r.StoppedBecause is null ? "" : $"  stopped: {r.StoppedBecause}");
                Console.WriteLine(line);
                runLog.WriteLine(line);
            }
        }

        if (Name == "wayback")
        {
            await WaybackAsync(provider.GetRequiredService<WaybackSeedService>(), runLog, cancellationToken);
        }

        if (Name == "import-prices")
        {
            await ImportPricesAsync(provider.GetRequiredService<ManualPriceImport>(), runLog, cancellationToken);
        }

        if (Name is "awin" or "awin-feeds")
        {
            await AwinAsync(provider.GetRequiredService<AwinFeedImportService>(), runLog, cancellationToken);
        }

        if (Name is "collect" or "report")
        {
            var report = await provider.GetRequiredService<DataQualityReport>().BuildAsync(cancellationToken);
            var text = new StringWriter();
            ReportPrinter.Print(report, text);
            Console.Write(text);
            runLog.WriteLine($"Report:{Environment.NewLine}{text}");
        }

        Console.WriteLine($"Run log: {runLog.FilePath}");
        return 0;
    }

    private async Task ImportNewslettersAsync(IServiceProvider provider, RunLogFile runLog, CancellationToken cancellationToken)
    {
        IMailSource source = Folder is null
            ? new ImapMailSource(provider.GetRequiredService<IOptions<NewsletterOptions>>().Value.Imap)
            : new EmlFolderMailSource(Folder);
        NewsletterImportResult result;
        try
        {
            result = await provider.GetRequiredService<NewsletterImportService>().ImportAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
        {
            // Setup problems (no mailbox access, wrong folder path): one clear line instead of a stack trace.
            Console.WriteLine(ex.Message);
            runLog.WriteLine(ex.Message);
            return;
        }


        foreach (var email in result.Processed)
        {
            var subject = email.Subject.Length > 50 ? email.Subject[..50] + "…" : email.Subject;
            var line = $"{email.Shop?.Key ?? email.From,-24} {email.Status,-14} {email.PricesWritten,4} prices {email.ProductsRejected,3} rejected  {subject}" +
                (email.Error is null ? "" : $"{Environment.NewLine}{"",24} {email.Error}");
            Console.WriteLine(line);
            runLog.WriteLine(line);
        }

        Console.WriteLine($"{result.Processed.Count} emails read, {result.AlreadyRead} skipped as already read.");
    }

    /// <summary>Finds each shop's platform (Shopify, Salesforce, Magento, Next.js...) and saves it.</summary>
    private async Task DetectAsync(IServiceProvider provider, RunLogFile runLog, CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<CatalogDbContext>();
        var detector = provider.GetRequiredService<PlatformDetector>();
        var shops = await db.Shops.Where(s => ShopKeys.Count == 0 || ShopKeys.Contains(s.Key)).OrderBy(s => s.Key).ToListAsync(cancellationToken);
        foreach (var shop in shops)
        {
            PlatformDetection detection;
            try
            {
                detection = await detector.DetectAsync(new Uri(shop.BaseUrl), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                detection = new PlatformDetection(Modules.Catalog.Domain.ShopPlatform.Unknown, $"request failed: {ex.Message}");
            }

            shop.Platform = detection.Platform;
            shop.PlatformEvidence = detection.Evidence.Length > 200 ? detection.Evidence[..200] : detection.Evidence;
            shop.PlatformCheckedAt = DateTimeOffset.UtcNow;
            var line = $"{shop.Key,-16} {detection.Platform,-24} {detection.Evidence}";
            Console.WriteLine(line);
            runLog.WriteLine(line);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Seeds price history from archive.org copies. Arguments are shop keys (their products with under two weeks of
    /// prices) or product page addresses. --limit caps the products (default 20); 12 copies are read per product.
    /// </summary>
    private async Task WaybackAsync(WaybackSeedService wayback, RunLogFile runLog, CancellationToken cancellationToken)
    {
        const int copies = 12;
        var core = ShopKeys.Contains("core", StringComparer.OrdinalIgnoreCase);
        var urls = ShopKeys.Where(k => k.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        var shops = ShopKeys.Except(urls).Where(k => !string.Equals(k, "core", StringComparison.OrdinalIgnoreCase)).ToList();
        var results = new List<WaybackSeedResult>();
        if (core)
        {
            results.Add(await wayback.SeedCoreAsync(copies, cancellationToken));
        }

        foreach (var url in urls)
        {
            results.Add(await wayback.SeedUrlAsync(new Uri(url), null, copies, cancellationToken));
        }

        if (shops.Count > 0 || (urls.Count == 0 && !core))
        {
            results.Add(await wayback.SeedShopsAsync(shops, Limit ?? 20, copies, cancellationToken));
        }

        foreach (var note in results.SelectMany(r => r.Notes))
        {
            Console.WriteLine(note);
            runLog.WriteLine(note);
        }

        var summary = $"Wayback: {results.Sum(r => r.Products)} products, {results.Sum(r => r.PagesRead)} archived pages read, " +
                      $"{results.Sum(r => r.PagesWithPrice)} with a price, {results.Sum(r => r.PricesAdded)} prices added as outside evidence.";
        Console.WriteLine(summary);
        runLog.WriteLine(summary);
    }

    /// <summary>Imports a CSV of dated prices (date, url, price, optional title, shop, currency, list_price, size, colour).</summary>
    private async Task ImportPricesAsync(ManualPriceImport import, RunLogFile runLog, CancellationToken cancellationToken)
    {
        if (ShopKeys.Count == 0 || !File.Exists(ShopKeys[0]))
        {
            Console.WriteLine("Usage: import-prices <file.csv>   (columns: date, url, price; optional title, shop, currency, list_price, size, colour)");
            return;
        }

        using var reader = File.OpenText(ShopKeys[0]);
        ManualImportResult result;
        try
        {
            result = await import.ImportAsync(reader, cancellationToken);
        }
        catch (FormatException ex)
        {
            Console.WriteLine(ex.Message);
            return;
        }

        foreach (var line in result.Skipped)
        {
            Console.WriteLine($"skipped {line}");
            runLog.WriteLine($"skipped {line}");
        }

        var summary = $"{result.Rows} rows read, {result.PricesAdded} prices added as outside evidence, {result.Skipped.Count} skipped.";
        Console.WriteLine(summary);
        runLog.WriteLine(summary);
    }

    private async Task AwinAsync(AwinFeedImportService awin, RunLogFile runLog, CancellationToken cancellationToken)
    {
        try
        {
            if (Name == "awin-feeds")
            {
                // What Awin offers: joined programmes are imported by "awin" when Awin:FeedIds is empty.
                foreach (var feed in (await awin.ListFeedsAsync(cancellationToken)).OrderByDescending(f => f.Joined).ThenBy(f => f.AdvertiserName))
                {
                    Console.WriteLine($"{(feed.Joined ? "joined" : "-"),-7} feed {feed.FeedId,-7} advertiser {feed.AdvertiserId,-7} {feed.Products,7} products  {feed.Region,-3} {feed.AdvertiserName} · {feed.FeedName}");
                }

                return;
            }

            foreach (var result in await awin.ImportAsync(cancellationToken))
            {
                var line = $"{result.ShopKey ?? "-",-16} feed {result.Feed.FeedId,-7} {result.Products,6} products {result.Prices,7} prices  {result.Feed.AdvertiserName}" +
                    (result.Error is null ? "" : $"  error: {result.Error}");
                Console.WriteLine(line);
                runLog.WriteLine(line);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            // No API key, or Awin refused it: one clear line instead of a stack trace.
            Console.WriteLine(ex.Message);
            runLog.WriteLine(ex.Message);
        }
    }

    // Arguments are product URLs here, not shop keys.
    private async Task<int> ProbeAsync(ProductPageProbe probe, CancellationToken cancellationToken)
    {
        if (ShopKeys.Count == 0)
        {
            Console.WriteLine("Usage: probe <product url> [--browser]");
            return 1;
        }

        var url = new Uri(ShopKeys[0]);
        var result = await probe.ProbeAsync(url, UseBrowser, "USD", cancellationToken);

        if (result.Html is not null)
        {
            var file = Path.GetFullPath($"probe-{url.Host}.html");
            await File.WriteAllTextAsync(file, result.Html, cancellationToken);
            Console.WriteLine($"Saved the page to {file}");
        }

        if (result.Product is not { } product)
        {
            Console.WriteLine($"No price found: {result.Error}");
            return 1;
        }

        Console.WriteLine($"{product.Title} | brand: {product.Brand ?? "-"} | id: {product.ExternalId}");
        foreach (var offer in product.Offers.Take(20))
        {
            Console.WriteLine($"  {offer.SizeLabel ?? "(no size)",-14} {offer.Price,9:0.00} {offer.Currency}" +
                $"{(offer.ListPrice is { } was ? $"  was {was:0.00}" : "")}  in stock: {offer.InStock?.ToString() ?? "?"}");
        }

        return 0;
    }
}
