using Microsoft.Extensions.Configuration;

namespace SmartStylist.Modules.Catalog.Tests;

public class ConnectionStringTests
{
    private const string Json = "Host=localhost;Port=5432;Database=smart_stylist;Username=stylist;Password=stylist";

    private static IConfiguration Config(string? port) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = Json,
            ["POSTGRES_PORT"] = port,
        })
        .Build();

    [Fact]
    public void Postgres_port_variable_overrides_the_port()
    {
        var connectionString = CatalogModule.GetConnectionString(Config("5433"));

        Assert.Contains("Port=5433", connectionString);
        Assert.Contains("Database=smart_stylist", connectionString);
    }

    [Fact]
    public void Without_the_variable_the_configured_string_is_used()
    {
        Assert.Equal(Json, CatalogModule.GetConnectionString(Config(null)));
    }
}
