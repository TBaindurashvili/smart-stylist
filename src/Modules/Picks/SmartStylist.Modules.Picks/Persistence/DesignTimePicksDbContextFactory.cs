using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartStylist.Modules.Picks.Persistence;

/// <summary>Used only by `dotnet ef` to build migrations; it never opens a connection.</summary>
internal sealed class DesignTimePicksDbContextFactory : IDesignTimeDbContextFactory<PicksDbContext>
{
    public PicksDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PicksDbContext>()
            .UseNpgsql("Host=localhost;Database=smart_stylist", npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PicksDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options);
}
