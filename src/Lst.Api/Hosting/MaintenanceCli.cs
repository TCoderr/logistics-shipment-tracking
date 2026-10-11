using Lst.Modules.Identity;
using Lst.Modules.Operations;
using Lst.Modules.Shipments;
using Lst.Modules.Tracking;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Api.Hosting;

/// <summary>
/// Web sunucusu başlatmadan çalışan tek seferlik komutlar (aynı image, farklı komut):
/// <list type="bullet">
/// <item><c>--migrate</c>: sahip rol bağlantısıyla (ConnectionStrings:LstMigrator) migration + çalışma zamanı rol yetkileri.
/// Demo verisi yalnız açık opt-in ile (Seed:Enabled=true) ve yalnız Development ortamında eklenir; aksi halde komut 3 ile reddeder.</item>
/// <item><c>--probe &lt;url&gt;</c>: konteyner health check'i; yalnız localhost için sertifika doğrulaması atlanır.</item>
/// </list>
/// Üretim API başlangıcı migration yapmaz; bu komut ayrı ve kontrollü adımdır.
/// </summary>
internal static partial class MaintenanceCli
{
    public const string MigrateFlag = "--migrate";
    public const string ProbeFlag = "--probe";

    public static bool IsMaintenanceCommand(string[] args) =>
        args.Contains(MigrateFlag, StringComparer.Ordinal) || args.Contains(ProbeFlag, StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args)
    {
        var probeIndex = Array.IndexOf(args, ProbeFlag);
        if (probeIndex >= 0)
        {
            return probeIndex + 1 < args.Length ? await ProbeAsync(args[probeIndex + 1]) : 2;
        }

        return await MigrateAsync(args);
    }

    private static async Task<int> ProbeAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
        {
            return 2;
        }

        using var handler = new HttpClientHandler
        {
            // Konteyner içi kendi kendine sorgu: sertifika localhost'a ait değilse bile yalnız loopback'te kabul edilir.
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(uri);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }

    private static async Task<int> MigrateAsync(string[] args)
    {
        // Host builder ortam adını DOTNET_ENVIRONMENT'tan okur; aynı image'ın web modu ASPNETCORE_ENVIRONMENT kullandığı için ikisi de desteklenir.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args.Where(a => a != MigrateFlag).ToArray(),
            EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
        });
        HostConfiguration.AddSecretFiles(builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o => o.UseUtcTimestamp = true);

        var configuration = builder.Configuration;
        var ownerConnection = configuration.GetConnectionString("LstMigrator");
        var runtimeRole = configuration["Database:RuntimeRole"] ?? "lst_app";
        if (string.IsNullOrWhiteSpace(ownerConnection))
        {
            await Console.Error.WriteLineAsync("ConnectionStrings:LstMigrator tanımlı değil (sahip rol bağlantısı).");
            return 2;
        }

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<LogisticsDbContext>(o => PersistenceRegistration.Configure(o, ownerConnection));
        // Identity modülünün yetkilendirme servisleri routing kayıtlarına bağlıdır (Development'ta DI derleme anında doğrulanır).
        builder.Services.AddRouting();
        builder.Services.AddIdentityModule();
        builder.Services.AddShipmentsModule(configuration);
        builder.Services.AddOperationsModule();
        builder.Services.AddTrackingPersistence();
        HostConfiguration.AddLstDataProtection(builder.Services, configuration);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Lst.Maintenance");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();

        await DatabaseMaintenance.MigrateAndGrantAsync(db, runtimeRole, CancellationToken.None);
        LogMigrated(logger, runtimeRole);

        if (configuration.GetValue<bool>("Seed:Enabled"))
        {
            if (!builder.Environment.IsDevelopment())
            {
                LogSeedRefused(logger, builder.Environment.EnvironmentName);
                return 3;
            }

            await DemoSeeder.SeedAsync(scope.ServiceProvider, configuration, logger, CancellationToken.None);
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations applied and runtime grants refreshed for role {RuntimeRole}")]
    private static partial void LogMigrated(ILogger logger, string runtimeRole);

    [LoggerMessage(Level = LogLevel.Error, Message = "Demo seed refused: Seed:Enabled=true is only allowed in Development (environment: {Environment})")]
    private static partial void LogSeedRefused(ILogger logger, string environment);
}

internal static class DatabaseMaintenance
{
    /// <summary>Sahip rol ile: bekleyen migration'lar, ardından çalışma zamanı rol yetkileri (idempotent).</summary>
    public static async Task MigrateAndGrantAsync(LogisticsDbContext ownerDb, string runtimeRole, CancellationToken cancellationToken)
    {
        await ownerDb.Database.MigrateAsync(cancellationToken);
        await ownerDb.Database.ExecuteSqlRawAsync(RuntimeGrants.Sql(runtimeRole), cancellationToken);
    }
}
