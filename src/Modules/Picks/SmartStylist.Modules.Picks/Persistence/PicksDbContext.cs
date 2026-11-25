using Microsoft.EntityFrameworkCore;
using SmartStylist.Modules.Picks.Domain;

namespace SmartStylist.Modules.Picks.Persistence;

public sealed class PicksDbContext(DbContextOptions<PicksDbContext> options) : DbContext(options)
{
    public const string Schema = "picks";

    public DbSet<Subscriber> Subscribers => Set<Subscriber>();

    public DbSet<SentPick> SentPicks => Set<SentPick>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Subscriber>(subscriber =>
        {
            subscriber.HasIndex(s => s.ProfileId).IsUnique();
            subscriber.HasIndex(s => s.LinkCode).IsUnique();
            subscriber.Property(s => s.LinkCode).HasMaxLength(32);
        });

        modelBuilder.Entity<SentPick>(pick =>
        {
            pick.HasIndex(p => new { p.ProfileId, p.ProductId });
            pick.Property(p => p.Title).HasMaxLength(500);
            pick.Property(p => p.Url).HasMaxLength(2000);
            pick.Property(p => p.Currency).HasMaxLength(3);
            pick.Property(p => p.Price).HasPrecision(12, 2);
        });
    }
}
