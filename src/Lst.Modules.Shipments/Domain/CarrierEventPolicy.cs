namespace Lst.Modules.Shipments.Domain;

public enum CarrierOutcome
{
    Applied,
    Duplicate,
    Stale,
    Rejected,
    PendingReview,
}

/// <summary>Normalize edilmiş, imzası doğrulanmış carrier olayı (Tracking modülü üretir).</summary>
public sealed record CarrierEventInput(
    Guid CarrierId,
    string CarrierCode,
    Guid ReceiptId,
    string TrackingNumber,
    TrackingEventType EventType,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    long? Sequence,
    string? LocationCode,
    string? LocationCity,
    short? AttemptNumber,
    DeliveryFailureReason? FailureReason);

public sealed record CarrierShipmentState(
    ShipmentStatus Status,
    DateTimeOffset StatusOccurredAt,
    Guid? CarrierId,
    long? LastCarrierSequence,
    bool HandedOver,
    bool HasDeparted,
    short DeliveryAttemptCount,
    int MaxDeliveryAttempts);

public sealed record CarrierDecision(CarrierOutcome Outcome, string? Reason, ShipmentStatus? StatusAfter = null, bool MarksHandover = false)
{
    public bool ChangesStatus => StatusAfter is not null;
}

public static class CarrierReasons
{
    public const string CarrierNotAssigned = "carrier-not-assigned";
    public const string FutureTimestamp = "future-timestamp";
    public const string TooOld = "too-old";
    public const string BusinessDuplicate = "business-duplicate";
    public const string SequenceDuplicate = "sequence-duplicate";
    public const string SequenceStale = "sequence-stale";
    public const string ShipmentStale = "shipment-stale";
    public const string ContradictsTerminal = "contradicts-terminal";
    public const string TransitionNotAllowed = "transition-not-allowed";
    public const string MaxAttemptsReached = "max-attempts-reached";
    public const string LocationOnly = "LocationOnly";
}

/// <summary>
/// Carrier olayı karar fonksiyonu (docs/02 §5.3). Saf; sıra: carrier bağı → saat sapması → iş/sequence tekrarı → sequence stale →
/// terminal koruma → zaman sırası → carrier geçiş tablosu (atlamalı ileri geçişe izin; tabloda olmayan → PendingReview).
/// Olaylar körlemesine timestamp'e göre yeniden oynatılmaz; kaynak güveni (carrier ataması) ve terminal koruma önce uygulanır.
/// </summary>
public static class CarrierEventPolicy
{
    public static CarrierDecision Decide(CarrierShipmentState state, CarrierEventInput evt, bool businessDuplicate, bool sequenceDuplicate)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evt);

        if (state.CarrierId != evt.CarrierId)
        {
            return new CarrierDecision(CarrierOutcome.Rejected, CarrierReasons.CarrierNotAssigned);
        }

        if (evt.OccurredAt > evt.ReceivedAt + ScanPolicy.MaxFutureSkew)
        {
            return new CarrierDecision(CarrierOutcome.Rejected, CarrierReasons.FutureTimestamp);
        }

        if (evt.OccurredAt < evt.ReceivedAt - ScanPolicy.MaxAge)
        {
            return new CarrierDecision(CarrierOutcome.PendingReview, CarrierReasons.TooOld);
        }

        if (sequenceDuplicate)
        {
            return new CarrierDecision(CarrierOutcome.Duplicate, CarrierReasons.SequenceDuplicate);
        }

        if (businessDuplicate)
        {
            return new CarrierDecision(CarrierOutcome.Duplicate, CarrierReasons.BusinessDuplicate);
        }

        if (evt.Sequence is { } sequence && state.LastCarrierSequence is { } last && sequence <= last)
        {
            return new CarrierDecision(CarrierOutcome.Stale, CarrierReasons.SequenceStale);
        }

        var claimed = ClaimedStatus(evt.EventType);
        if (state.Status.IsTerminal())
        {
            if (evt.OccurredAt < state.StatusOccurredAt)
            {
                return new CarrierDecision(CarrierOutcome.Stale, CarrierReasons.ShipmentStale);
            }

            return claimed == state.Status
                ? new CarrierDecision(CarrierOutcome.Duplicate, CarrierReasons.BusinessDuplicate)
                : new CarrierDecision(CarrierOutcome.PendingReview, CarrierReasons.ContradictsTerminal);
        }

        if (evt.OccurredAt < state.StatusOccurredAt)
        {
            return new CarrierDecision(CarrierOutcome.Stale, CarrierReasons.ShipmentStale);
        }

        var handover = !state.HandedOver;
        return (evt.EventType, state.Status) switch
        {
            // Teslim alma / yolda / tesis olayları: Created veya hub'dan T2/T4; yolda iken yalnız konum.
            (TrackingEventType.HandedToCarrier or TrackingEventType.CarrierInTransit or TrackingEventType.ArrivedAtHub or TrackingEventType.DepartedHub,
                ShipmentStatus.Created or ShipmentStatus.AtHub) => Apply(ShipmentStatus.InTransit, handover),
            (TrackingEventType.HandedToCarrier or TrackingEventType.CarrierInTransit or TrackingEventType.ArrivedAtHub or TrackingEventType.DepartedHub,
                ShipmentStatus.InTransit or ShipmentStatus.ReturningToSender) => LocationOnly(handover),

            (TrackingEventType.OutForDelivery, ShipmentStatus.InTransit or ShipmentStatus.AtHub or ShipmentStatus.DeliveryFailed)
                when state.DeliveryAttemptCount >= state.MaxDeliveryAttempts =>
                new CarrierDecision(CarrierOutcome.PendingReview, CarrierReasons.MaxAttemptsReached),
            (TrackingEventType.OutForDelivery, ShipmentStatus.InTransit or ShipmentStatus.AtHub or ShipmentStatus.DeliveryFailed) =>
                Apply(ShipmentStatus.OutForDelivery, handover),
            (TrackingEventType.OutForDelivery, ShipmentStatus.OutForDelivery) => LocationOnly(handover),

            // T11 (atlamalı) ve T13.
            (TrackingEventType.Delivered, ShipmentStatus.InTransit or ShipmentStatus.OutForDelivery) => Apply(ShipmentStatus.Delivered, handover),
            (TrackingEventType.DeliveryFailed, ShipmentStatus.InTransit or ShipmentStatus.OutForDelivery) => Apply(ShipmentStatus.DeliveryFailed, handover),

            (TrackingEventType.ReturnInitiated, ShipmentStatus.InTransit or ShipmentStatus.DeliveryFailed or ShipmentStatus.OutForDelivery) =>
                Apply(ShipmentStatus.ReturningToSender, handover),
            (TrackingEventType.ReturnInitiated, ShipmentStatus.AtHub) when state.HasDeparted => Apply(ShipmentStatus.ReturningToSender, handover),
            (TrackingEventType.ReturnInitiated, ShipmentStatus.ReturningToSender) => LocationOnly(handover),
            (TrackingEventType.ReturnedToSender, ShipmentStatus.ReturningToSender) => Apply(ShipmentStatus.ReturnedToSender, handover),

            // Tabloda olmayan, ama imzalı ve atanmış carrier'dan gelen yeni olay: insan incelemesi.
            _ => new CarrierDecision(CarrierOutcome.PendingReview, CarrierReasons.TransitionNotAllowed),
        };
    }

    /// <summary>Olayın iddia ettiği durum (terminal karşılaştırması için).</summary>
    public static ShipmentStatus? ClaimedStatus(TrackingEventType type) => type switch
    {
        TrackingEventType.Delivered => ShipmentStatus.Delivered,
        TrackingEventType.ReturnedToSender => ShipmentStatus.ReturnedToSender,
        TrackingEventType.DeliveryFailed => ShipmentStatus.DeliveryFailed,
        TrackingEventType.OutForDelivery => ShipmentStatus.OutForDelivery,
        TrackingEventType.ReturnInitiated => ShipmentStatus.ReturningToSender,
        _ => ShipmentStatus.InTransit,
    };

    /// <summary>Carrier iş anahtarı: kaynak + carrier + olay tipi + konum + deneme.</summary>
    public static string BusinessKey(string carrierCode, TrackingEventType eventType, string? locationCode, short? attempt) =>
        $"{EventSource.Carrier}:{carrierCode}:{eventType}:{locationCode}:{attempt}";

    private static CarrierDecision Apply(ShipmentStatus status, bool handover) =>
        new(CarrierOutcome.Applied, null, status, handover);

    private static CarrierDecision LocationOnly(bool handover) =>
        new(CarrierOutcome.Applied, CarrierReasons.LocationOnly, null, handover);
}

public sealed record CarrierEventNeedsReview(Guid ShipmentId, DateTimeOffset OccurredAt, Guid ReceiptId, string Reason)
    : ShipmentDomainEvent(ShipmentId, OccurredAt);

public sealed record CarrierEventConflicted(Guid ShipmentId, DateTimeOffset OccurredAt, Guid ReceiptId)
    : ShipmentDomainEvent(ShipmentId, OccurredAt);
