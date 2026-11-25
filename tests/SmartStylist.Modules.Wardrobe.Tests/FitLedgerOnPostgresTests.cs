using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SmartStylist.Modules.Wardrobe.Domain;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Persistence;
using SmartStylist.Modules.Wardrobe.Style;
using Testcontainers.PostgreSql;

namespace SmartStylist.Modules.Wardrobe.Tests;

/// <summary>Runs the real migrations on Postgres (needs Docker), then uses profiles and the ledger end to end.</summary>
public class FitLedgerOnPostgresTests : IClassFixture<FitLedgerOnPostgresTests.Database>
{
    private readonly Database _database;

    public FitLedgerOnPostgresTests(Database database) => _database = database;

    [Fact]
    public async Task Fits_recorded_before_profiles_move_to_a_profile_called_Me()
    {
        // Its own database: this test steps through migrations one by one.
        var legacy = new Npgsql.NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "legacy" }.ConnectionString;
        var services = new ServiceCollection().AddWardrobeModule(legacy).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WardrobeDbContext>();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrator.MigrateAsync("20261123145736_AddFitLedger");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO wardrobe.fit_entries (brand, size, verdict, recorded_on, kept) VALUES ('Uniqlo', 'XL', 'Perfect', '2026-11-20', true)");
        await migrator.MigrateAsync();

        var me = Assert.Single(await scope.ServiceProvider.GetRequiredService<StyleProfiles>().ListAsync(CancellationToken.None));
        Assert.Equal("Me", me.Name);
        Assert.Equal("XL", Assert.Single(await scope.ServiceProvider.GetRequiredService<FitLedger>().ListAsync(me.Id, CancellationToken.None)).Size);
    }

    [Fact]
    public async Task Each_person_has_their_own_profile_and_fits()
    {
        var services = new ServiceCollection().AddWardrobeModule(_database.ConnectionString).BuildServiceProvider();
        long temo, nino;
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<WardrobeDbContext>().Database.MigrateAsync();
            var profiles = scope.ServiceProvider.GetRequiredService<StyleProfiles>();
            temo = (await profiles.SaveAsync(new StyleProfile
            {
                Name = "Temo", ShopsFor = ShopsFor.Men, LikedStyles = ["minimal", " minimal ", "classic"], NoGo = ["logo"], TopSize = "L",
            }, CancellationToken.None)).Id;
            nino = (await profiles.SaveAsync(new StyleProfile { Name = "Nino", ShopsFor = ShopsFor.Women, TopSize = "S" }, CancellationToken.None)).Id;
            await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveAsync(new StyleProfile { Name = "temo" }, CancellationToken.None));

            var ledger = scope.ServiceProvider.GetRequiredService<FitLedger>();
            await ledger.AddAsync(temo, "Uniqlo", "Merino crew", "knit", "XL", FitVerdict.Perfect, null, true, CancellationToken.None);
            var removed = await ledger.AddAsync(temo, "Uniqlo", null, "tee", "M", FitVerdict.TooSmall, "tight chest", false, CancellationToken.None);
            await ledger.RemoveAsync(nino, removed.Id, CancellationToken.None); // not Nino's: stays
            await ledger.RemoveAsync(temo, removed.Id, CancellationToken.None);
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<StyleProfiles>().GetAsync(temo, CancellationToken.None);
            Assert.Equal(["minimal", "classic"], saved!.LikedStyles);

            var entry = Assert.Single(await scope.ServiceProvider.GetRequiredService<FitLedger>().ListAsync(temo, CancellationToken.None));
            Assert.Equal(FitVerdict.Perfect, entry.Verdict);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<FitLedger>().ListAsync(nino, CancellationToken.None));

            var advisor = scope.ServiceProvider.GetRequiredService<FitAdvisor>();
            Assert.Equal("XL", (await advisor.SuggestAsync(temo, "UNIQLO", null, "knit", CancellationToken.None)).Size);
            var ninos = await advisor.SuggestAsync(nino, "Uniqlo", null, "knit", CancellationToken.None);
            Assert.Equal(("S", FitConfidence.Low), (ninos.Size, ninos.Confidence));
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
