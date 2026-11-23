using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Wardrobe.Domain;

namespace SmartStylist.Modules.Wardrobe.Persistence;

public sealed class WardrobeDbContext(DbContextOptions<WardrobeDbContext> options) : DbContext(options)
{
    public const string Schema = "wardrobe";

    public DbSet<FitEntry> FitEntries => Set<FitEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<FitEntry>(fit =>
        {
            fit.HasIndex(f => f.Brand);
            fit.Property(f => f.Brand).HasMaxLength(200);
            fit.Property(f => f.Model).HasMaxLength(200);
            fit.Property(f => f.Category).HasMaxLength(100);
            fit.Property(f => f.Size).HasMaxLength(50);
            fit.Property(f => f.Verdict).HasConversion<string>().HasMaxLength(16);
            fit.Property(f => f.Note).HasMaxLength(1000);
        });
    }
}
