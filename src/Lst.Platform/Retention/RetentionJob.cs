using System.Diagnostics;
using System.Text.Json;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Platform.Retention;

/// <summary>Saklama süreleri (docs/04 §6). Tüm görevler idempotenttir: ikinci çalıştırma değişiklik yapmaz.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Terminal durumdan sonra kişisel verinin maskelenmesi (varsayım A2).</summary>
    public int PiiDays { get; set; } = 180;

    /// <summary>Ham webhook gövdesi ve normalize payload (varsayım A3); makbuz satırı silinmez (tombstone).</summary>
    public int RawPayloadDays { get; set; } = 30;

    public int ProcessedOutboxDays { get; set; } = 7;

    public int BatchSize { get; set; } = 1000;

    /// <summary>Günlük çalışma saati (yerel, HH:mm).</summary>
    public string RunAtLocalTime { get; set; } = "03:00";

    public string TimeZoneId { get; set; } = "Europe/Istanbul";
}

public sealed record RetentionContext(DateTimeOffset Now, RetentionOptions Options);

/// <summary>Modül saklama görevi. Kendi transaction'larını partiler halinde yönetir; etkilenen satır sayısını döner.</summary>
public interface IRetentionTask
{
    string Name { get; }

    int Order { get; }

    Task<int> RunAsync(LogisticsDbContext db, RetentionContext context, CancellationToken cancellationToken);
}

public sealed record RetentionRunResult(IReadOnlyDictionary<string, int> Affected)
{
    public int Total => Affected.Values.Sum();
}

/// <summary>
/// Saklama işi: advisory lock ile tek çalıştırıcı; görevler sırayla (PII maskeleme → not temizliği → ham payload → outbox → idempotency).
/// Sonuç (yalnız sayılar) audit'e yazılır.
/// </summary>
public sealed partial class RetentionJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<RetentionOptions> options,
    ILogger<RetentionJob> logger)
{
    public const long AdvisoryLockKey = 0x4C53_5402;

    /// <summary>Başka instance çalışıyorsa null. <paramref name="actorUserId"/> elle tetiklemede audit aktörüdür.</summary>
    public async Task<RetentionRunResult?> RunOnceAsync(Guid? actorUserId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (!await AdvisoryAsync(connection, "SELECT pg_try_advisory_lock(@key)", cancellationToken))
        {
            return null;
        }

        try
        {
            var now = timeProvider.GetUtcNowMicroseconds();
            var context = new RetentionContext(now, options.Value);
            var affected = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var task in scope.ServiceProvider.GetServices<IRetentionTask>().OrderBy(t => t.Order))
            {
                affected[task.Name] = await task.RunAsync(db, context, cancellationToken);
            }

            var result = new RetentionRunResult(affected);
            db.Add(actorUserId is { } userId
                ? AuditEntry.UserSucceeded(now, userId, RetentionAuditActions.RetentionRun, "Retention", "daily", Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(affected))
                : AuditEntry.SystemSucceeded(now, "system:retention", RetentionAuditActions.RetentionRun, "Retention", "daily", Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(affected)));
            await db.SaveChangesAsync(cancellationToken);
            LogCompleted(logger, result.Total);
            return result;
        }
        finally
        {
            await AdvisoryAsync(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None);
        }
    }

    private static async Task<bool> AdvisoryAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // SQL sabittir.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bigint) { Value = AdvisoryLockKey });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Verilen SQL'i (LIMIT @batch içeren DELETE/UPDATE) etkilenen satır kalmayana kadar parti parti çalıştırır; her parti kendi transaction'ıdır.</summary>
    public static async Task<int> RepeatBatchesAsync(
        LogisticsDbContext db, string sql, RetentionContext context, CancellationToken cancellationToken, params (string Name, NpgsqlDbType Type, object Value)[] parameters)
    {
        var total = 0;
        while (true)
        {
            await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = context.Now });
            command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = context.Options.BatchSize });
            foreach (var (name, type, value) in parameters)
            {
                command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            }

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            total += affected;
            if (affected < context.Options.BatchSize)
            {
                return total;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention run completed; {Total} rows affected")]
    private static partial void LogCompleted(ILogger logger, int total);
}

public static class RetentionAuditActions
{
    public const string RetentionRun = "RetentionRun";
}

/// <summary>İşlenmiş outbox mesajları (7 gün). Dead-letter'lar elle temizlenene kadar kalır.</summary>
internal sealed class OutboxRetentionTask : IRetentionTask
{
    public string Name => "outbox-processed";

    public int Order => 40;

    public Task<int> RunAsync(LogisticsDbContext db, RetentionContext context, CancellationToken cancellationToken) =>
        RetentionJob.RepeatBatchesAsync(db,
            """
            DELETE FROM platform.outbox_messages
             WHERE id IN (SELECT id FROM platform.outbox_messages
                           WHERE processed_at IS NOT NULL AND processed_at < @cutoff
                           ORDER BY id LIMIT @batch)
            """,
            context, cancellationToken, ("cutoff", NpgsqlDbType.TimestampTz, context.Now.AddDays(-context.Options.ProcessedOutboxDays)));
}

/// <summary>
/// Süresi dolmuş idempotency kayıtları (48 saat penceresi; kayıt zaten <c>expires_at</c> sonrası yeniden kullanılabilir).
/// Silme sonrası aynı anahtar yeni istek sayılır — tekrar ayıklama ufku kaydın ömrüyle aynıdır.
/// </summary>
internal sealed class IdempotencyRetentionTask : IRetentionTask
{
    public string Name => "idempotency-expired";

    public int Order => 50;

    public Task<int> RunAsync(LogisticsDbContext db, RetentionContext context, CancellationToken cancellationToken) =>
        RetentionJob.RepeatBatchesAsync(db,
            """
            DELETE FROM platform.idempotency_records
             WHERE id IN (SELECT id FROM platform.idempotency_records WHERE expires_at < @now ORDER BY expires_at LIMIT @batch)
            """,
            context, cancellationToken);
}

internal sealed partial class RetentionWorker(
    RetentionJob job,
    IConfiguration configuration,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Workers:Enabled", true))
        {
            return;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZoneId);
        var runAt = TimeOnly.ParseExact(options.Value.RunAtLocalTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = NextRun(timeProvider.GetUtcNow(), zone, runAt) - timeProvider.GetUtcNow();
            try
            {
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, timeProvider, stoppingToken);
                await job.RunOnceAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Ertesi gün yeniden denenir.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex.GetType().Name);
            }
        }
    }

    public static DateTimeOffset NextRun(DateTimeOffset nowUtc, TimeZoneInfo zone, TimeOnly runAt)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var candidateLocal = DateOnly.FromDateTime(local.DateTime).ToDateTime(runAt);
        if (candidateLocal <= local.DateTime)
        {
            candidateLocal = candidateLocal.AddDays(1);
        }

        return new DateTimeOffset(candidateLocal, zone.GetUtcOffset(candidateLocal));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention run failed ({ErrorType}); retrying at next schedule")]
    private static partial void LogFailed(ILogger logger, string errorType);
}

public static class RetentionRegistration
{
    public static IServiceCollection AddLstRetention(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .Validate(o => o.PiiDays >= 1 && o.RawPayloadDays >= 1 && o.ProcessedOutboxDays >= 1 && o.BatchSize is > 0 and <= 10_000
                && TimeOnly.TryParseExact(o.RunAtLocalTime, "HH:mm", out _), "Retention ayarları geçersiz.")
            .ValidateOnStart();
        services.AddSingleton<RetentionJob>();
        services.AddScoped<IRetentionTask, OutboxRetentionTask>();
        services.AddScoped<IRetentionTask, IdempotencyRetentionTask>();
        services.AddHostedService<RetentionWorker>();
        return services;
    }
}
