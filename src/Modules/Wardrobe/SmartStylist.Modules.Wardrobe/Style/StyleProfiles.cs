using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Persistence;

namespace SmartStylist.Modules.Wardrobe.Style;

/// <summary>Lists, reads and saves style profiles: one per person.</summary>
public sealed class StyleProfiles(WardrobeDbContext db, TimeProvider time)
{
    public Task<List<StyleProfile>> ListAsync(CancellationToken cancellationToken) =>
        db.StyleProfiles.AsNoTracking().OrderBy(p => p.Id).ToListAsync(cancellationToken);

    public Task<StyleProfile?> GetAsync(long id, CancellationToken cancellationToken) =>
        db.StyleProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    /// <summary>Adds the profile (Id 0) or replaces the saved one. Lists are trimmed and de-duplicated.</summary>
    public async Task<StyleProfile> SaveAsync(StyleProfile profile, CancellationToken cancellationToken)
    {
        var name = profile.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ArgumentException("A name is required.");
        }

        if (await db.StyleProfiles.AnyAsync(p => p.Id != profile.Id && p.Name.ToLower() == name.ToLower(), cancellationToken))
        {
            throw new ArgumentException($"There is already a profile called {name}.");
        }

        var saved = profile.Id == 0
            ? db.StyleProfiles.Add(new StyleProfile { Name = name }).Entity
            : await db.StyleProfiles.FirstOrDefaultAsync(p => p.Id == profile.Id, cancellationToken)
              ?? throw new ArgumentException($"No profile {profile.Id}.");

        saved.Name = name;
        saved.ShopsFor = profile.ShopsFor;
        saved.LikedStyles = Clean(profile.LikedStyles);
        saved.AvoidedStyles = Clean(profile.AvoidedStyles).Except(saved.LikedStyles, StringComparer.OrdinalIgnoreCase).ToList();
        saved.WantedCategories = Clean(profile.WantedCategories);
        saved.NoGo = Clean(profile.NoGo);
        saved.LovedBrands = Clean(profile.LovedBrands);
        saved.SkippedBrands = Clean(profile.SkippedBrands).Except(saved.LovedBrands, StringComparer.OrdinalIgnoreCase).ToList();
        saved.Goal = Clean(profile.Goal);
        saved.TopSize = Clean(profile.TopSize);
        saved.BottomSize = Clean(profile.BottomSize);
        saved.ShoeSize = Clean(profile.ShoeSize);
        saved.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
