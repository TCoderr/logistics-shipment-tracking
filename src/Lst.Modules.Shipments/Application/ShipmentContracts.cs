using Lst.Modules.Shipments.Domain;

namespace Lst.Modules.Shipments.Application;

/// <summary>
/// Shipments modülünün yayınladığı domain olaylarını aynı DbContext ve transaction içinde işleyen dinleyici (ör. Operations istisnaları).
/// Çağrı SaveChanges'tan önce yapılır; dinleyicinin değişiklikleri gönderi değişikliğiyle birlikte commit ya da rollback olur.
/// </summary>
public interface IShipmentEventSink
{
    Task HandleAsync(ShipmentDomainEvent domainEvent, ActorContext actor, CancellationToken cancellationToken);
}

/// <summary>Olayı tetikleyen aktör (audit/istisna açılış kaynağı için).</summary>
public sealed record ActorContext(Guid UserId, string Source);

/// <summary>
/// Başka modüllerin (Operations istisna çözümü) çağırdığı gönderi işlemleri. Çağıranın açık transaction'ı içinde çalışır,
/// gönderi satırını kendisi kilitler; işlem reddedilirse sonuç döner ve çağıran rollback yapar.
/// </summary>
public interface IShipmentOperations
{
    Task<ShipmentOperationResult> GrantExtraAttemptAsync(Guid shipmentId, ActorContext actor, CancellationToken cancellationToken);

    Task<ShipmentOperationResult> StartReturnToSenderAsync(Guid shipmentId, ActorContext actor, CancellationToken cancellationToken);
}

public sealed record ShipmentOperationResult(bool Succeeded, string? Code, string? CurrentStatus)
{
    public static readonly ShipmentOperationResult Ok = new(true, null, null);

    public static ShipmentOperationResult Refused(string code, ShipmentStatus status) => new(false, code, status.ToString());
}

/// <summary>Operations modülünün yayımladığı okuma sözleşmesi: operations.v_open_shipment_exceptions view'ı (keyless).</summary>
public sealed class OpenShipmentExceptionRow
{
    public Guid Id { get; init; }

    public Guid ShipmentId { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public Guid? HubId { get; init; }

    public int OccurrenceCount { get; init; }

    public DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset DueAt { get; init; }

    public Guid? AssignedToUserId { get; init; }
}

/// <summary>Tracking modülünün carrier tablosunun salt-okunur görünümü (kod ve ad).</summary>
public sealed class CarrierRow
{
    public Guid Id { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Tracking modülünün çağırdığı carrier olayı uygulama sözleşmesi. Çağıranın transaction'ında çalışır: gönderiyi kilitler, karar verir,
/// zaman çizelgesi/deneme/domain olaylarını ekler. SaveChanges çağıran tarafından (makbuz sonucuyla birlikte) yapılır.
/// </summary>
public interface IShipmentCarrierEvents
{
    Task<CarrierApplyResult> ApplyAsync(CarrierEventInput input, CancellationToken cancellationToken);

    /// <summary>Aynı event ID farklı içerik: gönderi çözülebiliyorsa çakışma istisnası için olay yayınlar.</summary>
    Task<Guid?> ReportConflictAsync(Guid receiptId, Guid carrierId, string trackingNumber, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record CarrierApplyResult(CarrierOutcome Outcome, string? Reason, Guid? ShipmentId);