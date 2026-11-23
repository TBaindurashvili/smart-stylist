using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Persistence;
using Testcontainers.PostgreSql;

namespace SmartStylist.Modules.Wardrobe.Tests;

/// <summary>Runs the real migration on Postgres (needs Docker), then uses the ledger end to end.</summary>
public class FitLedgerOnPostgresTests : IClassFixture<FitLedgerOnPostgresTests.Database>
{
    private readonly Database _database;

    public FitLedgerOnPostgresTests(Database database) => _database = database;

    [Fact]
    public async Task Records_fits_and_suggests_a_size()
    {
        var services = new ServiceCollection().AddWardrobeModule(_database.ConnectionString).BuildServiceProvider();
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<WardrobeDbContext>().Database.MigrateAsync();
            var ledger = scope.ServiceProvider.GetRequiredService<FitLedger>();
            await ledger.AddAsync("Uniqlo", "Merino crew", "knit", "XL", FitVerdict.Perfect, null, true, CancellationToken.None);
            var removed = await ledger.AddAsync("Uniqlo", null, "tee", "M", FitVerdict.TooSmall, "tight chest", false, CancellationToken.None);
            await ledger.RemoveAsync(removed.Id, CancellationToken.None);
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var entry = Assert.Single(await scope.ServiceProvider.GetRequiredService<FitLedger>().ListAsync(CancellationToken.None));
            Assert.Equal(FitVerdict.Perfect, entry.Verdict);

            var advice = await scope.ServiceProvider.GetRequiredService<FitAdvisor>().SuggestAsync("UNIQLO", null, "knit", CancellationToken.None);
            Assert.Equal("XL", advice.Size);
        }
    }

    public sealed class Database : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();

        public string ConnectionString => _container.GetConnectionString();

        public Task InitializeAsync() => _container.StartAsync();

        public Task DisposeAsync() => _container.DisposeAsync().AsTask();
    }
}
