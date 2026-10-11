using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lst.Api;

internal static class PersistenceRegistration
{
    public const string ConnectionStringName = "Lst";

    public static IServiceCollection AddLstPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<LogisticsDbContext>((sp, options) =>
        {
            // Bağlantı dizesi user-secrets / ortam değişkeninden gelir; değeri hiçbir yere yazılmaz.
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ConnectionStringName} tanımlı değil. Development için 'dotnet user-secrets set' kullanın.");
            }

            Configure(options, connectionString);
            // Modül interceptor'ları (ör. outbox) aynı SaveChanges/transaction içinde çalışır.
            options.AddInterceptors(sp.GetServices<IInterceptor>());
        });
        return services;
    }

    /// <summary>Uygulama, design-time factory ve test fixture'ı aynı ayarları kullanır.</summary>
    public static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        options
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsAssembly(typeof(LogisticsDbContext).Assembly.GetName().Name)
                .MigrationsHistoryTable(Schemas.MigrationsHistoryTable, Schemas.Platform))
            .UseSnakeCaseNamingConvention()
            .EnableSensitiveDataLogging(false);
    }
}
