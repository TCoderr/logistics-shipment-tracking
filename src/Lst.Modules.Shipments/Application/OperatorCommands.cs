using System.Diagnostics;
using System.Text.Json;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Shipments.Application;

/// <summary>Domain olaylarını kayıtlı dinleyicilere aynı transaction içinde iletir.</summary>
public sealed class ShipmentEventPublisher(IEnumerable<IShipmentEventSink> sinks)
{
    public async Task PublishAsync(IEnumerable<ShipmentDomainEvent> events, ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var domainEvent in events)
        {
            foreach (var sink in sinks)
            {
                await sink.HandleAsync(domainEvent, actor, cancellationToken);
            }
        }
    }
}

/// <summary>Operatör komutunun gönderi üzerindeki etkisi; null = izinli değil (Refusal doldurulur).</summary>
internal sealed record OperatorMutation(string AuditAction, string? Refusal, IReadOnlyList<ShipmentDomainEvent> Events, object? AuditChanges);

/// <summary>
/// If-Match korumalı operatör komutları için ortak akış (ADR-014): kilit → ETag karşılaştırma (412) → domain kuralı (409) →
/// değişiklik + domain olayları + audit → commit.
/// </summary>
public sealed class ShipmentOperatorCommands(
    LogisticsDbContext db,
    PromisedDeliveryCalculator calendar,
    TimeProvider timeProvider,
    ShipmentEventPublisher publisher)
{
    public const int MaxReasonLength = 500;

    public Task<OperatorCommandOutcome> CancelAsync(Guid shipmentId, string ifMatch, string reason, ActorAccess access, CancellationToken cancellationToken) =>
        RunAsync(shipmentId, ifMatch, access, (shipment, now) =>
        {
            if (!shipment.CanCancel)
            {
                return new OperatorMutation(ShipmentAuditActions.ShipmentCancelled, "shipment-not-cancellable", [], null);
            }

            var before = shipment.Status;
            shipment.Cancel(reason, access.UserId, now, calendar.OffsetMinutes(now));
            // Neden metni serbest metindir (PII içerebilir); audit'e yalnız varlığı yazılır.
            return new OperatorMutation(ShipmentAuditActions.ShipmentCancelled, null,
                [new ShipmentReachedTerminal(shipment.Id, now, shipment.Status)],
                new { statusBefore = before.ToString(), statusAfter = shipment.Status.ToString(), reasonProvided = true });
        }, cancellationToken);

    public Task<OperatorCommandOutcome> AssignCarrierAsync(Guid shipmentId, string ifMatch, Guid carrierId, string carrierCode, ActorAccess access, CancellationToken cancellationToken) =>
        RunAsync(shipmentId, ifMatch, access, (shipment, now) =>
        {
            if (!shipment.CanAssignCarrier)
            {
                return new OperatorMutation(ShipmentAuditActions.CarrierAssigned, "carrier-assignment-not-allowed", [], null);
            }

            shipment.AssignCarrier(carrierId, now);
            return new OperatorMutation(ShipmentAuditActions.CarrierAssigned, null, [], new { carrierCode });
        }, cancellationToken);

    public Task<OperatorCommandOutcome> ReturnToSenderAsync(Guid shipmentId, string ifMatch, ActorAccess access, CancellationToken cancellationToken) =>
        RunAsync(shipmentId, ifMatch, access, (shipment, now) =>
        {
            if (!shipment.ReturnToSenderAllowed)
            {
                return new OperatorMutation(ShipmentAuditActions.ReturnToSenderStarted, "return-not-allowed", [], null);
            }

            var before = shipment.Status;
            shipment.StartReturnToSender(access.UserId, now, calendar.OffsetMinutes(now));
            return new OperatorMutation(ShipmentAuditActions.ReturnToSenderStarted, null, [],
                new { statusBefore = before.ToString(), statusAfter = shipment.Status.ToString(), reasonProvided = true });
        }, cancellationToken);

    public Task<OperatorCommandOutcome> CompleteReturnAsync(Guid shipmentId, string ifMatch, ActorAccess access, CancellationToken cancellationToken) =>
        RunAsync(shipmentId, ifMatch, access, (shipment, now) =>
        {
            if (shipment.Status != ShipmentStatus.ReturningToSender)
            {
                return new OperatorMutation(ShipmentAuditActions.ReturnCompleted, "transition-not-allowed", [], null);
            }

            var (_, terminal) = shipment.CompleteReturn(access.UserId, now, calendar.OffsetMinutes(now));
            return new OperatorMutation(ShipmentAuditActions.ReturnCompleted, null, [terminal],
                new { statusBefore = ShipmentStatus.ReturningToSender.ToString(), statusAfter = shipment.Status.ToString() });
        }, cancellationToken);

    private async Task<OperatorCommandOutcome> RunAsync(
        Guid shipmentId,
        string ifMatch,
        ActorAccess access,
        Func<Shipment, DateTimeOffset, OperatorMutation> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var now = timeProvider.GetUtcNowMicroseconds();
        await using var transaction = await ShipmentCommandSupport.BeginAsync(db, cancellationToken);
        try
        {
            if (!await ShipmentCommandSupport.LockShipmentAsync(db, shipmentId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new OperatorCommandOutcome.NotFound();
            }

            var shipment = await db.Set<Shipment>().FirstAsync(s => s.Id == shipmentId, cancellationToken);
            var currentETag = ShipmentETag.From(shipment.Version);
            if (!string.Equals(currentETag, ifMatch, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new OperatorCommandOutcome.PreconditionFailed(currentETag);
            }

            var mutation = mutate(shipment, now);
            if (mutation.Refusal is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new OperatorCommandOutcome.NotAllowed(mutation.Refusal, shipment.Status.ToString(), shipment.ReturnToSenderAllowed);
            }

            await publisher.PublishAsync(mutation.Events, new ActorContext(access.UserId, nameof(EventSource.Operator)), cancellationToken);
            db.Add(AuditEntry.UserSucceeded(now, access.UserId, mutation.AuditAction, "Shipment", shipment.Id.ToString(),
                Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(mutation.AuditChanges)));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);

            return new OperatorCommandOutcome.Succeeded(
                new ShipmentCommandResponse(shipment.Id, shipment.TrackingNumber, shipment.Status.ToString(), shipment.StatusChangedAt.UtcDateTime),
                ShipmentETag.From(shipment.Version));
        }
        catch (Exception ex) when (PostgresErrors.IsLockTimeout(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new OperatorCommandOutcome.Busy();
        }
    }
}

/// <summary>
/// <see cref="IShipmentOperations"/>: Operations istisna çözümünün gönderi aksiyonları. Çağıranın transaction'ı içinde gönderiyi kilitler;
/// ret sonucunda hiçbir şey yazmaz (çağıran rollback yapar).
/// </summary>
public sealed class ShipmentOperations(LogisticsDbContext db, PromisedDeliveryCalculator calendar, TimeProvider timeProvider) : IShipmentOperations
{
    public async Task<ShipmentOperationResult> GrantExtraAttemptAsync(Guid shipmentId, ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNowMicroseconds();
        if (!await ShipmentCommandSupport.LockShipmentAsync(db, shipmentId, cancellationToken))
        {
            return new ShipmentOperationResult(false, "not-found", null);
        }

        var shipment = await db.Set<Shipment>().FirstAsync(s => s.Id == shipmentId, cancellationToken);
        if (shipment.Status.IsTerminal())
        {
            return ShipmentOperationResult.Refused("transition-not-allowed", shipment.Status);
        }

        shipment.GrantExtraAttempt(now);
        db.Add(AuditEntry.UserSucceeded(now, actor.UserId, ShipmentAuditActions.ExtraAttemptGranted, "Shipment", shipment.Id.ToString(),
            Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(new { extraAttemptsGranted = shipment.ExtraAttemptsGranted })));
        return ShipmentOperationResult.Ok;
    }

    public async Task<ShipmentOperationResult> StartReturnToSenderAsync(Guid shipmentId, ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNowMicroseconds();
        if (!await ShipmentCommandSupport.LockShipmentAsync(db, shipmentId, cancellationToken))
        {
            return new ShipmentOperationResult(false, "not-found", null);
        }

        var shipment = await db.Set<Shipment>().FirstAsync(s => s.Id == shipmentId, cancellationToken);
        if (!shipment.ReturnToSenderAllowed)
        {
            return ShipmentOperationResult.Refused("return-not-allowed", shipment.Status);
        }

        var before = shipment.Status;
        shipment.StartReturnToSender(actor.UserId, now, calendar.OffsetMinutes(now));
        db.Add(AuditEntry.UserSucceeded(now, actor.UserId, ShipmentAuditActions.ReturnToSenderStarted, "Shipment", shipment.Id.ToString(),
            Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(new { statusBefore = before.ToString(), statusAfter = shipment.Status.ToString(), via = "ExceptionResolution" })));
        return ShipmentOperationResult.Ok;
    }
}
