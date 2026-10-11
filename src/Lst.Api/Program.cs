using Lst.Api;
using Lst.Api.Hosting;
using Lst.Api.Realtime;
using Lst.Modules.Identity;
using Lst.Modules.Operations;
using Lst.Modules.Shipments;
using Lst.Modules.Shipments.Endpoints;
using Lst.Modules.Tracking;
using Lst.Platform.Health;
using Lst.Platform.Http;
using Lst.Platform.Outbox;
using Lst.Platform.Retention;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

if (MaintenanceCli.IsMaintenanceCommand(args))
{
    return await MaintenanceCli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
HostConfiguration.AddSecretFiles(builder.Configuration);

// JSON console log; HTTP gövde/header loglama ve EF sensitive data logging kapalıdır.
builder.Logging.ClearProviders();
// İstek satırı (yol) loglanmaz: public takip kodu URL'dedir ve log'a girmemelidir (docs/06 §5).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.StaticFiles", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.HttpsPolicy", LogLevel.Warning);
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = false;
    options.UseUtcTimestamp = true;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);
HostConfiguration.AddLstDataProtection(builder.Services, builder.Configuration);
builder.Services.AddLstPersistence(builder.Configuration);
builder.Services.AddLstOutbox(builder.Configuration);
builder.Services.AddIdentityModule();
builder.Services.AddShipmentsModule(builder.Configuration);
builder.Services.AddOperationsModule();
builder.Services.AddTrackingModule(builder.Configuration, builder.Environment);
builder.Services.AddLstRateLimiting(builder.Configuration);
builder.Services.AddLstRetention(builder.Configuration);
builder.Services.AddLstRealtime(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadyHealthCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();

// Operasyon paneli (React SPA) aynı origin'den sunulur; statik çıktı yoksa yalnız API çalışır.
var spaFiles = SpaHosting.ResolveFiles(app.Environment, app.Configuration);

// Yalnız ReverseProxy altında listelenen proxy'lerden gelen X-Forwarded-For istemci IP'si olarak kabul edilir.
app.UseLstForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();
if (spaFiles is not null)
{
    app.UseLstSpaStaticFiles(spaFiles);
}

app.UsePublicResponseHeaders();
app.UseRealtimeOriginCheck();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapAuthEndpoints();
app.MapShipmentEndpoints();
app.MapOperationsEndpoints();
app.MapTrackingEndpoints(app.Configuration, app.Environment);
app.MapLstRealtime();
if (spaFiles is not null)
{
    app.MapLstSpaFallback(spaFiles);
}

await DevelopmentStartup.RunAsync(app);

await app.RunAsync();
return 0;

/// <summary>WebApplicationFactory için.</summary>
public partial class Program;
