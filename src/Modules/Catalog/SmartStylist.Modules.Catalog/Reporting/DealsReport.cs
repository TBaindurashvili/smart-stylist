using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Catalog.Domain;
using SmartStylist.Modules.Catalog.Persistence;

namespace SmartStylist.Modules.Catalog.Reporting;

public enum DealVerdict
{
    /// <summary>Never been this cheap, or a rare sale on an item that is usually full price.</summary>
    BuyNow,

    /// <summary>Below the usual price, but it has been cheaper before.</summary>
    Wait,

    /// <summary>Not enough price history yet to judge the discount.</summary>
    StillLearning,

    /// <summary>The shop shows a "was" price, but this is about what it usually costs.</summary>
    FakeSale,

    /// <summary>Not on sale and not below the usual price. Left off the deals page.</summary>
    UsualPrice,
}

/// <summary>One earlier day of a product: its lowest price that day and whether the shop showed a "was" price.</summary>
public sealed record PricePoint(DateOnly Day, decimal Price, bool OnSale);

/// <summary>A price judged against the product's own history.</summary>
/// <param name="ClaimedDiscount">What the shop's crossed-out price says, 0.3 = 30% off.</param>
/// <param name="RealDiscount">Below the usual (median) price of earlier days; null while there is too little history.</param>
/// <param name="Reason">One line: buy now or wait, and why.</param>
public sealed record DealAssessment(
    DealVerdict Verdict,
    string Reason,
    decimal? ClaimedDiscount,
    decimal? RealDiscount,
    decimal? UsualPrice,
    decimal? LowestBefore,
    DateOnly? LowestOn,
    int Days);

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
/// The deals worth a look, judged by Temo's price rule instead of a percentage:
/// "When something usually isn't on sale and has never been cheaper, select it."
/// Buy now when the price is the lowest seen, or when an item that is rarely discounted is below its usual price.
/// Wait when it has been cheaper before. "Usual" is the median of the daily lowest price on earlier days.
/// </summary>
public sealed class DealsReport(CatalogDbContext db, TimeProvider time)
{
    /// <summary>Days of earlier prices needed before a verdict, about two weeks of daily prices.</summary>
    public const int MinHistoryDays = 14;

    /// <summary>An item on sale on fewer than this share of earlier days is "usually not on sale".</summary>
    public const decimal RarelyOnSaleShare = 0.2m;

    /// <summary>Only prices seen this recently are shown; older ones may have ended.</summary>
    public const int RecentDays = 14;

    private const int HistoryDays = 365;

    public static DealAssessment Assess(IReadOnlyList<PricePoint> earlier, decimal price, decimal? listPrice, string currency = "")
    {
        decimal? claimed = listPrice > price ? Math.Round((listPrice.Value - price) / listPrice.Value, 4) : null;
        var days = earlier.Count + 1;
        if (earlier.Count < MinHistoryDays)
        {
            return new DealAssessment(DealVerdict.StillLearning,
                $"Only {days} day(s) of prices so far, so it's too early to say whether this sale is real.",
                claimed, null, null, null, null, days);
        }

        var usual = Median(earlier.Select(p => p.Price).ToList());
        var lowestPoint = earlier.OrderBy(p => p.Price).ThenByDescending(p => p.Day).First();
        var lowest = lowestPoint.Price;
        var real = usual <= 0 ? 0 : Math.Round((usual - price) / usual, 4);
        var saleDays = earlier.Count(p => p.OnSale || p.Price < usual);
        var rarelyOnSale = (decimal)saleDays / earlier.Count < RarelyOnSaleShare;

        string Money(decimal amount) => string.Create(CultureInfo.InvariantCulture, $"{amount:0.00} {currency}").TrimEnd();
        string Day(DateOnly day) => day.ToString("MMM d", CultureInfo.InvariantCulture);

        DealAssessment Result(DealVerdict verdict, string reason) =>
            new(verdict, reason, claimed, real, usual, lowest, lowestPoint.Day, days);

        if (price < usual && price <= lowest)
        {
            return Result(DealVerdict.BuyNow, price < lowest
                ? $"Buy now: never been this cheap in {days} days (lowest before {Money(lowest)}, usually {Money(usual)})."
                : $"Buy now: matches its lowest price in {days} days (usually {Money(usual)}).");
        }

        if (price < usual && rarelyOnSale)
        {
            return Result(DealVerdict.BuyNow,
                $"Buy now: rarely discounted (on sale {saleDays} of {earlier.Count} days) and below its usual {Money(usual)}. It was {Money(lowest)} on {Day(lowestPoint.Day)}.");
        }

        if (price < usual)
        {
            return Result(DealVerdict.Wait,
                $"Wait: it was {Money(lowest)} on {Day(lowestPoint.Day)}, so a lower price is likely to come back.");
        }

        if (claimed is not null)
        {
            return Result(DealVerdict.FakeSale,
                $"Skip: the crossed-out price is not real, it usually costs {Money(usual)}.");
        }

        return Result(DealVerdict.UsualPrice, $"Its usual price ({Money(usual)}).");
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
                .Select(g => g.MinBy(s => s.Price)!)
                .Select(s => new PricePoint(s.ObservedOn, s.Price, s.ListPrice > s.Price))
                .ToList();

            var assessment = Assess(earlier, latest.Price, latest.ListPrice, latest.Currency);
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
