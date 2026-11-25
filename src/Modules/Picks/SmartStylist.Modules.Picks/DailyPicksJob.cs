using Hangfire;

namespace SmartStylist.Modules.Picks;

/// <summary>Hangfire entry point for the daily picks. Scheduled only when a Telegram bot token is set.</summary>
public sealed class DailyPicksJob(DailyPicks picks)
{
    public const string RecurringJobId = "daily-picks";

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
    public Task RunAsync(CancellationToken cancellationToken) => picks.SendAsync(cancellationToken);
}
