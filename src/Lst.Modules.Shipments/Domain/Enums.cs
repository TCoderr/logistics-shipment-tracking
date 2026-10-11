namespace Lst.Modules.Shipments.Domain;

public enum ServiceLevel
{
    Standard,
    Express,
}

public enum ShipmentKind
{
    Forward,
    Return,
}

/// <summary>docs/02 §3.1. Dilim 1 yalnız <see cref="Created"/> yazar; geçişler dilim 4'te.</summary>
public enum ShipmentStatus
{
    Created,
    AtHub,
    InTransit,
    OutForDelivery,
    DeliveryFailed,
    Delivered,
    ReturningToSender,
    ReturnedToSender,
    Cancelled,
}

public enum PartyRole
{
    Sender,
    Recipient,
}

public enum TrackingEventType
{
    Created,
    ArrivedAtHub,
    DepartedHub,
    HandedToCarrier,
    CarrierInTransit,
    OutForDelivery,
    DeliveryFailed,
    Delivered,
    ReturnInitiated,
    ReturnedToSender,
    Cancelled,
}

public enum EventSource
{
    HubScan,
    Operator,
    Carrier,
    System,
}

/// <summary>Zaman çizelgesine yazılan olayların sonucu (docs/02 §5.2). Duplicate/Rejected/Conflict timeline'a yazılmaz.</summary>
public enum TimelineOutcome
{
    Applied,
    Stale,
    PendingReview,
}

public static class ShipmentStatusExtensions
{
    public static bool IsTerminal(this ShipmentStatus status) =>
        status is ShipmentStatus.Delivered or ShipmentStatus.ReturnedToSender or ShipmentStatus.Cancelled;

    /// <summary>docs/02 §3.1: terminal olmayan, iadede olmayan ve söz verilen tarihi geçmiş gönderi.</summary>
    public static bool IsDelayed(this ShipmentStatus status, DateOnly promisedDeliveryDate, DateOnly todayLocal) =>
        !status.IsTerminal() && status != ShipmentStatus.ReturningToSender && promisedDeliveryDate < todayLocal;
}
