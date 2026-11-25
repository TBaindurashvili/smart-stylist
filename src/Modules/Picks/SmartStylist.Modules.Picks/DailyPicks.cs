using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartStylist.Modules.Catalog.Catalogue;
using SmartStylist.Modules.Catalog.Reporting;
using SmartStylist.Modules.Picks.Domain;
using SmartStylist.Modules.Picks.Persistence;
using SmartStylist.Modules.Picks.Telegram;
using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Style;

namespace SmartStylist.Modules.Picks;

/// <summary>A deal on a quality brand, judged for one person.</summary>
public sealed record Pick(DealRow Deal, string Brand, StyleMatch Style, SizeAdvice Size, PickJudgement Judgement);

/// <summary>What the daily run did for one person.</summary>
public sealed record PicksSent(string Person, int Told, int AlreadyTold, string? Error = null);

/// <summary>
/// Smart picks: every fresh deal on a quality-catalogue brand, judged for a person by <see cref="PickRule"/>, and the
/// daily run that tells each linked person about new ones. No pick, no message.
/// </summary>
public sealed class DailyPicks(
    DealsReport deals,
    QualityCatalogue catalogue,
    StyleProfiles profiles,
    FitAdvisor fit,
    PicksDbContext db,
    TelegramBot telegram,
    TimeProvider time,
    ILogger<DailyPicks> logger)
{
    /// <summary>Only prices seen this recently become picks; an older sale may have ended.</summary>
    public const int FreshDays = 3;

    /// <summary>Every fresh deal judged for one person: picks to tell first, best score first.</summary>
    public async Task<IReadOnlyList<Pick>> PreviewAsync(long profileId, CancellationToken cancellationToken) =>
        await profiles.GetAsync(profileId, cancellationToken) is { } profile
            ? await JudgeAsync(profile, await FreshDealsAsync(cancellationToken), cancellationToken)
            : [];

    /// <summary>The daily run: link new Telegram chats, then message each linked person the picks not told before.</summary>
    public async Task<IReadOnlyList<PicksSent>> SendAsync(CancellationToken cancellationToken)
    {
        await telegram.SyncLinksAsync(cancellationToken);
        var subscribers = await db.Subscribers.AsNoTracking().Where(s => s.TelegramChatId != null).ToListAsync(cancellationToken);
        if (subscribers.Count == 0)
        {
            return [];
        }

        var fresh = await FreshDealsAsync(cancellationToken);
        var today = Today();
        var results = new List<PicksSent>();
        foreach (var subscriber in subscribers)
        {
            if (await profiles.GetAsync(subscriber.ProfileId, cancellationToken) is not { } profile)
            {
                continue;
            }

            var told = (await JudgeAsync(profile, fresh, cancellationToken)).Where(p => p.Judgement.Tell).ToList();
            var before = await db.SentPicks.AsNoTracking().Where(s => s.ProfileId == profile.Id).ToListAsync(cancellationToken);
            // Told again only when it got cheaper than when we last said "buy now".
            var news = told.Where(p => !before.Any(s => s.ProductId == p.Deal.ProductId && s.Currency == p.Deal.Currency && s.Price <= p.Deal.Price)).ToList();
            if (news.Count == 0)
            {
                results.Add(new PicksSent(profile.Name, 0, told.Count));
                continue;
            }

            try
            {
                await telegram.SendAsync(subscriber.TelegramChatId!.Value, PickMessage.Telegram(profile.Name, news), cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                logger.LogWarning("Picks for {Person} not sent: {Error}", profile.Name, ex.Message);
                results.Add(new PicksSent(profile.Name, 0, told.Count - news.Count, ex.Message));
                continue;
            }

            db.SentPicks.AddRange(news.Select(p => new SentPick
            {
                ProfileId = profile.Id, ProductId = p.Deal.ProductId, Title = Cut(p.Deal.Title, 500), Url = Cut(p.Deal.Url, 2000),
                Price = p.Deal.Price, Currency = p.Deal.Currency, Score = p.Judgement.Score, SentOn = today,
            }));
            await db.SaveChangesAsync(cancellationToken);
            results.Add(new PicksSent(profile.Name, news.Count, told.Count - news.Count));
        }

        return results;
    }

    private async Task<IReadOnlyList<Pick>> JudgeAsync(StyleProfile profile, IReadOnlyList<DealRow> fresh, CancellationToken cancellationToken)
    {
        var sizeFor = await fit.ForPersonAsync(profile.Id, cancellationToken);
        var picks = new List<Pick>();
        foreach (var deal in fresh)
        {
            // Quality first: only brands in the quality catalogue are ever suggested.
            if (catalogue.FindBrand(deal.Brand, deal.Title) is not { } brand)
            {
                continue;
            }

            var style = StyleQuiz.Match(profile, BrandCards.Of(brand), deal.Title);
            var size = sizeFor(brand.Name, deal.Title);
            picks.Add(new Pick(deal, brand.Name, style, size, PickRule.Judge(deal.Assessment, deal.Price, style, size)));
        }

        return picks
            .OrderByDescending(p => p.Judgement.Tell)
            .ThenByDescending(p => p.Judgement.Score)
            .ThenByDescending(p => p.Deal.Assessment.RealDiscount ?? 0)
            .ToList();
    }

    private async Task<IReadOnlyList<DealRow>> FreshDealsAsync(CancellationToken cancellationToken)
    {
        var since = Today().AddDays(-FreshDays);
        return (await deals.BuildAsync(cancellationToken)).Where(d => d.ObservedOn >= since).ToList();
    }

    private DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;
}
