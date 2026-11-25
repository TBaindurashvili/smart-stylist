using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Reporting;
using SmartStylist.Modules.Picks.Persistence;

namespace SmartStylist.Modules.Picks;

/// <param name="Sent">"Buy now" picks told so far.</param>
/// <param name="Right">Checked picks whose price did not go lower within <see cref="PickOutcomes.CheckDays"/> days.</param>
/// <param name="Wrong">Checked picks that were cheaper later: "wait" would have been the better call.</param>
/// <param name="NotSeenAgain">Old enough to check, but no later price was seen, so it cannot be judged.</param>
/// <param name="Pending">Told less than <see cref="PickOutcomes.CheckDays"/> days ago.</param>
public sealed record OutcomeSummary(int Sent, int Right, int Wrong, int NotSeenAgain, int Pending)
{
    public string Describe() => Right + Wrong == 0
        ? $"{Sent} buy-now picks told; none old enough to check yet ({Pending} within {PickOutcomes.CheckDays} days, {NotSeenAgain} not seen again)."
        : $"Buy now was right {Right} of {Right + Wrong} times (goal: 8 of 10). {Pending} still within {PickOutcomes.CheckDays} days, {NotSeenAgain} not seen again.";
}

/// <summary>The mission's check: was "buy now" right? Judged from later prices, so nobody has to report back.</summary>
public sealed class PickOutcomes(PicksDbContext db, PriceHistoryService history, TimeProvider time)
{
    public const int CheckDays = 30;

    public async Task<OutcomeSummary> SummarizeAsync(long? profileId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var picks = await db.SentPicks.AsNoTracking()
            .Where(p => profileId == null || p.ProfileId == profileId)
            .ToListAsync(cancellationToken);
        var due = picks.Where(p => p.SentOn.AddDays(CheckDays) <= today).ToList();
        var later = due.Count == 0 ? [] : await history.GetPricesAsync(due.Select(p => p.ProductId).Distinct().ToList(), due.Min(p => p.SentOn).AddDays(1), cancellationToken);

        int right = 0, wrong = 0, notSeen = 0;
        foreach (var pick in due)
        {
            var after = later
                .Where(p => p.ProductId == pick.ProductId && p.Currency == pick.Currency && p.Day > pick.SentOn && p.Day <= pick.SentOn.AddDays(CheckDays))
                .ToList();
            if (after.Count == 0) notSeen++;
            else if (after.Min(p => p.Price) < pick.Price) wrong++;
            else right++;
        }

        return new OutcomeSummary(picks.Count, right, wrong, notSeen, picks.Count - due.Count);
    }
}
