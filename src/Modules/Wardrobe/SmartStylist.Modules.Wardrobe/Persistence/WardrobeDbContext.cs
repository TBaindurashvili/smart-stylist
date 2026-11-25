using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Wardrobe.Domain;

namespace SmartStylist.Modules.Wardrobe.Persistence;

public sealed class WardrobeDbContext(DbContextOptions<WardrobeDbContext> options) : DbContext(options)
{
    public const string Schema = "wardrobe";

    public DbSet<FitEntry> FitEntries => Set<FitEntry>();

    public DbSet<StyleProfile> StyleProfiles => Set<StyleProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<FitEntry>(fit =>
        {
            fit.HasIndex(f => new { f.ProfileId, f.Brand });
            fit.HasOne<StyleProfile>().WithMany().HasForeignKey(f => f.ProfileId).OnDelete(DeleteBehavior.Cascade);
            fit.Property(f => f.Brand).HasMaxLength(200);
            fit.Property(f => f.Model).HasMaxLength(200);
            fit.Property(f => f.Category).HasMaxLength(100);
            fit.Property(f => f.Size).HasMaxLength(50);
            fit.Property(f => f.Verdict).HasConversion<string>().HasMaxLength(16);
            fit.Property(f => f.Note).HasMaxLength(1000);
        });

        modelBuilder.Entity<StyleProfile>(profile =>
        {
            profile.HasIndex(p => p.Name).IsUnique();
            profile.Property(p => p.Name).HasMaxLength(100);
            profile.Property(p => p.ShopsFor).HasConversion<string>().HasMaxLength(8);
            profile.Property(p => p.Goal).HasMaxLength(1000);
            profile.Property(p => p.TopSize).HasMaxLength(50);
            profile.Property(p => p.BottomSize).HasMaxLength(50);
            profile.Property(p => p.ShoeSize).HasMaxLength(50);
        });
    }
}
