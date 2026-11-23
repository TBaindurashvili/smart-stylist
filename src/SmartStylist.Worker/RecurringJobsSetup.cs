using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartStylist.Modules.Catalog;
using SmartStylist.Modules.Catalog.Affiliates;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Newsletters;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Worker;

/// <summary>Migrates the database, syncs shops from config and keeps one daily Hangfire job per enabled shop.</summary>
internal sealed class RecurringJobsSetup(
    IServiceScopeFactory scopes,
    IRecurringJobManager recurringJobs,
    IOptions<CatalogOptions> options,
    IOptions<NewsletterOptions> newsletterOptions,
    IOptions<AwinOptions> awinOptions,
    ILogger<RecurringJobsSetup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync(cancellationToken);
        var shops = await scope.ServiceProvider.GetRequiredService<ShopCatalogSync>().SyncAsync(cancellationToken);

        foreach (var shop in shops)
        {
            var jobId = CollectPricesJob.RecurringJobId(shop.Key);
            if (shop.Enabled)
            {
                recurringJobs.AddOrUpdate<CollectPricesJob>(
                    jobId,
                    job => job.RunAsync(shop.Key, CancellationToken.None),
                    options.Value.DailyCron,
                    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            }
            else
            {
                recurringJobs.RemoveIfExists(jobId);
            }
        }

        logger.LogInformation("Scheduled daily price collection for {Count} shops ({Cron} UTC)",
            shops.Count(s => s.Enabled), options.Value.DailyCron);

        var newsletters = newsletterOptions.Value;
        if (string.IsNullOrWhiteSpace(newsletters.Imap.Host))
        {
            recurringJobs.RemoveIfExists(ImportNewslettersJob.RecurringJobId);
            logger.LogInformation("Sale email reading is off: Newsletters:Imap:Host is empty");
        }
        else
        {
            recurringJobs.AddOrUpdate<ImportNewslettersJob>(
                ImportNewslettersJob.RecurringJobId,
                job => job.RunAsync(CancellationToken.None),
                newsletters.Cron,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            logger.LogInformation("Scheduled sale email reading from {Host}/{Folder} ({Cron} UTC)",
                newsletters.Imap.Host, newsletters.Imap.Folder, newsletters.Cron);
        }

        var awin = awinOptions.Value;
        if (string.IsNullOrWhiteSpace(awin.ApiKey))
        {
            recurringJobs.RemoveIfExists(ImportAwinFeedsJob.RecurringJobId);
            logger.LogInformation("Awin feed import is off: Awin:ApiKey is empty");
        }
        else
        {
            recurringJobs.AddOrUpdate<ImportAwinFeedsJob>(
                ImportAwinFeedsJob.RecurringJobId,
                job => job.RunAsync(CancellationToken.None),
                awin.Cron,
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
            logger.LogInformation("Scheduled Awin feed import ({Cron} UTC)", awin.Cron);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
