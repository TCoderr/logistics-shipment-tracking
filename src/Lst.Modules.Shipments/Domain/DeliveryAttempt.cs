namespace Lst.Modules.Shipments.Domain;

public enum DeliveryOutcome
{
    Delivered,
    Failed,
}

public enum DeliveryFailureReason
{
    RecipientAbsent,
    AddressNotFound,
    RecipientRefused,
    AccessRestricted,
    PackageMissing,
    PackageDamaged,
    Other,
}

/// <summary>Numaralı teslimat denemesi (shipments.delivery_attempts, docs/04 §3.6). Append-only.</summary>
public sealed class DeliveryAttempt
{
    private DeliveryAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid ShipmentId { get; private set; }

    public short AttemptNumber { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DeliveryOutcome Outcome { get; private set; }

    public DeliveryFailureReason? FailureReason { get; private set; }

    public EventSource Source { get; private set; }

    public TrackingEvent TrackingEvent { get; private set; } = null!;

    public long TrackingEventId { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public string? Note { get; private set; }

    internal static DeliveryAttempt CreateFromCarrier(
        Guid shipmentId, short number, DateTimeOffset occurredAt, DeliveryOutcome outcome, DeliveryFailureReason? reason,
        TrackingEvent trackingEvent, string? note) =>
        new()
        {
            Id = Guid.CreateVersion7(occurredAt),
            ShipmentId = shipmentId,
            AttemptNumber = number,
            OccurredAt = occurredAt,
            Outcome = outcome,
            FailureReason = reason,
            Source = EventSource.Carrier,
            TrackingEvent = trackingEvent,
            RecordedByUserId = null,
            Note = note,
        };

    internal static DeliveryAttempt Create(
        Guid shipmentId, short number, DateTimeOffset occurredAt, DeliveryOutcome outcome, DeliveryFailureReason? reason,
        EventSource source, TrackingEvent trackingEvent, Guid actorUserId, string? note) =>
        new()
        {
            Id = Guid.CreateVersion7(occurredAt),
            ShipmentId = shipmentId,
            AttemptNumber = number,
            OccurredAt = occurredAt,
            Outcome = outcome,
            FailureReason = reason,
            Source = source,
            TrackingEvent = trackingEvent,
            RecordedByUserId = actorUserId,
            Note = note,
        };
}

/// <summary>Teslimat denemesi kararı (saf). docs/02 §3.5, T13/T14.</summary>
public static class DeliveryAttemptPolicy
{
    public const string NotOutForDelivery = "transition-not-allowed";
    public const string PackagesNotOutForDelivery = "packages-not-out-for-delivery";
    public const string ReasonRequired = "failure-reason-required";
    public const string NoteRequired = "note-required";
    public const string FutureTimestamp = "future-timestamp";
    public const string BeforeOutForDelivery = "before-out-for-delivery";

    /// <returns>Null: izinli; aksi halde red nedeni kodu.</returns>
    public static string? Validate(
        ShipmentSnapshot shipment,
        IReadOnlyList<PackageSnapshot> packages,
        DeliveryOutcome outcome,
        DeliveryFailureReason? reason,
        string? note,
        DateTimeOffset occurredAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        ArgumentNullException.ThrowIfNull(packages);

        if (shipment.Status != ShipmentStatus.OutForDelivery)
        {
            return NotOutForDelivery;
        }

        if (occurredAt > now + ScanPolicy.MaxFutureSkew)
        {
            return FutureTimestamp;
        }

        if (occurredAt < shipment.StatusOccurredAt)
        {
            return BeforeOutForDelivery;
        }

        if (outcome == DeliveryOutcome.Failed)
        {
            if (reason is null)
            {
                return ReasonRequired;
            }

            if (reason == DeliveryFailureReason.Other && string.IsNullOrWhiteSpace(note))
            {
                return NoteRequired;
            }

            return null;
        }

        // Hep-ya-hiç teslim: tüm koliler dağıtımda (kuryede) olmalı. Eksik koli → başarısız deneme (PackageMissing) kaydedilmelidir.
        return packages.All(p => p.CurrentHubId is null && p.LastScanType == ScanType.OutForDelivery)
            ? null
            : PackagesNotOutForDelivery;
    }
}

/// <summary>Shipments modülünün aynı transaction içinde yayınladığı domain olayları (Operations gibi modüller dinler).</summary>
public abstract record ShipmentDomainEvent(Guid ShipmentId, DateTimeOffset OccurredAt);

public sealed record DeliveryAttemptFailed(
    Guid ShipmentId, DateTimeOffset OccurredAt, short AttemptNumber, DeliveryFailureReason Reason, Guid DestinationHubId, bool MaxAttemptsReached)
    : ShipmentDomainEvent(ShipmentId, OccurredAt);

public sealed record ShipmentReachedTerminal(Guid ShipmentId, DateTimeOffset OccurredAt, ShipmentStatus Status)
    : ShipmentDomainEvent(ShipmentId, OccurredAt);

public sealed record ConsolidationRejected(Guid ShipmentId, DateTimeOffset OccurredAt, Guid HubId)
    : ShipmentDomainEvent(ShipmentId, OccurredAt);
