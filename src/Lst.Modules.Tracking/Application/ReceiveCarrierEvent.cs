using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Tracking.Domain;
using Lst.Modules.Tracking.Infrastructure;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Modules.Tracking.Application;

public abstract record ReceiveOutcome
{
    private ReceiveOutcome()
    {
    }

    /// <summary>Yeni makbuz kalıcı yazıldı (202); iş sonucu henüz belli değil.</summary>
    public sealed record Received(Guid ReceiptId) : ReceiveOutcome;

    /// <summary>Aynı event ID + aynı kanonik içerik (200).</summary>
    public sealed record Duplicate(Guid ReceiptId) : ReceiveOutcome;

    /// <summary>Aynı event ID, farklı içerik (409): ilk makbuz değişmez, kanıt ve istisna kaydedildi.</summary>
    public sealed record Conflict(Guid ReceiptId) : ReceiveOutcome;
}

/// <summary>
/// Webhook makbuzu: imza doğrulandıktan sonra tek transaction'da INSERT … ON CONFLICT DO NOTHING; çakışmada mevcut kayıt okunur.
/// HTTP yanıtı yalnız commit sonrası döner (ack = kalıcılık).
/// </summary>
public sealed class CarrierReceiptWriter(LogisticsDbContext db, IShipmentCarrierEvents shipmentCarrierEvents, TimeProvider timeProvider)
{
    public async Task<ReceiveOutcome> HandleAsync(
        Carrier carrier, NormalizedCarrierEvent normalized, byte[] rawBody, DateTimeOffset signedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(rawBody);
        var now = timeProvider.GetUtcNowMicroseconds();
        var payloadHash = normalized.CanonicalSha256();
        var rawHash = SHA256.HashData(rawBody);

        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var receiptId = Guid.CreateVersion7(now);
        var inserted = await InsertAsync(receiptId, carrier.Id, normalized, payloadHash, rawHash, rawBody, signedAt, now, cancellationToken);
        if (inserted)
        {
            await transaction.CommitAsync(CancellationToken.None);
            return new ReceiveOutcome.Received(receiptId);
        }

        var existing = await db.Set<CarrierEventReceipt>().AsNoTracking()
            .Where(r => r.CarrierId == carrier.Id && r.ExternalEventId == normalized.EventId)
            .Select(r => new { r.Id, r.PayloadSha256 })
            .FirstAsync(cancellationToken);
        if (CryptographicOperations.FixedTimeEquals(existing.PayloadSha256, payloadHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ReceiveOutcome.Duplicate(existing.Id);
        }

        db.Add(new CarrierEventConflict(Guid.CreateVersion7(now), existing.Id, now, payloadHash, rawHash, rawBody, signedAt));
        var shipmentId = await shipmentCarrierEvents.ReportConflictAsync(existing.Id, carrier.Id, normalized.TrackingNumber, now, cancellationToken);
        db.Add(AuditEntry.SystemSucceeded(now, $"carrier:{carrier.Code}", TrackingAuditActions.CarrierEventConflictDetected, "CarrierEventReceipt",
            existing.Id.ToString(), Activity.Current?.TraceId.ToString(),
            JsonSerializer.Serialize(new { externalEventId = normalized.EventId, shipmentId })));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return new ReceiveOutcome.Conflict(existing.Id);
    }

    private async Task<bool> InsertAsync(
        Guid id, Guid carrierId, NormalizedCarrierEvent e, byte[] payloadHash, byte[] rawHash, byte[] rawBody,
        DateTimeOffset signedAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {TrackingModelContributor.Schema}.carrier_event_receipts
                (id, carrier_id, external_event_id, payload_sha256, raw_sha256, raw_body, payload, signature_timestamp, received_at,
                 tracking_number, event_type, occurred_at, carrier_sequence, processing_status, attempts, next_attempt_at)
            VALUES (@id, @carrier_id, @external_event_id, @payload_sha256, @raw_sha256, @raw_body, @payload, @signed_at, @now,
                    @tracking_number, @event_type, @occurred_at, @sequence, 'Received', 0, @now)
            ON CONFLICT (carrier_id, external_event_id) DO NOTHING
            RETURNING id
            """,
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        command.Parameters.Add(new NpgsqlParameter("carrier_id", NpgsqlDbType.Uuid) { Value = carrierId });
        command.Parameters.Add(new NpgsqlParameter("external_event_id", NpgsqlDbType.Varchar) { Value = e.EventId });
        command.Parameters.Add(new NpgsqlParameter("payload_sha256", NpgsqlDbType.Bytea) { Value = payloadHash });
        command.Parameters.Add(new NpgsqlParameter("raw_sha256", NpgsqlDbType.Bytea) { Value = rawHash });
        command.Parameters.Add(new NpgsqlParameter("raw_body", NpgsqlDbType.Bytea) { Value = rawBody });
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = e.ToCanonicalJson() });
        command.Parameters.Add(new NpgsqlParameter("signed_at", NpgsqlDbType.TimestampTz) { Value = signedAt });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        command.Parameters.Add(new NpgsqlParameter("tracking_number", NpgsqlDbType.Varchar) { Value = e.TrackingNumber });
        command.Parameters.Add(new NpgsqlParameter("event_type", NpgsqlDbType.Varchar) { Value = e.EventType.ToString() });
        command.Parameters.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.TimestampTz) { Value = e.OccurredAt.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter("sequence", NpgsqlDbType.Bigint) { Value = (object?)e.Sequence ?? DBNull.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }
}

public static class TrackingAuditActions
{
    public const string CarrierEventConflictDetected = "CarrierEventConflictDetected";
    public const string CarrierEventDeadLettered = "CarrierEventDeadLettered";
}
