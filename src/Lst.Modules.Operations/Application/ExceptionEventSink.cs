using System.Diagnostics;
using System.Text.Json;
using Lst.Modules.Operations.Domain;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Operations.Application;

/// <summary>
/// Shipments domain olaylarından istisna açar/günceller/kapatır. Gönderi komutunun transaction'ı ve gönderi kilidi altında çalışır;
/// bu yüzden "tek açık kayıt" kontrolü serileşir (partial unique index son savunmadır).
/// </summary>
internal sealed class ExceptionEventSink(LogisticsDbContext db, TimeProvider timeProvider) : IShipmentEventSink
{
    public async Task HandleAsync(ShipmentDomainEvent domainEvent, ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNowMicroseconds();
        switch (domainEvent)
        {
            case DeliveryAttemptFailed failed:
                await OpenOrRefreshAsync(failed.ShipmentId, ExceptionType.DeliveryFailed, ExceptionSeverity.Medium, failed.DestinationHubId, actor, now, cancellationToken);
                if (failed.MaxAttemptsReached)
                {
                    await OpenOrRefreshAsync(failed.ShipmentId, ExceptionType.MaxAttemptsReached, ExceptionSeverity.High, failed.DestinationHubId, actor, now, cancellationToken);
                }

                break;
            case CarrierEventNeedsReview review:
                await OpenOrRefreshAsync(review.ShipmentId, ExceptionType.CarrierEventPendingReview, ExceptionSeverity.Medium, null, actor, now, cancellationToken);
                break;
            case CarrierEventConflicted conflicted:
                await OpenOrRefreshAsync(conflicted.ShipmentId, ExceptionType.CarrierEventConflict, ExceptionSeverity.High, null, actor, now, cancellationToken);
                break;
            case ConsolidationRejected rejected:
                await OpenOrRefreshAsync(rejected.ShipmentId, ExceptionType.PackageSplit, ExceptionSeverity.High, rejected.HubId, actor, now, cancellationToken);
                break;
            case ShipmentReachedTerminal terminal:
                foreach (var open in await OpenExceptionsAsync(terminal.ShipmentId, cancellationToken))
                {
                    if (ExceptionResolutionPolicy.AutoResolvesOnTerminal(open.Type))
                    {
                        open.AutoResolve(now);
                        AddAudit(actor, now, OperationsAuditActions.ExceptionAutoResolved, open, new { type = open.Type.ToString(), shipmentStatus = terminal.Status.ToString() });
                    }
                }

                break;
        }
    }

    /// <summary>Gönderi + tip için açık kayıt varsa tekrar sayısını artırır, yoksa açar (SLA worker da kullanır).</summary>
    public async Task<ShipmentException> OpenOrRefreshAsync(
        Guid shipmentId, ExceptionType type, ExceptionSeverity severity, Guid? hubId, ActorContext actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = (await OpenExceptionsAsync(shipmentId, cancellationToken)).FirstOrDefault(x => x.Type == type);
        if (existing is not null)
        {
            existing.RecordOccurrence(now);
            return existing;
        }

        var opened = ShipmentException.Open(shipmentId, type, severity, hubId, actor.Source, now);
        db.Add(opened);
        AddAudit(actor, now, OperationsAuditActions.ExceptionOpened, opened, new { type = type.ToString(), severity = severity.ToString() });
        return opened;
    }

    private async Task<List<ShipmentException>> OpenExceptionsAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        // Aynı transaction'da az önce eklenmiş (henüz kaydedilmemiş) kayıtlar da görülmeli.
        var local = db.Set<ShipmentException>().Local.Where(x => x.ShipmentId == shipmentId && x.IsOpen).ToList();
        var stored = await db.Set<ShipmentException>()
            .Where(x => x.ShipmentId == shipmentId && x.Status != ExceptionStatus.Resolved)
            .ToListAsync(cancellationToken);
        return local.Union(stored).Where(x => x.IsOpen).ToList();
    }

    private void AddAudit(ActorContext actor, DateTimeOffset now, string action, ShipmentException exception, object changes) =>
        db.Add(actor.UserId == Guid.Empty
            ? AuditEntry.SystemSucceeded(now, actor.Source, action, "ShipmentException", exception.Id.ToString(), Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(changes))
            : AuditEntry.UserSucceeded(now, actor.UserId, action, "ShipmentException", exception.Id.ToString(), Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(changes)));
}

public static class OperationsAuditActions
{
    public const string ExceptionOpened = "ExceptionOpened";
    public const string ExceptionAutoResolved = "ExceptionAutoResolved";
    public const string ExceptionEscalated = "ExceptionEscalated";
    public const string ExceptionAssigned = "ExceptionAssigned";
    public const string ExceptionResolved = "ExceptionResolved";
}
