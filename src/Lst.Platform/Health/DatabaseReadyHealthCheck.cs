using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lst.Platform.Health;

/// <summary>/health/ready: DB'ye bağlanılabiliyor ve bekleyen migration yok.</summary>
public sealed class DatabaseReadyHealthCheck(LogisticsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("database-unreachable");
            }

            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            return pending.Any()
                ? HealthCheckResult.Unhealthy("pending-migrations")
                : HealthCheckResult.Healthy();
        }
#pragma warning disable CA1031 // Health check hiçbir istisnayı dışarı sızdırmaz; ayrıntı yanıta yazılmaz.
        catch (Exception)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("database-check-failed");
        }
    }
}
