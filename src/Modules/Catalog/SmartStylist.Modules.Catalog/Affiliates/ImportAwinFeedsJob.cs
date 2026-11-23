using Hangfire;

namespace SmartStylist.Modules.Catalog.Affiliates;

/// <summary>Hangfire entry point: imports today's prices from the Awin product feeds.</summary>
public sealed class ImportAwinFeedsJob(AwinFeedImportService importer)
{
    public const string RecurringJobId = "import-awin-feeds";

    [AutomaticRetry(Attempts = 1)]
    [DisableConcurrentExecution(timeoutInSeconds: 60 * 60)]
    public Task RunAsync(CancellationToken cancellationToken) => importer.ImportAsync(cancellationToken);
}
