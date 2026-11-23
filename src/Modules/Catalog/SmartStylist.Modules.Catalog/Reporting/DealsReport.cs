using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Reporting;

public enum DealVerdict
{
    /// <summary>Lowest price we have seen, well below the usual price.</summary>
    BestPrice,

    /// <summary>Clearly below the usual price, but it has been lower.</summary>
    GoodPrice,

    /// <summary>Not enough price history yet to judge the discount.</summary>
    StillLearning,

    /// <summary>The shop shows a "was" price, but this is about what it usually costs.</summary>
    FakeSale,

    /// <summary>Not on sale and not below the usual price. Left off the deals page.</summary>
    UsualPrice,
}

/// <summary>A price judged against the product's own history.</summary>
/// <param name="ClaimedDiscount">What the shop's crossed-out price says, 0.3 = 30% off.</param>
/// <param name="RealDiscount">Below the usual (median) price of earlier days; null while there is too little history.</param>
public sealed record DealAssessment(DealVerdict Verdict, decimal? ClaimedDiscount, decimal? RealDiscount, decimal? UsualPrice, decimal? LowestBefore, int Days);

public sealed record DealRow(
    long ProductId,
    string ShopName,
    string Title,
    string? Brand,
    string Url,
    string? ImageUrl,
    string Currency,
    decimal Price,
    decimal? ListPrice,
    DateOnly ObservedOn,
    PriceSource Source,
    DealAssessment Assessment);

/// <summary>
/// The deals worth a look: recent prices that are on sale or below their usual price, each with a verdict on
/// whether the discount is real. "Usual" is the median of the product's daily lowest price on earlier days.
/// </summary>
public sealed class DealsReport(CatalogDbContext db, TimeProvider time)
{
    /// <summary>Days of earlier prices needed before a verdict, about two weeks of daily prices.</summary>
    public const int MinHistoryDays = 14;

    public const decimal BestPriceDiscount = 0.15m;

    public const decimal GoodPriceDiscount = 0.10m;

    /// <summary>Only prices seen this recently are shown; older ones may have ended.</summary>
    public const int RecentDays = 14;

    private const int HistoryDays = 180;

    public static DealAssessment Assess(IReadOnlyList<decimal> earlierDailyLows, decimal price, decimal? listPrice)
    {
        decimal? claimed = listPrice > price ? Math.Round((listPrice.Value - price) / listPrice.Value, 4) : null;
        var days = earlierDailyLows.Count + 1;
        if (earlierDailyLows.Count < MinHistoryDays)
        {
            return new DealAssessment(DealVerdict.StillLearning, claimed, null, null, null, days);
        }

        var usual = Median(earlierDailyLows);
        var lowest = earlierDailyLows.Min();
        var real = usual <= 0 ? 0 : Math.Round((usual - price) / usual, 4);
        var verdict = real >= BestPriceDiscount && price <= lowest ? DealVerdict.BestPrice
            : real >= GoodPriceDiscount ? DealVerdict.GoodPrice
            : claimed is not null ? DealVerdict.FakeSale
            : DealVerdict.UsualPrice;
        return new DealAssessment(verdict, claimed, real, usual, lowest, days);
    }

    public async Task<IReadOnlyList<DealRow>> BuildAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var recentFrom = today.AddDays(-RecentDays);
        var historyFrom = today.AddDays(-HistoryDays);

        var products = await db.Products.AsNoTracking()
            .Where(p => p.Snapshots.Any(s => s.ObservedOn >= recentFrom))
            .Select(p => new
            {
                p.Id, ShopName = p.Shop.Name, p.Title, p.Brand, p.Url, p.ImageUrl,
                Snapshots = p.Snapshots
                    .Where(s => s.ObservedOn >= historyFrom)
                    .Select(s => new { s.ObservedOn, s.Price, s.ListPrice, s.Currency, s.Source })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        var rows = new List<DealRow>();
        foreach (var p in products)
        {
            var lastDay = p.Snapshots.Max(s => s.ObservedOn);
            var latest = p.Snapshots.Where(s => s.ObservedOn == lastDay).MinBy(s => s.Price)!;
            var earlier = p.Snapshots
                .Where(s => s.ObservedOn < lastDay && s.Currency == latest.Currency)
                .GroupBy(s => s.ObservedOn)
                .Select(g => g.Min(s => s.Price))
                .ToList();

            var assessment = Assess(earlier, latest.Price, latest.ListPrice);
            if (assessment.Verdict == DealVerdict.UsualPrice
                || (assessment.Verdict == DealVerdict.StillLearning && assessment.ClaimedDiscount is null))
            {
                continue;
            }

            rows.Add(new DealRow(p.Id, p.ShopName, p.Title, p.Brand, p.Url, p.ImageUrl, latest.Currency, latest.Price,
                latest.ListPrice, lastDay, latest.Source, assessment));
        }

        return rows
            .OrderBy(r => r.Assessment.Verdict)
            .ThenByDescending(r => r.Assessment.RealDiscount ?? r.Assessment.ClaimedDiscount ?? 0)
            .ToList();
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
