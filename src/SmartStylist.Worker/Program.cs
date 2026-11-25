using Hangfire;
using Hangfire.PostgreSql;
using SmartStylist.Modules.Catalog;
using SmartStylist.Modules.Picks;
using SmartStylist.Modules.Wardrobe;
using SmartStylist.Worker;

// Usage:
//   dotnet run                         -> Hangfire server; collects every shop daily on Catalog:DailyCron
//   dotnet run -- collect [shop ...] [--limit N]   -> collect now (all enabled shops if none given), then print the report
//   dotnet run -- report               -> print the data-quality report
//   dotnet run -- migrate              -> apply database migrations and sync shops from config
//   dotnet run -- install-browser      -> download headless Chromium for shops that need a real browser
//   dotnet run -- probe <url> [--browser]  -> show what the parser reads from one product page and save the HTML
//   dotnet run -- newsletters [--folder <dir>]  -> price new sale emails from the IMAP mailbox, or from saved .eml files
//   dotnet run -- detect [shop ...]   -> find and save each shop's platform (Shopify, Salesforce, Magento, Next.js...)
//   dotnet run -- awin-feeds           -> list the Awin product feeds and which programmes you joined
//   dotnet run -- awin                 -> import today's prices from the Awin product feeds
//   dotnet run -- wayback [core|shop ...|url ...] [--limit N]  -> seed price history from archive.org copies
//   dotnet run -- import-prices <file.csv|file.md> [--date YYYY-MM-DD]  -> import written-down prices (Wardrobe doc tables)
//   dotnet run -- catalogue            -> the quality catalogue: each brand, its best working price source, last 30 days
//   dotnet run -- picks [person]       -> today's picks per person (or one person, by id or name), without sending
//   dotnet run -- picks send           -> link new Telegram chats and send each linked person their new picks
//   dotnet run -- picks link <person>  -> the Telegram link that person opens to get their picks
//   dotnet run -- picks outcomes       -> was "buy now" right? (the mission's 8-of-10 check)
//   dotnet run -- watch add <url> [--size M] | remove <url> | list | run  -> the real-Chrome watch (J.Crew, lululemon)
var command = CliCommand.Parse(args);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = command is null ? args : [],
});

// Disposed after the host (declared before it), so shutdown messages still reach the file.
using var runLog = new RunLogFile(Path.Combine(builder.Environment.ContentRootPath, "logs"));
builder.Logging.AddProvider(runLog);
builder.Services.AddSingleton(runLog);

builder.Configuration.AddQualityCatalogue();
builder.Services.AddCatalogModule(builder.Configuration);
builder.Services.AddWardrobeModule(CatalogModule.GetConnectionString(builder.Configuration));
builder.Services.AddPicksModule(builder.Configuration, CatalogModule.GetConnectionString(builder.Configuration));

if (command is null)
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(
            storage => storage.UseNpgsqlConnection(CatalogModule.GetConnectionString(builder.Configuration)),
            new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = true }));

    builder.Services.AddHangfireServer(options => options.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 2));

    // Migrates, syncs shops from config and (re)creates one recurring job per shop on every start.
    builder.Services.AddHostedService<RecurringJobsSetup>();
}

using var host = builder.Build();

if (command is null)
{
    await host.RunAsync();
    return 0;
}

return await command.RunAsync(host.Services, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
