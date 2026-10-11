namespace Lst.Modules.Tracking.Domain;

/// <summary>tracking.carriers. Webhook secret'ları burada değil, secret store/konfigürasyondadır.</summary>
public sealed class Carrier
{
    private Carrier()
    {
    }

    public Carrier(Guid id, string code, string name, bool sendsSequence)
    {
        Id = id;
        Code = code;
        Name = name;
        SendsSequence = sendsSequence;
        IsActive = true;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public bool SendsSequence { get; private set; }
}

public enum ReceiptProcessingStatus
{
    Received,
    Processing,
    Processed,
    DeadLettered,
}

/// <summary>
/// Carrier inbox makbuzu (tracking.carrier_event_receipts). İmzası doğrulanmış her olay kalıcı yazıldıktan sonra 202 döner.
/// <see cref="PayloadSha256"/> normalize alanların kanonik hash'idir (çakışma tespiti); <see cref="RawSha256"/> ve <see cref="RawBody"/>
/// imzalı orijinal byte'lardır (jsonb ham byte kanıtı değildir). Lease alanları <see cref="LockToken"/> ile fencing sağlar.
/// </summary>
public sealed class CarrierEventReceipt
{
    private CarrierEventReceipt()
    {
    }

    public Guid Id { get; private set; }

    public Guid CarrierId { get; private set; }

    public string ExternalEventId { get; private set; } = string.Empty;

    public byte[] PayloadSha256 { get; private set; } = [];

    public byte[] RawSha256 { get; private set; } = [];

    public byte[]? RawBody { get; private set; }

    public string? Payload { get; private set; }

    public DateTimeOffset? PayloadPurgedAt { get; private set; }

    public DateTimeOffset SignatureTimestamp { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public string? TrackingNumber { get; private set; }

    public Guid? ShipmentId { get; private set; }

    public string? EventType { get; private set; }

    public DateTimeOffset? OccurredAt { get; private set; }

    public long? CarrierSequence { get; private set; }

    public ReceiptProcessingStatus ProcessingStatus { get; private set; }

    public string? Outcome { get; private set; }

    public string? OutcomeReason { get; private set; }

    public short Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public Guid? LockToken { get; private set; }

    public string? LockedBy { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }
}

/// <summary>Aynı event ID farklı içerik kanıtı (tracking.carrier_event_conflicts). İlk makbuz değişmez.</summary>
public sealed class CarrierEventConflict
{
    private CarrierEventConflict()
    {
    }

    public CarrierEventConflict(Guid id, Guid receiptId, DateTimeOffset receivedAt, byte[] payloadSha256, byte[] rawSha256, byte[] rawBody, DateTimeOffset signatureTimestamp)
    {
        Id = id;
        ReceiptId = receiptId;
        ReceivedAt = receivedAt;
        PayloadSha256 = payloadSha256;
        RawSha256 = rawSha256;
        RawBody = rawBody;
        SignatureTimestamp = signatureTimestamp;
    }

    public Guid Id { get; private set; }

    public Guid ReceiptId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public byte[] PayloadSha256 { get; private set; } = [];

    public byte[] RawSha256 { get; private set; } = [];

    public byte[]? RawBody { get; private set; }

    public DateTimeOffset? PayloadPurgedAt { get; private set; }

    public DateTimeOffset SignatureTimestamp { get; private set; }
}
