using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Lst.Modules.Operations.Domain;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Modules.Operations.Application;

public sealed class SlaMonitorOptions
{
    public const string SectionName = "Workers:SlaMonitor";

    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Hareketsizlik eşiği, iş günü saatleriyle (Pazar ve resmî tatiller sayılmaz).</summary>
    public int NoMovementBusinessHours { get; set; } = 48;

    /// <summary>Bu kadar gün gecikmede DeliveryOverdue önemi High olur.</summary>
    public int OverdueHighAfterDays { get; set; } = 2;

    public int BatchSize { get; set; } = 500;
}

/// <summary>SLA iş takvimi hesapları (Europe/Istanbul, Pazar + resmî tatiller iş günü değildir; docs/01 §4.4).</summary>
public static class SlaCalendar
{
    /// <summary>[from, to) aralığında iş günlerine düşen saat (yerel takvim). Ters aralıkta 0.</summary>
    public static double BusinessHoursBetween(SlaPolicy policy, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (to <= from)
        {
            return 0;
        }

        // Çok eski hareket: eşik zaten aşılmıştır; sınırsız gün döngüsünden kaçın.
        if (to - from > TimeSpan.FromDays(60))
        {
            return (to - from).TotalHours;
        }

        var tz = policy.TimeZone;
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, tz).DateTime);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, tz).DateTime);
        double hours = 0;
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (!policy.IsBusinessDay(day))
            {
                continue;
            }

            var start = LocalMidnight(day, tz);
            var end = LocalMidnight(day.AddDays(1), tz);
            var overlapStart = start > from ? start : from;
            var overlapEnd = end < to ? end : to;
            if (overlapEnd > overlapStart)
            {
                hours += (overlapEnd - overlapStart).TotalHours;
            }
        }

        return hours;
    }

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }
}

public sealed record SlaRunResult(int Evaluated, int Opened, int Escalated, int Resolved);

/// <summary>
/// Gecikme (DeliveryOverdue) ve hareketsizlik (NoMovement) istisnalarını açar, yükseltir ve kapatır (docs/02 §4.1, docs/03 §6.4).
/// Tek çalıştırıcı: PostgreSQL advisory lock; ikinci instance turu atlar. Gönderi başına küçük transaction: gönderi kilidi → istisna
/// (ADR-015 kilit sırası), koşul kilit altında yeniden değerlendirilir; tekrar çalıştırma değişiklik üretmez (idempotent).
/// </summary>
public sealed partial class SlaMonitor(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    PromisedDeliveryCalculator calendar,
    IOptions<SlaMonitorOptions> options,
    ILogger<SlaMonitor> logger)
{
    public const long AdvisoryLockKey = 0x4C53_5401;
    public const string Actor = "system:sla-monitor";

    /// <summary>Bir tur. Başka instance tur yürütüyorsa null. <paramref name="onlyShipments"/> testler ve operasyon aracı içindir.</summary>
    public async Task<SlaRunResult?> RunOnceAsync(IReadOnlyCollection<Guid>? onlyShipments, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (!await ScalarAsync<bool>(connection, "SELECT pg_try_advisory_lock(@key)", cancellationToken, Key()))
        {
            LogSkipped(logger);
            return null;
        }

        try
        {
            var now = timeProvider.GetUtcNowMicroseconds();
            var today = calendar.TodayLocal(now);
            var candidates = await CandidatesAsync(connection, now, today, onlyShipments, cancellationToken);
            int opened = 0, escalated = 0, resolved = 0;
            foreach (var shipmentId in candidates)
            {
                var (o, e, r) = await EvaluateAsync(shipmentId, now, today, cancellationToken);
                opened += o;
                escalated += e;
                resolved += r;
            }

            return new SlaRunResult(candidates.Count, opened, escalated, resolved);
        }
        finally
        {
            await ScalarAsync<bool>(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None, Key());
        }
    }

    private async Task<List<Guid>> CandidatesAsync(NpgsqlConnection connection, DateTimeOffset now, DateOnly today, IReadOnlyCollection<Guid>? only, CancellationToken cancellationToken)
    {
        // Duvar saatiyle 48 saat, iş saatiyle 48 saatin gerekli koşuludur; kesin karar gönderi kilidi altında C#'ta verilir.
        await using var command = new NpgsqlCommand(
            """
            SELECT id FROM (
                SELECT s.id FROM shipments.shipments s
                 WHERE s.status NOT IN ('Delivered','ReturnedToSender','Cancelled')
                   AND (@only::uuid[] IS NULL OR s.id = ANY(@only))
                   AND ((s.status <> 'ReturningToSender' AND s.promised_delivery_date < @today)
                        OR COALESCE((SELECT max(e.occurred_at) FROM shipments.tracking_events e
                                      WHERE e.shipment_id = s.id AND e.outcome = 'Applied'), s.created_at) < @movement_cutoff)
                UNION
                SELECT x.shipment_id FROM operations.shipment_exceptions x
                 WHERE x.status IN ('Open','Assigned') AND x.type IN ('DeliveryOverdue','NoMovement')
                   AND (@only::uuid[] IS NULL OR x.shipment_id = ANY(@only))
            ) c
            ORDER BY id
            LIMIT @batch
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("only", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = only is null ? DBNull.Value : only.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = today });
        command.Parameters.Add(new NpgsqlParameter("movement_cutoff", NpgsqlDbType.TimestampTz) { Value = now.AddHours(-options.Value.NoMovementBusinessHours) });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = options.Value.BatchSize });
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private async Task<(int Opened, int Escalated, int Resolved)> EvaluateAsync(Guid shipmentId, DateTimeOffset now, DateOnly today, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);
        var locked = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM shipments.shipments WHERE id = {shipmentId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (0, 0, 0);
        }

        var shipment = await db.Set<Shipment>().AsNoTracking().FirstAsync(s => s.Id == shipmentId, cancellationToken);
        var lastMovement = await db.Set<TrackingEvent>().AsNoTracking()
            .Where(e => e.ShipmentId == shipmentId && e.Outcome == TimelineOutcome.Applied)
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, cancellationToken) ?? shipment.CreatedAt;
        var open = await db.Set<ShipmentException>()
            .Where(x => x.ShipmentId == shipmentId && x.Status != ExceptionStatus.Resolved
                && (x.Type == ExceptionType.DeliveryOverdue || x.Type == ExceptionType.NoMovement))
            .ToListAsync(cancellationToken);

        int opened = 0, escalated = 0, resolved = 0;
        var terminal = shipment.Status.IsTerminal();

        var overdueDays = today.DayNumber - shipment.PromisedDeliveryDate.DayNumber;
        var overdue = !terminal && shipment.Status != ShipmentStatus.ReturningToSender && overdueDays > 0;
        var overdueSeverity = overdueDays >= options.Value.OverdueHighAfterDays ? ExceptionSeverity.High : ExceptionSeverity.Medium;
        Apply(ExceptionType.DeliveryOverdue, overdue, overdueSeverity, shipment.DestinationHubId, new { promisedDeliveryDate = shipment.PromisedDeliveryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), overdueDays });

        var idleHours = SlaCalendar.BusinessHoursBetween(calendar.Policy, lastMovement, now);
        var noMovement = !terminal && idleHours >= options.Value.NoMovementBusinessHours;
        Apply(ExceptionType.NoMovement, noMovement, ExceptionSeverity.Medium, shipment.CurrentHubId ?? shipment.OriginHubId, new { businessHoursIdle = Math.Round(idleHours, 1) });

        if (opened + escalated + resolved == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (0, 0, 0);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return (opened, escalated, resolved);

        void Apply(ExceptionType type, bool active, ExceptionSeverity severity, Guid? hubId, object details)
        {
            var existing = open.FirstOrDefault(x => x.Type == type);
            if (active && existing is null)
            {
                var exception = ShipmentException.Open(shipmentId, type, severity, hubId, "SlaMonitor", now);
                db.Add(exception);
                Audit(OperationsAuditActions.ExceptionOpened, exception, new { type = type.ToString(), severity = severity.ToString(), details });
                opened++;
            }
            else if (active && existing!.Escalate(severity, now))
            {
                Audit(OperationsAuditActions.ExceptionEscalated, existing, new { type = type.ToString(), severity = severity.ToString(), details });
                escalated++;
            }
            else if (!active && existing is not null)
            {
                existing.AutoResolve(now);
                Audit(OperationsAuditActions.ExceptionAutoResolved, existing, new { type = type.ToString(), reason = "condition-cleared" });
                resolved++;
            }
        }

        void Audit(string action, ShipmentException exception, object changes) =>
            db.Add(AuditEntry.SystemSucceeded(now, Actor, action, "ShipmentException", exception.Id.ToString(), Activity.Current?.TraceId.ToString(),
                JsonSerializer.Serialize(changes)));
    }

    private static NpgsqlParameter Key() => new("key", NpgsqlDbType.Bigint) { Value = AdvisoryLockKey };

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
#pragma warning disable CA2100 // SQL sabittir; değerler parametreyle verilir.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "SLA monitor run skipped: another instance holds the advisory lock")]
    private static partial void LogSkipped(ILogger logger);
}

internal sealed partial class SlaMonitorWorker(
    SlaMonitor monitor,
    IConfiguration configuration,
    IOptions<SlaMonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<SlaMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Workers:Enabled", true))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await monitor.RunOnceAsync(null, stoppingToken);
                if (result is { } r && r.Opened + r.Escalated + r.Resolved > 0)
                {
                    LogRun(logger, r.Evaluated, r.Opened, r.Escalated, r.Resolved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Worker döngüsü hiçbir hatada durmaz; bir sonraki turda yeniden dener.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex.GetType().Name, TransientFailures.IsTransient(ex));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.IntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SLA monitor evaluated {Evaluated}: opened {Opened}, escalated {Escalated}, resolved {Resolved}")]
    private static partial void LogRun(ILogger logger, int evaluated, int opened, int escalated, int resolved);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SLA monitor run failed ({ErrorType}, transient={Transient})")]
    private static partial void LogFailed(ILogger logger, string errorType, bool transient);
}
