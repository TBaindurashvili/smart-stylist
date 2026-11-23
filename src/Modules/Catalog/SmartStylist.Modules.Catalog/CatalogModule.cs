using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog.Affiliates;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Evidence;
using SmartStylist.Modules.Catalog.Newsletters;
using SmartStylist.Modules.Catalog.Persistence;
using SmartStylist.Modules.Catalog.Reporting;

namespace SmartStylist.Modules.Catalog;

public static class CatalogModule
{
    public const string ConnectionStringName = "Postgres";

    /// <summary>
    /// The Postgres connection string. <c>POSTGRES_PORT</c> (the same variable docker-compose.yml reads)
    /// overrides the port, so a machine whose 5432 is taken needs no JSON edit.
    /// </summary>
    public static string GetConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Missing connection string '{ConnectionStringName}'.");
        if (!int.TryParse(configuration["POSTGRES_PORT"], out var port))
        {
            return connectionString;
        }

        return new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Port = port }.ConnectionString;
    }

    /// <summary>Registers the Catalog module: products, daily prices, collectors and the data-quality report.</summary>
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CatalogOptions>()
            .Bind(configuration.GetSection(CatalogOptions.SectionName))
            .Validate(o => o.Shops.Select(s => s.Key).Distinct().Count() == o.Shops.Count, "Shop keys must be unique.")
            .ValidateOnStart();
        services.AddOptions<NewsletterOptions>().Bind(configuration.GetSection(NewsletterOptions.SectionName));
        services.AddOptions<AwinOptions>().Bind(configuration.GetSection(AwinOptions.SectionName));

        var connectionString = GetConnectionString(configuration);

        services.AddDbContext<CatalogDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CatalogDbContext.Schema))
            .UseSnakeCaseNamingConvention());

        services.AddCatalogCollection();
        return services;
    }

    /// <summary>Collector services without the database registration, so tests can plug in their own provider.</summary>
    internal static IServiceCollection AddCatalogCollection(this IServiceCollection services)
    {
        services.AddHttpClient<HttpPageFetcher>(HttpPageFetcher.ClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<CatalogOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });

        services.AddSingleton<BrowserPageFetcher>();
        services.AddSingleton<SitemapReader>();
        services.AddScoped<IShopCollector, ShopifyCollector>();
        services.AddScoped<IShopCollector, SitemapJsonLdCollector>();
        services.AddScoped<IShopCollector, BrowserJsonLdCollector>();
        services.AddScoped<IShopCollector, UniqloApiCollector>();
        services.AddSingleton<EgressFetchers>();
        services.AddScoped(sp => new PriceCollectionService(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetServices<IShopCollector>(),
            sp.GetRequiredService<IOptions<CatalogOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PriceCollectionService>>(),
            sp.GetRequiredService<HttpPageFetcher>(),
            sp.GetRequiredService<EgressFetchers>()));
        services.AddScoped<ShopCatalogSync>();
        services.AddScoped(sp => new ProductPageProbe(sp.GetRequiredService<HttpPageFetcher>(), sp.GetRequiredService<BrowserPageFetcher>()));
        services.AddScoped(sp => new PageCaptureService(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<CatalogOptions>>().Value.PriceSanity));
        services.AddScoped<CollectPricesJob>();
        services.AddScoped(sp => new WatchedPagesService(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<BrowserPageFetcher>(),
            sp.GetRequiredService<PageCaptureService>(),
            sp.GetRequiredService<IOptions<CatalogOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WatchedPagesService>>()));
        services.AddScoped<WatchPagesJob>();
        services.AddScoped<PlatformDetector>();
        services.AddScoped<DataQualityReport>();
        services.AddScoped<DealsReport>();
        services.AddScoped<PriceHistoryService>();
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient<ILinkResolver, LinkResolver>(LinkResolver.ClientName, (sp, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(sp.GetRequiredService<IOptions<CatalogOptions>>().Value.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<INewsletterExtractor, ClaudeNewsletterExtractor>();
        services.AddScoped(sp => new NewsletterImportService(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<INewsletterExtractor>(),
            sp.GetRequiredService<ILinkResolver>(),
            sp.GetRequiredService<IOptions<CatalogOptions>>(),
            sp.GetRequiredService<IOptions<NewsletterOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NewsletterImportService>>()));
        services.AddScoped<ImportNewslettersJob>();

        services.AddHttpClient<AwinFeedImportService>(AwinFeedImportService.ClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddScoped<ImportAwinFeedsJob>();

        services.AddHttpClient(WaybackSeedService.ClientName, (sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SmartStylist/1.0 (personal price history)");
        });
        services.AddScoped(sp => new WaybackSeedService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(WaybackSeedService.ClientName),
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<CatalogOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WaybackSeedService>>()));
        services.AddScoped(sp => new ManualPriceImport(sp.GetRequiredService<CatalogDbContext>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
