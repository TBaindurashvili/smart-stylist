using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Persistence;

namespace SmartStylist.Modules.Wardrobe.Fit;

/// <summary>Adds, lists and removes one person's fit records.</summary>
public sealed class FitLedger(WardrobeDbContext db, TimeProvider time)
{
    public Task<List<FitEntry>> ListAsync(long profileId, CancellationToken cancellationToken) =>
        db.FitEntries.AsNoTracking().Where(e => e.ProfileId == profileId).OrderBy(e => e.Brand).ThenByDescending(e => e.RecordedOn).ToListAsync(cancellationToken);

    public async Task<FitEntry> AddAsync(long profileId, string brand, string? model, string? category, string size, FitVerdict verdict, string? note,
        bool kept, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(brand) || string.IsNullOrWhiteSpace(size))
        {
            throw new ArgumentException("Brand and size are required.");
        }

        var entry = new FitEntry
        {
            ProfileId = profileId,
            Brand = brand.Trim(),
            Model = Clean(model),
            Category = Clean(category),
            Size = size.Trim(),
            Verdict = verdict,
            Note = Clean(note),
            Kept = kept,
            RecordedOn = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
        };
        db.FitEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task RemoveAsync(long profileId, long id, CancellationToken cancellationToken) =>
        await db.FitEntries.Where(e => e.ProfileId == profileId && e.Id == id).ExecuteDeleteAsync(cancellationToken);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
