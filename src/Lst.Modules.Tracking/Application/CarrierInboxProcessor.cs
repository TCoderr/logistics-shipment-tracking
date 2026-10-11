using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Modules.Tracking.Infrastructure;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Modules.Tracking.Application;

public sealed class CarrierInboxOptions
{
    public const string SectionName = "Workers:CarrierInbox";

    public int BatchSize { get; set; } = 20;

    public int LeaseSeconds { get; set; } = 30;

    /// <summary>Kalıcı (transient olmayan) hata sayısı bu değere ulaşınca makbuz DeadLettered olur.</summary>
    public int MaxAttempts { get; set; } = 5;

    public int PollMilliseconds { get; set; } = 1000;
}

public sealed record ClaimedReceipt(Guid Id, Guid LockToken);

public enum ProcessResult
{
    Processed,
    LostLease,
    TransientFailure,
    RetryScheduled,
    DeadLettered,
}

/// <summary>
/// Carrier inbox işleyicisi (ADR-016). Claim: <c>FOR UPDATE SKIP LOCKED</c> ile kısa transaction'da lease + yeni lock_token.
/// İşleme: makbuz satırı token eşleşmesiyle kilitlenir (fencing); token değişmişse (lease başka worker'a geçmiş) hiçbir şey commit edilmez.
/// Transient (bağlantı/kilit/serileştirme) hatası deneme bütçesini tüketmez ve DLQ'ya düşürmez; kalıcı hata üstel backoff ile
/// yeniden denenir, <see cref="CarrierInboxOptions.MaxAttempts"/> sonunda DeadLettered + audit.
/// </summary>
public sealed partial class CarrierInboxProcessor(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<CarrierInboxOptions> options,
    ILogger<CarrierInboxProcessor> logger)
{
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private readonly string workerId = $"worker:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>Bir tur: claim + her makbuzu ayrı transaction'da işle. İşlenen makbuz sayısını döner.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(options.Value.BatchSize, cancellationToken);
        foreach (var receipt in claimed)
        {
            await ProcessAsync(receipt, cancellationToken);
        }

        return claimed.Count;
    }

    public Task<IReadOnlyList<ClaimedReceipt>> ClaimAsync(int batchSize, CancellationToken cancellationToken) =>
        ClaimAsync(batchSize, null, cancellationToken);

    /// <summary><paramref name="only"/> verilirse yalnız bu makbuzlar arasından claim edilir (operasyon aracı ve testler için).</summary>
    public async Task<IReadOnlyList<ClaimedReceipt>> ClaimAsync(int batchSize, IReadOnlyCollection<Guid>? only, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNowMicroseconds();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {TrackingModelContributor.Schema}.carrier_event_receipts r
               SET processing_status = 'Processing', lock_token = gen_random_uuid(), locked_until = @lease_until, locked_by = @worker
             WHERE r.id IN (
                   SELECT id FROM {TrackingModelContributor.Schema}.carrier_event_receipts
                    WHERE ((processing_status = 'Received' AND next_attempt_at <= @now)
                       OR (processing_status = 'Processing' AND locked_until < @now))
                      AND (@only::uuid[] IS NULL OR id = ANY(@only))
                    ORDER BY next_attempt_at, id
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
            RETURNING r.id, r.lock_token, r.received_at
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        command.Parameters.Add(new NpgsqlParameter("lease_until", NpgsqlDbType.TimestampTz) { Value = now.AddSeconds(options.Value.LeaseSeconds) });
        command.Parameters.Add(new NpgsqlParameter("worker", NpgsqlDbType.Varchar) { Value = workerId });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter("only", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = only is null ? DBNull.Value : only.ToArray() });
        var claimed = new List<(ClaimedReceipt Receipt, DateTime ReceivedAt)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add((new ClaimedReceipt(reader.GetGuid(0), reader.GetGuid(1)), reader.GetDateTime(2)));
        }

        // UPDATE ... RETURNING satır sırası garanti etmez. Aynı partideki makbuzlar alınış sırasıyla işlenir; aksi halde sıralı gelen
        // bir carrier akışını işçi kendisi karıştırır ve geçerli olaylar gereksiz yere Stale sonuçlanır. Bu bir en-iyi-çaba sıralamasıdır:
        // doğruluk yine olay kararlarına (sequence/occurredAt) dayanır, sıra garantisine değil.
        return [.. claimed.OrderBy(c => c.ReceivedAt).ThenBy(c => c.Receipt.Id).Select(c => c.Receipt)];
    }

    public async Task<ProcessResult> ProcessAsync(ClaimedReceipt claimed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        try
        {
            return await ApplyAsync(claimed, cancellationToken);
        }
        catch (Exception ex) when (IsTransient(ex) && !cancellationToken.IsCancellationRequested)
        {
            LogTransient(logger, claimed.Id, ex.GetType().Name);
            await TryReleaseAsync(claimed, ex.GetType().Name);
            return ProcessResult.TransientFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Kapanış: lease süresi dolunca başka worker devralır; deneme sayılmaz.
            throw;
        }
#pragma warning disable CA1031 // Kalıcı hata makbuz üzerinde kayıt altına alınır; worker döngüsü durmaz.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPoison(logger, claimed.Id, ex.GetType().Name);
            try
            {
                return await RecordFailureAsync(claimed, ex, cancellationToken);
            }
            catch (Exception recordEx) when (IsTransient(recordEx))
            {
                // Hata kaydı da yazılamadı (ör. DB kesintisi): lease dolunca yeniden denenir, bütçe tüketilmez.
                return ProcessResult.TransientFailure;
            }
        }
    }

    private async Task<ProcessResult> ApplyAsync(ClaimedReceipt claimed, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        var carrierEvents = scope.ServiceProvider.GetRequiredService<IShipmentCarrierEvents>();
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);

        var row = await LockOwnedAsync(db, claimed, cancellationToken);
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            LogLostLease(logger, claimed.Id);
            return ProcessResult.LostLease;
        }

        var input = row.ToInput(claimed.Id);
        var result = await carrierEvents.ApplyAsync(input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var now = timeProvider.GetUtcNowMicroseconds();
        var updated = await ExecuteFencedAsync(db, claimed,
            """
            processing_status = 'Processed', outcome = @outcome, outcome_reason = @reason, shipment_id = @shipment_id,
            processed_at = @now, last_error = NULL, lock_token = NULL, locked_until = NULL, locked_by = NULL
            """,
            cancellationToken,
            new NpgsqlParameter("outcome", NpgsqlDbType.Varchar) { Value = result.Outcome.ToString() },
            new NpgsqlParameter("reason", NpgsqlDbType.Varchar) { Value = (object?)result.Reason ?? DBNull.Value },
            new NpgsqlParameter("shipment_id", NpgsqlDbType.Uuid) { Value = (object?)result.ShipmentId ?? DBNull.Value },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        if (updated != 1)
        {
            // Satır kilitli olduğundan beklenmez; yine de token eşleşmiyorsa hiçbir şey commit edilmez.
            await transaction.RollbackAsync(cancellationToken);
            return ProcessResult.LostLease;
        }

        await transaction.CommitAsync(CancellationToken.None);
        return ProcessResult.Processed;
    }

    private async Task<ProcessResult> RecordFailureAsync(ClaimedReceipt claimed, Exception error, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockOwnedAsync(db, claimed, cancellationToken);
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProcessResult.LostLease;
        }

        var now = timeProvider.GetUtcNowMicroseconds();
        var attempts = row.Attempts + 1;
        // Hata metni yerine yalnız tip adı saklanır (mesaj PII veya bağlantı ayrıntısı içerebilir).
        var lastError = error.GetType().FullName ?? error.GetType().Name;
        if (attempts >= options.Value.MaxAttempts)
        {
            await ExecuteFencedAsync(db, claimed,
                """
                processing_status = 'DeadLettered', attempts = @attempts, last_error = @error,
                lock_token = NULL, locked_until = NULL, locked_by = NULL
                """,
                cancellationToken,
                new NpgsqlParameter("attempts", NpgsqlDbType.Smallint) { Value = (short)attempts },
                new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = lastError });
            db.Add(AuditEntry.SystemSucceeded(now, "system:carrier-inbox", TrackingAuditActions.CarrierEventDeadLettered, "CarrierEventReceipt",
                claimed.Id.ToString(), Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(new { attempts, error = lastError })));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            return ProcessResult.DeadLettered;
        }

        await ExecuteFencedAsync(db, claimed,
            """
            processing_status = 'Received', attempts = @attempts, last_error = @error, next_attempt_at = @next,
            lock_token = NULL, locked_until = NULL, locked_by = NULL
            """,
            cancellationToken,
            new NpgsqlParameter("attempts", NpgsqlDbType.Smallint) { Value = (short)attempts },
            new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = lastError },
            new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = now + Backoff(attempts) });
        await transaction.CommitAsync(CancellationToken.None);
        return ProcessResult.RetryScheduled;
    }

    /// <summary>Transient hatada lease bırakılır; deneme sayısı artmaz. Bu da başarısızsa lease süresi dolunca devralınır.</summary>
    private async Task TryReleaseAsync(ClaimedReceipt claimed, string errorType)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            await db.Database.OpenConnectionAsync(CancellationToken.None);
            await ExecuteFencedAsync(db, claimed,
                """
                processing_status = 'Received', next_attempt_at = @next, last_error = @error,
                lock_token = NULL, locked_until = NULL, locked_by = NULL
                """,
                CancellationToken.None,
                new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = timeProvider.GetUtcNowMicroseconds() + TransientRetryDelay },
                new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = "transient:" + errorType });
        }
#pragma warning disable CA1031 // DB erişilemiyorsa lease zaman aşımı yeterlidir.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    public static TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Pow(2, Math.Clamp(attempts, 1, 16)) * 10;
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    /// <summary>Bkz. <see cref="TransientFailures"/>.</summary>
    public static bool IsTransient(Exception exception) => TransientFailures.IsTransient(exception);

    private static async Task<OwnedReceipt?> LockOwnedAsync(LogisticsDbContext db, ClaimedReceipt claimed, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT r.carrier_id, c.code, r.payload::text, r.received_at, r.attempts
              FROM {TrackingModelContributor.Schema}.carrier_event_receipts r
              JOIN {TrackingModelContributor.Schema}.carriers c ON c.id = r.carrier_id
             WHERE r.id = @id AND r.lock_token = @token AND r.processing_status = 'Processing'
               FOR UPDATE OF r
            """,
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = claimed.Id });
        command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = claimed.LockToken });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OwnedReceipt(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt16(4));
    }

    private static async Task<int> ExecuteFencedAsync(
        LogisticsDbContext db, ClaimedReceipt claimed, string setClause, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {TrackingModelContributor.Schema}.carrier_event_receipts SET {setClause} WHERE id = @id AND lock_token = @token AND processing_status = 'Processing'",
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = claimed.Id });
        command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = claimed.LockToken });
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record OwnedReceipt(Guid CarrierId, string CarrierCode, string? Payload, DateTimeOffset ReceivedAt, short Attempts)
    {
        public CarrierEventInput ToInput(Guid receiptId)
        {
            var canonical = JsonSerializer.Deserialize<CanonicalPayload>(Payload ?? throw new InvalidDataException("Receipt payload missing."))
                ?? throw new InvalidDataException("Receipt payload invalid.");
            var eventType = CarrierWebhookParser.MapEventType(canonical.EventType)
                ?? throw new InvalidDataException("Receipt event type unsupported.");
            // Domain ve veritabanı UTC çalışır; carrier'ın gönderdiği ofset kanonik payload'da (kanıt) kalır.
            var occurredAt = new DateTimeOffset(canonical.OccurredAtUtcTicks, TimeSpan.Zero);
            DeliveryFailureReason? reason = canonical.FailureReason is null ? null : Enum.Parse<DeliveryFailureReason>(canonical.FailureReason);
            return new CarrierEventInput(CarrierId, CarrierCode, receiptId, canonical.TrackingNumber, eventType, occurredAt, ReceivedAt,
                canonical.Sequence, canonical.FacilityCode, canonical.City, canonical.AttemptNumber, reason);
        }
    }

    private sealed record CanonicalPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("eventId")] string EventId,
        [property: System.Text.Json.Serialization.JsonPropertyName("trackingNumber")] string TrackingNumber,
        [property: System.Text.Json.Serialization.JsonPropertyName("eventType")] string EventType,
        [property: System.Text.Json.Serialization.JsonPropertyName("occurredAtUtcTicks")] long OccurredAtUtcTicks,
        [property: System.Text.Json.Serialization.JsonPropertyName("offsetMinutes")] int OffsetMinutes,
        [property: System.Text.Json.Serialization.JsonPropertyName("sequence")] long? Sequence,
        [property: System.Text.Json.Serialization.JsonPropertyName("facilityCode")] string? FacilityCode,
        [property: System.Text.Json.Serialization.JsonPropertyName("city")] string? City,
        [property: System.Text.Json.Serialization.JsonPropertyName("attemptNumber")] short? AttemptNumber,
        [property: System.Text.Json.Serialization.JsonPropertyName("failureReason")] string? FailureReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Carrier receipt {ReceiptId} transient failure ({ErrorType}); lease released without consuming an attempt")]
    private static partial void LogTransient(ILogger logger, Guid receiptId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Carrier receipt {ReceiptId} processing failed ({ErrorType})")]
    private static partial void LogPoison(ILogger logger, Guid receiptId, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Carrier receipt {ReceiptId} lease lost; nothing committed")]
    private static partial void LogLostLease(ILogger logger, Guid receiptId);
}
