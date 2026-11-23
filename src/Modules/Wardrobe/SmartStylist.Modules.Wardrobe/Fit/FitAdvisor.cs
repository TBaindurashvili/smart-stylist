using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Persistence;

namespace SmartStylist.Modules.Wardrobe.Fit;

public enum FitConfidence
{
    None,
    Low,
    Medium,
    High,
}

/// <param name="Size">The size to buy, or null when the ledger can't say.</param>
/// <param name="Reason">One line on where the advice comes from.</param>
public sealed record SizeAdvice(string? Size, FitConfidence Confidence, string Reason, IReadOnlyList<FitEntry> Evidence)
{
    public static SizeAdvice Unknown(string brand) =>
        new(null, FitConfidence.None, $"No fit records for {brand} yet. Use the size chart, then record how it fits.", []);
}

/// <summary>Suggests a size from the fit ledger: the same model first, then the same kind of item, then the brand.</summary>
public sealed class FitAdvisor(WardrobeDbContext db)
{
    private static readonly string[] LetterSizes = ["XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL"];

    public async Task<SizeAdvice> SuggestAsync(string brand, string? model, string? category, CancellationToken cancellationToken)
    {
        var key = Normalize(brand);
        if (key.Length == 0)
        {
            return SizeAdvice.Unknown(brand);
        }

        var entries = await db.FitEntries.AsNoTracking().ToListAsync(cancellationToken);
        return Suggest(entries.Where(e => Normalize(e.Brand) == key).ToList(), brand, model, category);
    }

    /// <summary>All brands in the ledger, for matching a product's brand to fit advice.</summary>
    public async Task<IReadOnlyDictionary<string, SizeAdvice>> SuggestForBrandsAsync(IEnumerable<string?> brands, CancellationToken cancellationToken)
    {
        var wanted = brands.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entries = await db.FitEntries.AsNoTracking().ToListAsync(cancellationToken);
        return wanted.ToDictionary(
            b => b,
            b => Suggest(entries.Where(e => Normalize(e.Brand) == Normalize(b)).ToList(), b, null, null),
            StringComparer.OrdinalIgnoreCase);
    }

    public static SizeAdvice Suggest(IReadOnlyList<FitEntry> brandEntries, string brand, string? model, string? category)
    {
        if (brandEntries.Count == 0)
        {
            return SizeAdvice.Unknown(brand);
        }

        var byModel = string.IsNullOrWhiteSpace(model) ? [] : brandEntries.Where(e => Same(e.Model, model)).ToList();
        var byCategory = string.IsNullOrWhiteSpace(category) ? [] : brandEntries.Where(e => Same(e.Category, category)).ToList();
        var (scope, evidence, best) = byModel.Count > 0 ? ("this model", byModel, FitConfidence.High)
            : byCategory.Count > 0 ? ($"{brand} {category}", byCategory, FitConfidence.Medium)
            : ($"{brand}", brandEntries.ToList(), FitConfidence.Medium);

        var newest = evidence.OrderByDescending(e => e.RecordedOn).ThenByDescending(e => e.Id).ToList();

        if (newest.FirstOrDefault(e => e.Verdict == FitVerdict.Perfect) is { } perfect)
        {
            return new SizeAdvice(perfect.Size, best, $"{perfect.Size}: fitted perfectly ({Describe(perfect)}).", newest);
        }

        if (newest.FirstOrDefault(e => e.Verdict is FitVerdict.Snug or FitVerdict.Roomy) is { } close)
        {
            var feel = close.Verdict == FitVerdict.Snug ? "a little snug" : "a little roomy";
            return new SizeAdvice(close.Size, Lower(best), $"{close.Size}, but it was {feel} ({Describe(close)}).", newest);
        }

        // Only "too small" / "too big" so far: step one letter size away when we can.
        var miss = newest[0];
        var step = miss.Verdict == FitVerdict.TooBig ? -1 : 1;
        var next = Step(miss.Size, step);
        var direction = step < 0 ? "down" : "up";
        return next is null
            ? new SizeAdvice(null, FitConfidence.Low, $"{miss.Size} was {(step < 0 ? "too big" : "too small")} ({Describe(miss)}). Go one size {direction}.", newest)
            : new SizeAdvice(next, FitConfidence.Low, $"{miss.Size} was {(step < 0 ? "too big" : "too small")} ({Describe(miss)}), so try {next}.", newest);
    }

    private static FitConfidence Lower(FitConfidence confidence) => confidence == FitConfidence.High ? FitConfidence.Medium : FitConfidence.Low;

    private static string Describe(FitEntry e) =>
        string.Join(" ", new[] { e.Brand, e.Model, e.Category }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string? Step(string size, int step)
    {
        var i = Array.FindIndex(LetterSizes, s => s.Equals(size.Trim(), StringComparison.OrdinalIgnoreCase));
        var j = i + step;
        return i < 0 || j < 0 || j >= LetterSizes.Length ? null : LetterSizes[j];
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Normalize(a) == Normalize(b);

    /// <summary>"Levi's", "LEVIS" and "levi’s" are one brand.</summary>
    public static string Normalize(string? value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
