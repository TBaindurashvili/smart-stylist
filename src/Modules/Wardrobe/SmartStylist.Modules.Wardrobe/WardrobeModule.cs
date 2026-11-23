using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartStylist.Modules.Wardrobe.Fit;
using SmartStylist.Modules.Wardrobe.Persistence;

namespace SmartStylist.Modules.Wardrobe;

public static class WardrobeModule
{
    /// <summary>Registers the Wardrobe module: the fit ledger and size advice. Uses the same Postgres database, schema "wardrobe".</summary>
    public static IServiceCollection AddWardrobeModule(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<WardrobeDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WardrobeDbContext.Schema))
            .UseSnakeCaseNamingConvention());
        return services.AddWardrobeServices();
    }

    /// <summary>Services without the database registration, so tests can plug in their own provider.</summary>
    internal static IServiceCollection AddWardrobeServices(this IServiceCollection services)
    {
        services.AddScoped<FitLedger>();
        services.AddScoped<FitAdvisor>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
