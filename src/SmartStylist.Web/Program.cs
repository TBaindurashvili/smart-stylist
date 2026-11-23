using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hangfire.PostgreSql;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using SmartStylist.Modules.Catalog;
using SmartStylist.Modules.Wardrobe;
using SmartStylist.Modules.Catalog.Collection;
using SmartStylist.Modules.Catalog.Reporting;
using SmartStylist.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCatalogModule(builder.Configuration);
builder.Services.AddWardrobeModule(CatalogModule.GetConnectionString(builder.Configuration));
builder.Services.Configure<JsonOptions>(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The web host only reads Hangfire state for the dashboard; jobs run in SmartStylist.Worker.
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(
        storage => storage.UseNpgsqlConnection(CatalogModule.GetConnectionString(builder.Configuration)),
        new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = true }));

var app = builder.Build();

// The fit ledger lives in its own schema; create it here too so the page works before the worker has run.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<SmartStylist.Modules.Wardrobe.Persistence.WardrobeDbContext>().Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

// Default dashboard authorization allows local requests only.
app.MapHangfireDashboard("/hangfire");

// Called by the browser extension. No CORS policy on purpose: only the extension's host permission lets a page reach this.
app.MapPost("/api/captures", (PageCapture capture, PageCaptureService service, CancellationToken cancellationToken) =>
    service.CaptureAsync(capture, cancellationToken));

// Price-history API for the stylist layer.
app.MapGet("/api/products/{id:long}/history", async (long id, string? colour, string? size, PriceHistoryService history, TimeProvider time, CancellationToken cancellationToken) =>
    await history.GetHistoryAsync(id, colour, size, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), cancellationToken) is { } result
        ? Results.Ok(result)
        : Results.NotFound());
app.MapGet("/api/drops", async (DateOnly? since, string? sizes, PriceHistoryService history, TimeProvider time, CancellationToken cancellationToken) =>
    await history.GetDropsAsync(
        since ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1),
        sizes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        cancellationToken));

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
