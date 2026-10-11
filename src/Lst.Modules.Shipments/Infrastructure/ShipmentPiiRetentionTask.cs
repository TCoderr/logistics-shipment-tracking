using Lst.Platform.Persistence;
using Lst.Platform.Retention;
using NpgsqlTypes;

namespace Lst.Modules.Shipments.Infrastructure;

/// <summary>
/// Terminal durumdan <see cref="RetentionOptions.PiiDays"/> gün sonra kişisel veriyi maskeler (docs/04 §6): ad "[redacted]", firma, telefon,
/// son 4 hane, e-posta, adres satırları NULL; şehir/ilçe/posta kodu kalır. Serbest metinler: iptal nedeni "[redacted]" (CHECK zorunlu kılar), olay ve deneme notu NULL.
/// Tek SQL ifadesi = parti başına atomik; <c>pii_redacted_at</c> ikinci çalıştırmayı etkisiz kılar. Terminal olmayan gönderilere dokunulmaz.
/// </summary>
internal sealed class ShipmentPiiRetentionTask : IRetentionTask
{
    public string Name => "shipment-pii";

    public int Order => 10;

    public Task<int> RunAsync(LogisticsDbContext db, RetentionContext context, CancellationToken cancellationToken) =>
        RetentionJob.RepeatBatchesAsync(db,
            """
            WITH batch AS (
                SELECT id FROM shipments.shipments
                 WHERE status IN ('Delivered','ReturnedToSender','Cancelled') AND pii_redacted_at IS NULL AND status_changed_at < @cutoff
                 ORDER BY status_changed_at, id
                 LIMIT @batch
                 FOR UPDATE SKIP LOCKED),
            parties AS (
                UPDATE shipments.shipment_parties p
                   SET full_name = '[redacted]', company_name = NULL, phone_e164 = NULL, phone_last4 = NULL, email = NULL,
                       address_line1 = NULL, address_line2 = NULL
                  FROM batch WHERE p.shipment_id = batch.id
                RETURNING 1),
            events AS (
                UPDATE shipments.tracking_events e SET internal_note = NULL
                  FROM batch WHERE e.shipment_id = batch.id AND e.internal_note IS NOT NULL
                RETURNING 1),
            attempts AS (
                UPDATE shipments.delivery_attempts a SET note = NULL
                  FROM batch WHERE a.shipment_id = batch.id AND a.note IS NOT NULL
                RETURNING 1)
            UPDATE shipments.shipments s
               SET pii_redacted_at = @now,
                   cancellation_reason = CASE WHEN s.cancellation_reason IS NULL THEN NULL ELSE '[redacted]' END
              FROM batch
             WHERE s.id = batch.id
            """,
            context, cancellationToken, ("cutoff", NpgsqlDbType.TimestampTz, context.Now.AddDays(-context.Options.PiiDays)));
}
