using System.Globalization;
using System.Text.Json;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Identity;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Modules.Operations.Application;

public sealed record Ratio(int Numerator, int Denominator, decimal? Rate);

public sealed record DueToday(int Total, int Delivered);

public sealed record OpenExceptionStats(int Count, int Overdue, long? OldestAgeSeconds);

public sealed record CountByKey(string Key, int Count);

/// <summary>UC-10 KPI satırı (docs/07 §6). Tüm sayılar o anki DB toplamlarıdır; oran payda 0 ise null (UI payda &lt; 20 ise oran yerine "pay / payda" gösterir).</summary>
public sealed record OperationsSummary(
    DateTime AsOf,
    string Date,
    string TimeZone,
    string? HubCode,
    DueToday DueToday,
    int DelayedOpen,
    Ratio OnTimeDelivery7Days,
    Ratio FirstAttemptDelivery7Days,
    OpenExceptionStats OpenExceptions,
    IReadOnlyList<CountByKey> FailedAttemptReasonsToday,
    IReadOnlyList<CountByKey> CarrierOutcomes24Hours);

public abstract record SummaryOutcome
{
    private SummaryOutcome()
    {
    }

    public sealed record Ok(OperationsSummary Summary) : SummaryOutcome;

    public sealed record UnknownHub : SummaryOutcome;
}

public sealed class OperationsSummaryHandler(LogisticsDbContext db, PromisedDeliveryCalculator calendar, IOptions<SlaOptions> sla, TimeProvider timeProvider)
{
    private const string HubFilter = "(@hub::uuid IS NULL OR s.origin_hub_id = @hub OR s.destination_hub_id = @hub)";

    public async Task<SummaryOutcome> HandleAsync(string? hubCode, DateOnly? date, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNowMicroseconds();
        var day = date ?? calendar.TodayLocal(now);
        Guid? hubId = null;
        if (hubCode is not null)
        {
            hubId = await db.Set<Hub>().AsNoTracking().Where(h => h.Code == hubCode).Select(h => (Guid?)h.Id).FirstOrDefaultAsync(cancellationToken);
            if (hubId is null)
            {
                return new SummaryOutcome.UnknownHub();
            }
        }

        var timeZone = sla.Value.TimeZoneId;
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        NpgsqlParameter[] Parameters() =>
        [
            new("hub", NpgsqlDbType.Uuid) { Value = (object?)hubId ?? DBNull.Value },
            new("day", NpgsqlDbType.Date) { Value = day },
            new("week_start", NpgsqlDbType.Date) { Value = day.AddDays(-7) },
            new("tz", NpgsqlDbType.Text) { Value = timeZone },
            new("now", NpgsqlDbType.TimestampTz) { Value = now },
        ];

        var due = await RowAsync(connection, $"""
            SELECT count(*)::int, (count(*) FILTER (WHERE s.status = 'Delivered'))::int
              FROM shipments.shipments s
             WHERE s.promised_delivery_date = @day AND s.kind = 'Forward' AND s.status <> 'Cancelled' AND {HubFilter}
            """, Parameters(), cancellationToken);
        var delayed = await RowAsync(connection, $"""
            SELECT count(*)::int, 0
              FROM shipments.shipments s
             WHERE s.status NOT IN ('Delivered','ReturnedToSender','Cancelled','ReturningToSender') AND s.promised_delivery_date < @day AND {HubFilter}
            """, Parameters(), cancellationToken);
        // Son 7 tam yerel gün: [day-7, day).
        var delivered = $"""
            FROM shipments.shipments s
            WHERE s.status = 'Delivered' AND s.delivered_at IS NOT NULL
              AND (s.delivered_at AT TIME ZONE @tz)::date >= @week_start AND (s.delivered_at AT TIME ZONE @tz)::date < @day AND {HubFilter}
            """;
        var onTime = await RowAsync(connection,
            $"SELECT (count(*) FILTER (WHERE (s.delivered_at AT TIME ZONE @tz)::date <= s.promised_delivery_date))::int, count(*)::int {delivered}",
            Parameters(), cancellationToken);
        var firstAttempt = await RowAsync(connection, $"""
            SELECT (count(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM shipments.delivery_attempts a WHERE a.shipment_id = s.id AND a.outcome = 'Failed')))::int,
                   count(*)::int {delivered}
            """, Parameters(), cancellationToken);

        int openCount = 0, overdueCount = 0;
        long? oldestAge = null;
        await using (var command = Command(connection, $"""
            SELECT count(*)::int, (count(*) FILTER (WHERE x.due_at < @now))::int, min(x.opened_at)
              FROM operations.shipment_exceptions x JOIN shipments.shipments s ON s.id = x.shipment_id
             WHERE x.status IN ('Open','Assigned') AND {HubFilter}
            """, Parameters()))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            openCount = reader.GetInt32(0);
            overdueCount = reader.GetInt32(1);
            oldestAge = reader.IsDBNull(2) ? null : (long)(now - reader.GetFieldValue<DateTimeOffset>(2)).TotalSeconds;
        }

        var reasons = await GroupAsync(connection, $"""
            SELECT COALESCE(a.failure_reason, 'Unknown'), count(*)::int
              FROM shipments.delivery_attempts a JOIN shipments.shipments s ON s.id = a.shipment_id
             WHERE a.outcome = 'Failed' AND (a.occurred_at AT TIME ZONE @tz)::date = @day AND {HubFilter}
             GROUP BY 1 ORDER BY 2 DESC, 1
            """, Parameters(), cancellationToken);
        var carrier = await GroupAsync(connection, $"""
            SELECT r.outcome, count(*)::int
              FROM tracking.carrier_event_receipts r LEFT JOIN shipments.shipments s ON s.id = r.shipment_id
             WHERE r.processing_status = 'Processed' AND r.processed_at >= @now - interval '24 hours'
               AND (@hub::uuid IS NULL OR s.id IS NOT NULL AND (s.origin_hub_id = @hub OR s.destination_hub_id = @hub))
             GROUP BY 1 ORDER BY 2 DESC, 1
            """, Parameters(), cancellationToken);

        return new SummaryOutcome.Ok(new OperationsSummary(
            now.UtcDateTime,
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            timeZone,
            hubCode,
            new DueToday(due.A, due.B),
            delayed.A,
            ToRatio(onTime),
            ToRatio(firstAttempt),
            new OpenExceptionStats(openCount, overdueCount, oldestAge),
            reasons,
            carrier));
    }

    private static Ratio ToRatio((int A, int B) row) =>
        new(row.A, row.B, row.B == 0 ? null : Math.Round((decimal)row.A / row.B, 4));

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, NpgsqlParameter[] parameters)
    {
#pragma warning disable CA2100 // SQL sabit şablondur; tüm değerler parametredir.
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static async Task<(int A, int B)> RowAsync(NpgsqlConnection connection, string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task<List<CountByKey>> GroupAsync(NpgsqlConnection connection, string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CountByKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CountByKey(reader.GetString(0), reader.GetInt32(1)));
        }

        return rows;
    }
}

public sealed record AuditItem(long Id, DateTime OccurredAt, string ActorType, string ActorId, string? ActorDisplayName, string Action,
    string EntityType, string EntityId, string Outcome, string? ReasonCode, JsonElement Changes);

public sealed record AuditPage(IReadOnlyList<AuditItem> Items, long? NextBefore);

/// <summary>UC-12: gönderi ve istisnalarının audit kayıtları (yalnız OpsManager). Değişiklikler zaten allowlist'tir; PII içermez.</summary>
public sealed class ShipmentAuditHandler(LogisticsDbContext db)
{
    public async Task<AuditPage?> HandleAsync(Guid shipmentId, long? before, int limit, CancellationToken cancellationToken)
    {
        if (!await db.Set<Shipment>().AnyAsync(s => s.Id == shipmentId, cancellationToken))
        {
            return null;
        }

        var exceptionIds = await db.Set<Domain.ShipmentException>().AsNoTracking()
            .Where(x => x.ShipmentId == shipmentId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);
        var shipmentKey = shipmentId.ToString();
        var take = Math.Clamp(limit, 1, 200);
        var rows = await db.Set<AuditEntry>().AsNoTracking()
            .Where(a => (a.EntityType == "Shipment" && a.EntityId == shipmentKey) || (a.EntityType == "ShipmentException" && exceptionIds.Contains(a.EntityId)))
            .Where(a => before == null || a.Id < before)
            .OrderByDescending(a => a.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var userIds = rows.Where(r => r.ActorType == AuditActorTypes.User && Guid.TryParse(r.ActorId, out _)).Select(r => Guid.Parse(r.ActorId)).Distinct().ToList();
        var names = await db.Set<LstUser>().AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id.ToString(), u => u.DisplayName, cancellationToken);
        var items = rows.Take(take).Select(r => new AuditItem(r.Id, r.OccurredAt.UtcDateTime, r.ActorType, r.ActorId, names.GetValueOrDefault(r.ActorId),
            r.Action, r.EntityType, r.EntityId, r.Outcome, r.ReasonCode, JsonSerializer.Deserialize<JsonElement>(r.Changes))).ToList();
        return new AuditPage(items, rows.Count > take ? items[^1].Id : null);
    }
}
