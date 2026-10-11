using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Lst.Modules.Identity;
using Lst.Modules.Tracking.Application;
using Lst.Modules.Tracking.Endpoints;
using Lst.Modules.Tracking.Infrastructure;
using Lst.Platform.Http;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lst.Modules.Tracking;

public static partial class TrackingModule
{
    public static IServiceCollection AddTrackingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        services.AddTrackingPersistence();

        services.AddOptions<CarrierOptions>()
            .Bind(configuration.GetSection(CarrierOptions.SectionName))
            .Validate(o => o.Secrets.Values.All(s => s.Values.Count <= 2 && s.Values.All(v => v.Length >= 32)),
                "Carrier secret'ları en az 32 karakter olmalı; carrier başına en fazla iki aktif secret.")
            .Validate(o => o.RateLimitPermitLimit is > 0 and <= 100_000 && o.RateLimitWindowSeconds is > 0 and <= 3600 && o.SignatureToleranceSeconds is >= 30 and <= 900, "Carriers limitleri geçersiz.")
            .ValidateOnStart();
        services.AddOptions<CarrierInboxOptions>()
            .Bind(configuration.GetSection(CarrierInboxOptions.SectionName))
            .Validate(o => o.BatchSize is > 0 and <= 500 && o.LeaseSeconds is >= 5 and <= 600 && o.MaxAttempts is > 0 and <= 50 && o.PollMilliseconds >= 50,
                "Workers:CarrierInbox ayarları geçersiz.")
            .ValidateOnStart();
        services.AddOptions<SimulationOptions>()
            .Bind(configuration.GetSection(SimulationOptions.SectionName))
            .Validate(o => !o.Enabled || environment.IsDevelopment(), "Simulation:Enabled yalnız Development ortamında true olabilir.")
            .Validate(o => !o.Enabled || (o.BaseUrl.IsAbsoluteUri && o.BaseUrl.IsLoopback && o.BaseUrl.Scheme == Uri.UriSchemeHttps),
                "Simulation:BaseUrl HTTPS loopback adresi olmalı.")
            .ValidateOnStart();

        services.AddScoped<CarrierReceiptWriter>();
        services.AddScoped<Lst.Platform.Retention.IRetentionTask, RawPayloadRetentionTask>();
        services.AddSingleton<CarrierInboxProcessor>();
        // Bayrak çalışma anında okunur (Workers:Enabled=false iken worker hiçbir şey claim etmez).
        services.AddHostedService<CarrierInboxWorker>();

        if (IsSimulationEnabled(configuration, environment))
        {
            services.AddScoped<CarrierSimulator>();
            services.AddHttpClient<ICarrierWebhookTransport, HttpCarrierWebhookTransport>((sp, client) =>
                {
                    client.BaseAddress = sp.GetRequiredService<IOptions<SimulationOptions>>().Value.BaseUrl;
                    client.Timeout = TimeSpan.FromSeconds(10);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    // Yalnız Development: kendi loopback adresine giden istekte yerel geliştirme sertifikası (CN=localhost) kabul edilir.
                    ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                        errors == System.Net.Security.SslPolicyErrors.None
                        || (request.RequestUri is { IsLoopback: true } && certificate is not null
                            && certificate.Subject.Equals("CN=localhost", StringComparison.Ordinal)),
                });
        }

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(TrackingEndpoints.WebhookRateLimitPolicy, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<CarrierOptions>>().Value;
                var code = httpContext.Request.RouteValues["carrierCode"] as string;
                // Bölüm anahtarı sınırlıdır: geçersiz biçimli kodlar tek bir ortak bölümü paylaşır.
                var partition = code is not null && CarrierCodeRegex().IsMatch(code) ? code : "~invalid";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.RateLimitPermitLimit,
                    Window = TimeSpan.FromSeconds(limits.RateLimitWindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });

        return services;
    }

    /// <summary>Yalnız EF model katkısı (design-time, migrator, test hazırlığı).</summary>
    public static IServiceCollection AddTrackingPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, TrackingModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapTrackingEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration, IHostEnvironment environment)
    {
        TrackingEndpoints.Map(app);
        if (IsSimulationEnabled(configuration, environment))
        {
            // Keşif ucu: arayüz demo simülatör panelini yalnız bu route varsa (Development + bayrak) ve kullanıcı OpsManager ise gösterir.
            app.MapGet("/api/dev/carrier-simulator", (HttpContext httpContext) =>
                {
                    httpContext.Response.Headers.CacheControl = "no-store";
                    return TypedResults.Ok(new SimulatorInfoResponse(CarrierSimulator.CarrierCode, Enum.GetNames<SimulationScenario>()));
                })
                .RequireAuthorization(Policies.OpsManagerOnly)
                .WithTags("Development")
                .WithName("GetCarrierSimulator")
                .Produces<SimulatorInfoResponse>();

            app.MapPost("/api/dev/carrier-simulator/runs", RunSimulationAsync)
                .RequireAuthorization(Policies.OpsManagerOnly)
                .RequireCsrf()
                .WithTags("Development")
                .WithName("RunCarrierSimulation")
                .Accepts<SimulationRunRequest>("application/json")
                .Produces<SimulationRunResponse>()
                .ProducesValidationProblem();
        }

        return app;
    }

    public static bool IsSimulationEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevelopment() && configuration.GetValue<bool>("Simulation:Enabled");

    private static async Task<IResult> RunSimulationAsync(SimulationRunRequest request, CarrierSimulator simulator, HttpContext httpContext)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.TrackingNumber is null || !Shipments.Domain.TrackingNumbers.IsValid(request.TrackingNumber))
        {
            errors["trackingNumber"] = ["Geçerli LS tracking numarası olmalı."];
        }

        if (!Enum.TryParse<SimulationScenario>(request.Scenario, ignoreCase: false, out var scenario) || !Enum.IsDefined(scenario)
            || int.TryParse(request.Scenario, out _))
        {
            errors["scenario"] = ["Desteklenen senaryolar: " + string.Join(", ", Enum.GetNames<SimulationScenario>()) + "."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(await simulator.RunAsync(request.TrackingNumber!, scenario, httpContext.RequestAborted));
    }

    [GeneratedRegex("^[A-Z0-9]{2,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex CarrierCodeRegex();
}

/// <summary>Carrier inbox worker'ı (Workers:Enabled). DB kesintisinde bekler; deneme bütçesi tüketilmez.</summary>
internal sealed partial class CarrierInboxWorker(
    CarrierInboxProcessor processor,
    IConfiguration configuration,
    IOptions<CarrierInboxOptions> options,
    TimeProvider timeProvider,
    ILogger<CarrierInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Workers:Enabled", true))
        {
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var processed = await processor.RunOnceAsync(stoppingToken);
                failures = 0;
                delay = processed > 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(options.Value.PollMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Worker döngüsü hiçbir hatada durmaz; transient olmayanlar da loglanıp beklenir.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures++;
                LogClaimFailed(logger, ex.GetType().Name, CarrierInboxProcessor.IsTransient(ex));
                delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 6))));
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Carrier inbox poll failed ({ErrorType}, transient={Transient}); backing off")]
    private static partial void LogClaimFailed(ILogger logger, string errorType, bool transient);
}

/// <summary>Simülatör keşif yanıtı (yalnız Development + Simulation:Enabled).</summary>
public sealed record SimulatorInfoResponse(string CarrierCode, IReadOnlyList<string> Scenarios);
