using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartStylist.Modules.Wardrobe.Persistence;

/// <summary>Used only by `dotnet ef` to build migrations; it never opens a connection.</summary>
internal sealed class DesignTimeWardrobeDbContextFactory : IDesignTimeDbContextFactory<WardrobeDbContext>
{
    public WardrobeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WardrobeDbContext>()
            .UseNpgsql("Host=localhost;Database=smart_stylist", npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WardrobeDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options);
}
