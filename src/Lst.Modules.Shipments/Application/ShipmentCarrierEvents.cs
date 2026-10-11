using System.Diagnostics;
using System.Text.Json;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Shipments.Application;

/// <summary>
/// <see cref="IShipmentCarrierEvents"/>: carrier olayını gönderi kilidi altında karara bağlar ve uygular (docs/02 §5.3).
/// İş tekrarı (iş anahtarı ±120 sn) ve sequence tekrarı kontrolü ile olay INSERT'ü aynı kilit altındadır.
/// </summary>
public sealed class ShipmentCarrierEvents(
    LogisticsDbContext db,
    PromisedDeliveryCalculator calendar,
    ShipmentEventPublisher publisher) : IShipmentCarrierEvents
{
    public const string UnknownShipment = "unknown-shipment";

    public async Task<CarrierApplyResult> ApplyAsync(CarrierEventInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var shipmentId = await db.Set<Shipment>().AsNoTracking()
            .Where(s => s.TrackingNumber == input.TrackingNumber).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (shipmentId is null)
        {
            return new CarrierApplyResult(CarrierOutcome.Rejected, UnknownShipment, null);
        }

        await ShipmentCommandSupport.LockShipmentAsync(db, shipmentId.Value, cancellationToken);
        var shipment = await db.Set<Shipment>().FirstAsync(s => s.Id == shipmentId, cancellationToken);

        var key = CarrierEventPolicy.BusinessKey(input.CarrierCode, input.EventType, input.LocationCode, input.AttemptNumber);
        var from = input.OccurredAt - ScanPolicy.BusinessDuplicateWindow;
        var to = input.OccurredAt + ScanPolicy.BusinessDuplicateWindow;
        var businessDuplicate = await db.Set<TrackingEvent>().AsNoTracking()
            .AnyAsync(e => e.ShipmentId == shipment.Id && e.BusinessKey == key && e.OccurredAt >= from && e.OccurredAt <= to, cancellationToken);
        var sequenceDuplicate = input.Sequence is { } sequence && await db.Set<TrackingEvent>().AsNoTracking()
            .AnyAsync(e => e.ShipmentId == shipment.Id && e.Source == EventSource.Carrier && e.CarrierSequence == sequence, cancellationToken);

        var decision = CarrierEventPolicy.Decide(shipment.ToCarrierState(), input, businessDuplicate, sequenceDuplicate);
        var actor = new ActorContext(Guid.Empty, nameof(EventSource.Carrier));
        if (decision.Outcome is CarrierOutcome.Applied or CarrierOutcome.Stale or CarrierOutcome.PendingReview)
        {
            var before = shipment.Status;
            var events = shipment.ApplyCarrierEvent(decision, input, calendar.OffsetMinutes(input.ReceivedAt));
            await publisher.PublishAsync(events, actor, cancellationToken);
            db.Add(AuditEntry.SystemSucceeded(input.ReceivedAt, $"carrier:{input.CarrierCode}", ShipmentAuditActions.CarrierEventRecorded, "Shipment",
                shipment.Id.ToString(), Activity.Current?.TraceId.ToString(),
                JsonSerializer.Serialize(new
                {
                    receiptId = input.ReceiptId,
                    eventType = input.EventType.ToString(),
                    outcome = decision.Outcome.ToString(),
                    reason = decision.Reason,
                    statusBefore = before.ToString(),
                    statusAfter = shipment.Status.ToString(),
                })));
        }

        // Başka carrier'a ait gönderi makbuza bağlanmaz (provider binding; varlık sızdırılmaz).
        return new CarrierApplyResult(decision.Outcome, decision.Reason,
            decision.Reason == CarrierReasons.CarrierNotAssigned ? null : shipment.Id);
    }

    public async Task<Guid?> ReportConflictAsync(Guid receiptId, Guid carrierId, string trackingNumber, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var shipmentId = await db.Set<Shipment>().AsNoTracking()
            .Where(s => s.TrackingNumber == trackingNumber && s.CarrierId == carrierId).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (shipmentId is null)
        {
            return null;
        }

        await ShipmentCommandSupport.LockShipmentAsync(db, shipmentId.Value, cancellationToken);
        await publisher.PublishAsync([new CarrierEventConflicted(shipmentId.Value, now, receiptId)],
            new ActorContext(Guid.Empty, nameof(EventSource.Carrier)), cancellationToken);
        return shipmentId;
    }
}
