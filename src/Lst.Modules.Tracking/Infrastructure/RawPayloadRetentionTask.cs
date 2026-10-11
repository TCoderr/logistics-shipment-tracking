using Lst.Platform.Persistence;
using Lst.Platform.Retention;
using NpgsqlTypes;

namespace Lst.Modules.Tracking.Infrastructure;

/// <summary>
/// <see cref="RetentionOptions.RawPayloadDays"/> günden eski ham webhook gövdesi ve normalize payload'ı temizler. Makbuz ve çakışma satırları
/// silinmez (tombstone): <c>tracking_events.carrier_event_receipt_id</c> FK'si korunur, hash'ler ve normalize kolonlar kalır, teknik tekrar
/// ayıklaması (carrier + event ID) süresizdir. Henüz işlenmemiş makbuzlara dokunulmaz.
/// </summary>
internal sealed class RawPayloadRetentionTask : IRetentionTask
{
    public string Name => "carrier-raw-payload";

    public int Order => 30;

    public async Task<int> RunAsync(LogisticsDbContext db, RetentionContext context, CancellationToken cancellationToken)
    {
        var cutoff = ("cutoff", NpgsqlDbType.TimestampTz, (object)context.Now.AddDays(-context.Options.RawPayloadDays));
        var receipts = await RetentionJob.RepeatBatchesAsync(db,
            """
            UPDATE tracking.carrier_event_receipts SET raw_body = NULL, payload = NULL, payload_purged_at = @now
             WHERE id IN (SELECT id FROM tracking.carrier_event_receipts
                           WHERE payload_purged_at IS NULL AND received_at < @cutoff AND processing_status IN ('Processed','DeadLettered')
                           ORDER BY received_at, id LIMIT @batch)
            """,
            context, cancellationToken, cutoff);
        var conflicts = await RetentionJob.RepeatBatchesAsync(db,
            """
            UPDATE tracking.carrier_event_conflicts SET raw_body = NULL, payload_purged_at = @now
             WHERE id IN (SELECT id FROM tracking.carrier_event_conflicts
                           WHERE payload_purged_at IS NULL AND received_at < @cutoff
                           ORDER BY received_at, id LIMIT @batch)
            """,
            context, cancellationToken, cutoff);
        return receipts + conflicts;
    }
}
