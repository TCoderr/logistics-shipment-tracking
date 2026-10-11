using Lst.Platform.Persistence;
using Lst.Platform.Retention;

namespace Lst.Modules.Operations.Infrastructure;

/// <summary>PII'si maskelenmiş gönderilerin istisna çözüm notlarını (serbest metin) temizler. Gönderi maskelemesinden sonra çalışır.</summary>
internal sealed class ExceptionNoteRetentionTask : IRetentionTask
{
    public string Name => "exception-notes";

    public int Order => 20;

    public Task<int> RunAsync(LogisticsDbContext db, RetentionContext context, CancellationToken cancellationToken) =>
        RetentionJob.RepeatBatchesAsync(db,
            """
            UPDATE operations.shipment_exceptions x SET resolution_note = NULL
             WHERE x.id IN (SELECT e.id FROM operations.shipment_exceptions e JOIN shipments.shipments s ON s.id = e.shipment_id
                             WHERE s.pii_redacted_at IS NOT NULL AND e.resolution_note IS NOT NULL
                             ORDER BY e.id LIMIT @batch)
            """,
            context, cancellationToken);
}
