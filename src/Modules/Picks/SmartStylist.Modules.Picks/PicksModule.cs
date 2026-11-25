using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartStylist.Modules.Picks.Persistence;
using SmartStylist.Modules.Picks.Telegram;

namespace SmartStylist.Modules.Picks;

public static class PicksModule
{
    /// <summary>
    /// Registers the Picks module: smart picks per person, the Telegram bot and the buy-now outcome check. Needs the
    /// Catalog and Wardrobe modules. Same Postgres database, schema "picks".
    /// </summary>
    public static IServiceCollection AddPicksModule(this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<PicksDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PicksDbContext.Schema))
            .UseSnakeCaseNamingConvention());
        services.AddOptions<PicksOptions>().Bind(configuration.GetSection(PicksOptions.SectionName));
        return services.AddPicksServices();
    }

    /// <summary>Services without the database registration, so tests can plug in their own provider.</summary>
    internal static IServiceCollection AddPicksServices(this IServiceCollection services)
    {
        services.AddHttpClient<TelegramBot>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<DailyPicks>();
        services.AddScoped<PickOutcomes>();
        services.AddScoped<DailyPicksJob>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
