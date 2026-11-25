using SmartStylist.Modules.Wardrobe.Domain;

namespace SmartStylist.Modules.Wardrobe.Style;

/// <summary>A catalogue brand as the quiz sees it. The host builds these from the quality catalogue.</summary>
public sealed record BrandCard(string Name, IReadOnlyList<string> Styles, IReadOnlyList<string> Categories, bool ForMen, bool ForWomen)
{
    public bool Dresses(ShopsFor who) => who switch
    {
        ShopsFor.Men => ForMen,
        ShopsFor.Women => ForWomen,
        _ => ForMen || ForWomen,
    };
}

/// <param name="Liked">Style words of loved brands that skipped brands did not cancel out, most frequent first.</param>
/// <param name="Avoided">Style words that came up more in skipped brands than loved ones.</param>
/// <param name="Categories">What the loved brands are good at, most frequent first.</param>
public sealed record DetectedStyle(IReadOnlyList<string> Liked, IReadOnlyList<string> Avoided, IReadOnlyList<string> Categories);

/// <param name="Score">Higher fits better; 0 is neutral. Meaningless when Excluded.</param>
/// <param name="Excluded">Never suggest: a skipped brand, a no-go word, or not their clothes.</param>
/// <param name="Reason">One line a person can read.</param>
public sealed record StyleMatch(int Score, bool Excluded, string Reason);

/// <summary>
/// Detects a style from the brands a person loves or skips, and matches a brand (and optionally an item's words)
/// against a profile. Pure functions on the catalogue's Styles and Categories words.
/// </summary>
public static class StyleQuiz
{
    public static DetectedStyle Detect(IEnumerable<BrandCard> loved, IEnumerable<BrandCard> skipped)
    {
        var lovedList = loved.ToList();
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in lovedList.SelectMany(b => b.Styles))
        {
            scores[word] = scores.GetValueOrDefault(word) + 1;
        }

        foreach (var word in skipped.SelectMany(b => b.Styles))
        {
            scores[word] = scores.GetValueOrDefault(word) - 1;
        }

        var categories = lovedList.SelectMany(b => b.Categories)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key)
            .ToList();

        return new DetectedStyle(
            scores.Where(s => s.Value > 0).OrderByDescending(s => s.Value).ThenBy(s => s.Key).Select(s => s.Key).ToList(),
            scores.Where(s => s.Value < 0).OrderBy(s => s.Value).ThenBy(s => s.Key).Select(s => s.Key).ToList(),
            categories);
    }

    /// <summary>How well a brand, or one of its items described by <paramref name="itemText"/>, fits the person.</summary>
    public static StyleMatch Match(StyleProfile profile, BrandCard brand, string? itemText = null)
    {
        if (!brand.Dresses(profile.ShopsFor))
        {
            return new StyleMatch(0, true, $"{brand.Name} does not make clothes for them.");
        }

        if (Has(profile.SkippedBrands, brand.Name))
        {
            return new StyleMatch(0, true, $"{brand.Name} is not them (skipped in the quiz).");
        }

        var text = Words(string.Join(" ", brand.Categories.Append(itemText ?? "")));
        if (profile.NoGo.FirstOrDefault(n => Words(n).Trim().Length > 0 && text.Contains(Words(n), StringComparison.Ordinal)) is { } noGo)
        {
            return new StyleMatch(0, true, $"No-go: {noGo}.");
        }

        var liked = brand.Styles.Where(s => Has(profile.LikedStyles, s)).ToList();
        var avoided = brand.Styles.Where(s => Has(profile.AvoidedStyles, s)).ToList();
        var wanted = profile.WantedCategories.Where(c => Words(c).Trim().Length > 0 && text.Contains(Words(c), StringComparison.Ordinal)).ToList();
        var loved = Has(profile.LovedBrands, brand.Name);

        var score = (loved ? 2 : 0) + liked.Count - 2 * avoided.Count + (wanted.Count > 0 ? 1 : 0);
        var why = new List<string>();
        if (loved) why.Add("a brand they love");
        if (liked.Count > 0) why.Add(string.Join(", ", liked));
        if (wanted.Count > 0) why.Add($"wants {string.Join(", ", wanted)}");
        if (avoided.Count > 0) why.Add($"but {string.Join(", ", avoided)} is not them");
        return new StyleMatch(score, false, why.Count == 0 ? "Nothing in their style either way." : Capitalize(string.Join("; ", why)) + ".");
    }

    private static bool Has(IEnumerable<string> list, string value) =>
        list.Any(v => string.Equals(v.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>" word word " in lower case, plural "s" dropped, so " suit " is found in " navy suits ".</summary>
    private static string Words(string text)
    {
        var words = new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) ? w[..^1] : w);
        return " " + string.Join(" ", words) + " ";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
