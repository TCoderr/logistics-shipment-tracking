namespace Lst.Modules.Shipments.Domain;

public sealed record PartySnapshot(
    string FullName,
    string? CompanyName,
    string PhoneE164,
    string? Email,
    string AddressLine1,
    string? AddressLine2,
    string? District,
    string City,
    string? PostalCode,
    string CountryCode);

public sealed record PackageSpec(int WeightGrams, int LengthMm, int WidthMm, int HeightMm, string? ContentDescription);

public sealed record DeclaredValue(decimal Amount, string Currency);

public sealed record NewShipment(
    Guid Id,
    string TrackingNumber,
    string PublicTrackingCode,
    ServiceLevel ServiceLevel,
    string? ExternalReference,
    Guid OriginHubId,
    Guid DestinationHubId,
    DateOnly PromisedDeliveryDate,
    PartySnapshot Sender,
    PartySnapshot Recipient,
    IReadOnlyList<PackageSpec> Packages,
    DeclaredValue? DeclaredValue,
    Guid CreatedByUserId,
    DateTimeOffset Now,
    short BusinessOffsetMinutes);

/// <summary>Gönderi aggregate'i (docs/02 §2). Dilim 1 yalnız oluşturmayı içerir; durum geçişleri dilim 4'te eklenir.</summary>
public sealed class Shipment
{
    public const int MaxPackages = 20;

    private readonly List<Package> _packages = [];
    private readonly List<ShipmentParty> _parties = [];
    private readonly List<TrackingEvent> _trackingEvents = [];
    private readonly List<DeliveryAttempt> _deliveryAttempts = [];

    private Shipment()
    {
    }

    public Guid Id { get; private set; }

    public string TrackingNumber { get; private set; } = string.Empty;

    public string PublicTrackingCode { get; private set; } = string.Empty;

    public ShipmentKind Kind { get; private set; }

    public string? ExternalReference { get; private set; }

    public ServiceLevel ServiceLevel { get; private set; }

    public ShipmentStatus Status { get; private set; }

    public DateTimeOffset StatusOccurredAt { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    public Guid OriginHubId { get; private set; }

    public Guid DestinationHubId { get; private set; }

    public Guid? CurrentHubId { get; private set; }

    public short PackageCount { get; private set; }

    public DateOnly PromisedDeliveryDate { get; private set; }

    public short DeliveryAttemptCount { get; private set; }

    /// <summary>Yönetici onayıyla verilen ek teslimat denemesi hakkı (dilim 5).</summary>
    public short ExtraAttemptsGranted { get; private set; }

    /// <summary>İlk çıkış taraması (veya hub değişimi) gerçekleşti; bundan sonra iptal yerine göndericiye iade kullanılır.</summary>
    public bool HasDeparted { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Son mil carrier'ı (dilim 6). Atanmamışsa gönderi kendi ağımızda.</summary>
    public Guid? CarrierId { get; private set; }

    /// <summary>Carrier'ın gönderiyi fiilen teslim aldığı an (ilk uygulanan carrier olayı); sonrasında iptal yok.</summary>
    public DateTimeOffset? CarrierHandedOverAt { get; private set; }

    /// <summary>Atanmış carrier'dan uygulanan en büyük olay sıra numarası (docs/02 §5.3 kural 5).</summary>
    public long? LastCarrierSequence { get; private set; }

    public decimal? DeclaredValueAmount { get; private set; }

    public string? DeclaredValueCurrency { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>PostgreSQL xmin; optimistic concurrency token ve ETag kaynağı.</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<Package> Packages => _packages;

    public IReadOnlyList<ShipmentParty> Parties => _parties;

    public IReadOnlyList<TrackingEvent> TrackingEvents => _trackingEvents;

    public IReadOnlyList<DeliveryAttempt> DeliveryAttempts => _deliveryAttempts;

    /// <summary>Teslim anı (gerçekleşme zamanı); yalnız Delivered durumunda dolu.</summary>
    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>Saklama işi kişisel verileri maskelediğinde dolar (docs/04 §6).</summary>
    public DateTimeOffset? PiiRedactedAt { get; private set; }

    /// <summary>Public takip kodunu yeniler; eski kod hemen geçersizdir (docs/06 §3).</summary>
    public void RotatePublicTrackingCode(string newCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newCode);
        if (string.Equals(newCode, PublicTrackingCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Yeni kod eskisiyle aynı olamaz.");
        }

        PublicTrackingCode = newCode;
        UpdatedAt = now;
    }

    public ShipmentParty Sender => _parties.Single(p => p.Role == PartyRole.Sender);

    public ShipmentParty Recipient => _parties.Single(p => p.Role == PartyRole.Recipient);

    public static Shipment Create(NewShipment input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TrackingNumbers.IsValid(input.TrackingNumber))
        {
            throw new ArgumentException("Geçersiz tracking numarası.", nameof(input));
        }

        if (input.Packages.Count is < 1 or > MaxPackages)
        {
            throw new ArgumentException("Gönderi 1–20 paket içermelidir.", nameof(input));
        }

        var shipment = new Shipment
        {
            Id = input.Id,
            TrackingNumber = input.TrackingNumber,
            PublicTrackingCode = input.PublicTrackingCode,
            Kind = ShipmentKind.Forward,
            ExternalReference = input.ExternalReference,
            ServiceLevel = input.ServiceLevel,
            Status = ShipmentStatus.Created,
            StatusOccurredAt = input.Now,
            StatusChangedAt = input.Now,
            OriginHubId = input.OriginHubId,
            DestinationHubId = input.DestinationHubId,
            PackageCount = checked((short)input.Packages.Count),
            PromisedDeliveryDate = input.PromisedDeliveryDate,
            DeliveryAttemptCount = 0,
            DeclaredValueAmount = input.DeclaredValue?.Amount,
            DeclaredValueCurrency = input.DeclaredValue?.Currency,
            CreatedAt = input.Now,
            CreatedByUserId = input.CreatedByUserId,
            UpdatedAt = input.Now,
        };

        shipment._parties.Add(ShipmentParty.Create(shipment.Id, PartyRole.Sender, input.Sender));
        shipment._parties.Add(ShipmentParty.Create(shipment.Id, PartyRole.Recipient, input.Recipient));

        for (var i = 0; i < input.Packages.Count; i++)
        {
            shipment._packages.Add(Package.Create(
                Guid.CreateVersion7(input.Now),
                shipment.Id,
                checked((short)(i + 1)),
                input.TrackingNumber,
                input.Packages[i]));
        }

        shipment._trackingEvents.Add(TrackingEvent.ShipmentCreated(
            shipment.Id, input.Now, input.BusinessOffsetMinutes, input.CreatedByUserId));

        return shipment;
    }

    /// <summary>docs/02 §3.3: iptal yalnız fiziksel hareket başlamadan (Created veya çıkış yapmamış AtHub).</summary>
    public bool CanCancel =>
        CarrierHandedOverAt is null && (Status == ShipmentStatus.Created || (Status == ShipmentStatus.AtHub && !HasDeparted));

    /// <summary>Carrier ataması yalnız fiziksel hareket ve devir başlamadan.</summary>
    public bool CanAssignCarrier =>
        CarrierHandedOverAt is null && (Status == ShipmentStatus.Created || (Status == ShipmentStatus.AtHub && !HasDeparted));

    public void AssignCarrier(Guid carrierId, DateTimeOffset now)
    {
        if (!CanAssignCarrier)
        {
            throw new InvalidOperationException("Carrier bu durumda atanamaz.");
        }

        CarrierId = carrierId;
        UpdatedAt = now;
    }

    public CarrierShipmentState ToCarrierState() =>
        new(Status, StatusOccurredAt, CarrierId, LastCarrierSequence, CarrierHandedOverAt is not null, HasDeparted, DeliveryAttemptCount, MaxDeliveryAttempts);

    /// <summary>
    /// Applied / Stale / PendingReview carrier kararını uygular (Duplicate ve Rejected zaman çizelgesine yazılmaz).
    /// Teslim ve başarısız deneme carrier kaynağıyla numaralı deneme olarak da kaydedilir. Domain olayları döner.
    /// </summary>
    public IReadOnlyList<ShipmentDomainEvent> ApplyCarrierEvent(CarrierDecision decision, CarrierEventInput input, short offsetMinutes)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(input);
        if (decision.Outcome is not (CarrierOutcome.Applied or CarrierOutcome.Stale or CarrierOutcome.PendingReview))
        {
            throw new InvalidOperationException("Yalnız Applied, Stale veya PendingReview uygulanır.");
        }

        var before = Status;
        var events = new List<ShipmentDomainEvent>();
        UpdatedAt = input.ReceivedAt;
        var trackingEvent = TrackingEvent.FromCarrier(Id, decision, input, before);
        _trackingEvents.Add(trackingEvent);

        if (decision.Outcome == CarrierOutcome.PendingReview)
        {
            events.Add(new CarrierEventNeedsReview(Id, input.OccurredAt, input.ReceiptId, decision.Reason ?? CarrierReasons.TransitionNotAllowed));
            return events;
        }

        if (decision.Outcome == CarrierOutcome.Stale)
        {
            return events;
        }

        if (decision.MarksHandover)
        {
            CarrierHandedOverAt = input.OccurredAt;
        }

        if (input.Sequence is { } sequence && (LastCarrierSequence is null || sequence > LastCarrierSequence))
        {
            LastCarrierSequence = sequence;
        }

        if (decision.StatusAfter is not { } after)
        {
            return events;
        }

        if (before is ShipmentStatus.Created or ShipmentStatus.AtHub)
        {
            HasDeparted = true;
            CurrentHubId = null;
        }

        Status = after;
        StatusOccurredAt = input.OccurredAt;
        StatusChangedAt = input.ReceivedAt;
        if (after is ShipmentStatus.Delivered or ShipmentStatus.DeliveryFailed)
        {
            var number = checked((short)(DeliveryAttemptCount + 1));
            DeliveryAttemptCount = number;
            var outcome = after == ShipmentStatus.Delivered ? DeliveryOutcome.Delivered : DeliveryOutcome.Failed;
            var reason = outcome == DeliveryOutcome.Failed ? input.FailureReason ?? DeliveryFailureReason.Other : (DeliveryFailureReason?)null;
            _deliveryAttempts.Add(DeliveryAttempt.CreateFromCarrier(Id, number, input.OccurredAt, outcome, reason, trackingEvent,
                outcome == DeliveryOutcome.Failed && reason == DeliveryFailureReason.Other ? "Carrier tarafından sınıflandırılmamış neden" : null));
            if (outcome == DeliveryOutcome.Delivered)
            {
                DeliveredAt = input.OccurredAt;
                events.Add(new ShipmentReachedTerminal(Id, input.OccurredAt, Status));
            }
            else
            {
                events.Add(new DeliveryAttemptFailed(Id, input.OccurredAt, number, reason!.Value, DestinationHubId, number >= MaxDeliveryAttempts));
            }
        }
        else if (after == ShipmentStatus.ReturnedToSender)
        {
            CurrentHubId = null;
            events.Add(new ShipmentReachedTerminal(Id, input.OccurredAt, Status));
        }

        return events;
    }

    /// <summary>Çıkış sonrası durdurma talebi iptal değil göndericiye iadedir (T7, T12, T17).</summary>
    public bool ReturnToSenderAllowed =>
        Status is ShipmentStatus.InTransit or ShipmentStatus.DeliveryFailed
        || (Status == ShipmentStatus.AtHub && (HasDeparted || DeliveryAttemptCount > 0));

    public ShipmentSnapshot ToSnapshot() =>
        new(Status, StatusOccurredAt, CurrentHubId, DestinationHubId, HasDeparted, DeliveryAttemptCount, ExtraAttemptsGranted);

    /// <summary>
    /// Applied veya Stale tarama kararını uygular. Stale yalnız geçmişe yazılır; durum/konum değişmez.
    /// Her durumda gönderi "touch" edilir (UpdatedAt) ki ETag/xmin zaman çizelgesi değişimini yansıtsın.
    /// </summary>
    public TrackingEvent ApplyScan(ScanDecision decision, ScanInput input, ScanContext context)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (decision.Outcome is not (DecisionOutcome.Applied or DecisionOutcome.Stale))
        {
            throw new InvalidOperationException("Yalnız Applied veya Stale karar uygulanır.");
        }

        var package = _packages.Single(p => p.Id == input.PackageId);
        var before = Status;
        if (decision.Outcome == DecisionOutcome.Applied)
        {
            if (decision.StatusAfter is { } after)
            {
                Status = after;
                StatusOccurredAt = input.OccurredAt;
                StatusChangedAt = input.ReceivedAt;
            }

            if (decision.ShipmentHubChanges)
            {
                CurrentHubId = decision.ShipmentHubAfter;
            }

            if (decision.MarksDeparted)
            {
                HasDeparted = true;
            }

            package.RecordScan(input.Type, decision.PackageHubAfter, input.OccurredAt);
        }

        UpdatedAt = input.ReceivedAt;
        var trackingEvent = TrackingEvent.FromScan(Id, decision, input, before, context);
        _trackingEvents.Add(trackingEvent);
        return trackingEvent;
    }

    /// <summary>Kalan hakla birlikte izin verilen toplam deneme sayısı.</summary>
    public int MaxDeliveryAttempts => ScanPolicy.MaxDeliveryAttempts + ExtraAttemptsGranted;

    /// <summary>
    /// T13/T14: Delivered → terminal (tüm koliler), Failed → DeliveryFailed. Deneme numarası gönderi içinde artar; kilit altında çağrılır.
    /// Önce <see cref="DeliveryAttemptPolicy.Validate"/> çağrılmış olmalıdır.
    /// </summary>
    public (DeliveryAttempt Attempt, IReadOnlyList<ShipmentDomainEvent> Events) RecordDeliveryAttempt(
        DeliveryOutcome outcome, DeliveryFailureReason? reason, string? note, DateTimeOffset occurredAt, DateTimeOffset now,
        EventSource source, Guid actorUserId)
    {
        var violation = DeliveryAttemptPolicy.Validate(ToSnapshot(), _packages.Select(p => p.ToSnapshot()).ToList(), outcome, reason, note, occurredAt, now);
        if (violation is not null)
        {
            throw new InvalidOperationException($"Teslimat denemesi kaydedilemez: {violation}");
        }

        var before = Status;
        var number = checked((short)(DeliveryAttemptCount + 1));
        DeliveryAttemptCount = number;
        Status = outcome == DeliveryOutcome.Delivered ? ShipmentStatus.Delivered : ShipmentStatus.DeliveryFailed;
        StatusOccurredAt = occurredAt;
        StatusChangedAt = now;
        UpdatedAt = now;
        if (outcome == DeliveryOutcome.Delivered)
        {
            DeliveredAt = occurredAt;
            CurrentHubId = null;
        }

        var trackingEvent = TrackingEvent.DeliveryAttemptRecorded(Id, outcome, reason, number, before, Status, occurredAt, now, source, actorUserId);
        _trackingEvents.Add(trackingEvent);
        var attempt = DeliveryAttempt.Create(Id, number, occurredAt, outcome, reason, source, trackingEvent, actorUserId, note);
        _deliveryAttempts.Add(attempt);

        IReadOnlyList<ShipmentDomainEvent> events = outcome == DeliveryOutcome.Delivered
            ? [new ShipmentReachedTerminal(Id, occurredAt, Status)]
            : [new DeliveryAttemptFailed(Id, occurredAt, number, reason!.Value, DestinationHubId, number >= MaxDeliveryAttempts)];
        return (attempt, events);
    }

    /// <summary>MaxAttemptsReached istisnası ReattemptApproved ile çözülünce bir ek deneme hakkı.</summary>
    public void GrantExtraAttempt(DateTimeOffset now)
    {
        if (Status.IsTerminal())
        {
            throw new InvalidOperationException("Terminal gönderiye ek deneme verilemez.");
        }

        ExtraAttemptsGranted = checked((short)(ExtraAttemptsGranted + 1));
        UpdatedAt = now;
    }

    /// <summary>T7, T12, T17: çıkış sonrası durdurma veya başarısız teslim sonrası göndericiye iade (aynı gönderi lifecycle'ı).</summary>
    public TrackingEvent StartReturnToSender(Guid actorUserId, DateTimeOffset now, short offsetMinutes)
    {
        if (!ReturnToSenderAllowed)
        {
            throw new InvalidOperationException("Göndericiye iade bu durumda başlatılamaz.");
        }

        var before = Status;
        Status = ShipmentStatus.ReturningToSender;
        StatusOccurredAt = now;
        StatusChangedAt = now;
        UpdatedAt = now;
        var trackingEvent = TrackingEvent.OperatorStatusChange(
            Id, TrackingEventType.ReturnInitiated, before, Status, now, offsetMinutes, actorUserId, reasonCode: "OperatorReturnToSender");
        _trackingEvents.Add(trackingEvent);
        return trackingEvent;
    }

    /// <summary>T19: göndericiye teslim (terminal).</summary>
    public (TrackingEvent Event, ShipmentDomainEvent Terminal) CompleteReturn(Guid actorUserId, DateTimeOffset now, short offsetMinutes)
    {
        if (Status != ShipmentStatus.ReturningToSender)
        {
            throw new InvalidOperationException("Yalnız iade yolundaki gönderi tamamlanabilir.");
        }

        var before = Status;
        Status = ShipmentStatus.ReturnedToSender;
        StatusOccurredAt = now;
        StatusChangedAt = now;
        CurrentHubId = null;
        UpdatedAt = now;
        var trackingEvent = TrackingEvent.OperatorStatusChange(
            Id, TrackingEventType.ReturnedToSender, before, Status, now, offsetMinutes, actorUserId, reasonCode: "OperatorReturnCompleted");
        _trackingEvents.Add(trackingEvent);
        return (trackingEvent, new ShipmentReachedTerminal(Id, now, Status));
    }

    public TrackingEvent Cancel(string reason, Guid actorUserId, DateTimeOffset now, short offsetMinutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!CanCancel)
        {
            throw new InvalidOperationException("Gönderi bu durumda iptal edilemez.");
        }

        var before = Status;
        Status = ShipmentStatus.Cancelled;
        StatusOccurredAt = now;
        StatusChangedAt = now;
        CancellationReason = reason;
        UpdatedAt = now;
        var trackingEvent = TrackingEvent.OperatorStatusChange(
            Id, TrackingEventType.Cancelled, before, ShipmentStatus.Cancelled, now, offsetMinutes, actorUserId, reasonCode: "OperatorCancelled");
        _trackingEvents.Add(trackingEvent);
        return trackingEvent;
    }
}

/// <summary>Taramanın yapıldığı hub ve aktör bilgisi (zaman çizelgesi alanları).</summary>
public sealed record ScanContext(string HubCode, string HubCity, string ClientScanId, Guid ActorUserId);

public sealed class Package
{
    private Package()
    {
    }

    public Guid Id { get; private set; }

    public Guid ShipmentId { get; private set; }

    public short Sequence { get; private set; }

    public string LabelCode { get; private set; } = string.Empty;

    public int WeightGrams { get; private set; }

    public int LengthMm { get; private set; }

    public int WidthMm { get; private set; }

    public int HeightMm { get; private set; }

    public string? ContentDescription { get; private set; }

    /// <summary>Paketin bulunduğu hub; çıkış/dağıtım taramasından sonra NULL (yolda/kuryede).</summary>
    public Guid? CurrentHubId { get; private set; }

    public ScanType? LastScanType { get; private set; }

    public DateTimeOffset? LastScannedAt { get; private set; }

    public PackageSnapshot ToSnapshot() => new(Id, CurrentHubId, LastScanType, LastScannedAt);

    internal void RecordScan(ScanType type, Guid? hubAfter, DateTimeOffset occurredAt)
    {
        CurrentHubId = hubAfter;
        LastScanType = type;
        LastScannedAt = occurredAt;
    }

    internal static Package Create(Guid id, Guid shipmentId, short sequence, string trackingNumber, PackageSpec spec) =>
        new()
        {
            Id = id,
            ShipmentId = shipmentId,
            Sequence = sequence,
            LabelCode = TrackingNumbers.LabelCode(trackingNumber, sequence),
            WeightGrams = spec.WeightGrams,
            LengthMm = spec.LengthMm,
            WidthMm = spec.WidthMm,
            HeightMm = spec.HeightMm,
            ContentDescription = spec.ContentDescription,
        };
}

/// <summary>Gönderen/alıcı adres ve iletişim snapshot'ı (PII). Oluşturmadan sonra v1'de değişmez.</summary>
public sealed class ShipmentParty
{
    private ShipmentParty()
    {
    }

    public Guid ShipmentId { get; private set; }

    public PartyRole Role { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public string? CompanyName { get; private set; }

    /// <summary>Saklama süresi sonunda NULL (PII maskeleme, Dilim 8).</summary>
    public string? PhoneE164 { get; private set; }

    public string? PhoneLast4 { get; private set; }

    public string? Email { get; private set; }

    public string? AddressLine1 { get; private set; }

    public string? AddressLine2 { get; private set; }

    public string? District { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string? PostalCode { get; private set; }

    public string CountryCode { get; private set; } = "TR";

    internal static ShipmentParty Create(Guid shipmentId, PartyRole role, PartySnapshot snapshot) =>
        new()
        {
            ShipmentId = shipmentId,
            Role = role,
            FullName = snapshot.FullName,
            CompanyName = snapshot.CompanyName,
            PhoneE164 = snapshot.PhoneE164,
            PhoneLast4 = PhoneMasking.Last4(snapshot.PhoneE164),
            Email = snapshot.Email,
            AddressLine1 = snapshot.AddressLine1,
            AddressLine2 = snapshot.AddressLine2,
            District = snapshot.District,
            City = snapshot.City,
            PostalCode = snapshot.PostalCode,
            CountryCode = snapshot.CountryCode,
        };
}

/// <summary>Append-only zaman çizelgesi olayı (shipments.tracking_events, docs/04 §3.5).</summary>
public sealed class TrackingEvent
{
    private TrackingEvent()
    {
    }

    public long Id { get; private set; }

    public Guid ShipmentId { get; private set; }

    public TrackingEventType EventType { get; private set; }

    public EventSource Source { get; private set; }

    public TimelineOutcome Outcome { get; private set; }

    public bool IsLate { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public short OccurredAtOffsetMinutes { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Yalnız 'Created' olayında NULL (önceki durum yok).</summary>
    public ShipmentStatus? StatusBefore { get; private set; }

    public ShipmentStatus? StatusAfter { get; private set; }

    public Guid? HubId { get; private set; }

    public string? LocationCode { get; private set; }

    public string? LocationCity { get; private set; }

    public Guid? PackageId { get; private set; }

    public short? DeliveryAttemptNumber { get; private set; }

    public string? ReasonCode { get; private set; }

    public Guid? ActorUserId { get; private set; }

    public Guid? CarrierEventReceiptId { get; private set; }

    public long? CarrierSequence { get; private set; }

    public string? ClientScanId { get; private set; }

    public string BusinessKey { get; private set; } = string.Empty;

    public string? InternalNote { get; private set; }

    /// <summary>Hub taraması iş anahtarı: kaynak + hub + olay tipi + paket (çok paketli taramalar birbirinin tekrarı sayılmaz).</summary>
    public static string ScanBusinessKey(string hubCode, TrackingEventType eventType, Guid packageId) =>
        $"{EventSource.HubScan}:{hubCode}:{eventType}:{packageId:N}";

    internal static TrackingEvent FromScan(Guid shipmentId, ScanDecision decision, ScanInput input, ShipmentStatus statusBefore, ScanContext context) =>
        new()
        {
            ShipmentId = shipmentId,
            EventType = decision.EventType,
            Source = EventSource.HubScan,
            Outcome = decision.Outcome == DecisionOutcome.Stale ? TimelineOutcome.Stale : TimelineOutcome.Applied,
            IsLate = decision.Outcome == DecisionOutcome.Stale,
            OccurredAt = input.OccurredAt.ToUniversalTime(),
            OccurredAtOffsetMinutes = checked((short)input.OccurredAt.Offset.TotalMinutes),
            ReceivedAt = input.ReceivedAt,
            StatusBefore = statusBefore,
            StatusAfter = decision.Outcome == DecisionOutcome.Applied ? decision.StatusAfter : null,
            HubId = input.HubId,
            LocationCode = context.HubCode,
            LocationCity = context.HubCity,
            PackageId = input.PackageId,
            ReasonCode = decision.ReasonCode,
            ActorUserId = context.ActorUserId,
            ClientScanId = context.ClientScanId,
            BusinessKey = ScanBusinessKey(context.HubCode, decision.EventType, input.PackageId),
        };

    internal static TrackingEvent FromCarrier(Guid shipmentId, CarrierDecision decision, CarrierEventInput input, ShipmentStatus before) =>
        new()
        {
            ShipmentId = shipmentId,
            EventType = input.EventType,
            Source = EventSource.Carrier,
            Outcome = decision.Outcome switch
            {
                CarrierOutcome.Stale => TimelineOutcome.Stale,
                CarrierOutcome.PendingReview => TimelineOutcome.PendingReview,
                _ => TimelineOutcome.Applied,
            },
            IsLate = decision.Outcome == CarrierOutcome.Stale,
            OccurredAt = input.OccurredAt.ToUniversalTime(),
            OccurredAtOffsetMinutes = checked((short)input.OccurredAt.Offset.TotalMinutes),
            ReceivedAt = input.ReceivedAt,
            StatusBefore = before,
            StatusAfter = decision.Outcome == CarrierOutcome.Applied ? decision.StatusAfter : null,
            LocationCode = input.LocationCode,
            LocationCity = input.LocationCity,
            DeliveryAttemptNumber = input.AttemptNumber,
            ReasonCode = decision.Reason ?? input.FailureReason?.ToString(),
            CarrierEventReceiptId = input.ReceiptId,
            CarrierSequence = input.Sequence,
            BusinessKey = CarrierEventPolicy.BusinessKey(input.CarrierCode, input.EventType, input.LocationCode, input.AttemptNumber),
        };

    internal static TrackingEvent DeliveryAttemptRecorded(
        Guid shipmentId,
        DeliveryOutcome outcome,
        DeliveryFailureReason? reason,
        short attemptNumber,
        ShipmentStatus before,
        ShipmentStatus after,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        EventSource source,
        Guid actorUserId)
    {
        var eventType = outcome == DeliveryOutcome.Delivered ? TrackingEventType.Delivered : TrackingEventType.DeliveryFailed;
        return new TrackingEvent
        {
            ShipmentId = shipmentId,
            EventType = eventType,
            Source = source,
            Outcome = TimelineOutcome.Applied,
            OccurredAt = occurredAt.ToUniversalTime(),
            OccurredAtOffsetMinutes = checked((short)occurredAt.Offset.TotalMinutes),
            ReceivedAt = receivedAt,
            StatusBefore = before,
            StatusAfter = after,
            DeliveryAttemptNumber = attemptNumber,
            ReasonCode = reason?.ToString(),
            ActorUserId = actorUserId,
            BusinessKey = $"{source}:{shipmentId:N}:{eventType}::{attemptNumber}",
        };
    }

    internal static TrackingEvent OperatorStatusChange(
        Guid shipmentId,
        TrackingEventType eventType,
        ShipmentStatus before,
        ShipmentStatus after,
        DateTimeOffset now,
        short offsetMinutes,
        Guid actorUserId,
        string? reasonCode) =>
        new()
        {
            ShipmentId = shipmentId,
            EventType = eventType,
            Source = EventSource.Operator,
            Outcome = TimelineOutcome.Applied,
            OccurredAt = now,
            OccurredAtOffsetMinutes = offsetMinutes,
            ReceivedAt = now,
            StatusBefore = before,
            StatusAfter = after,
            ReasonCode = reasonCode,
            ActorUserId = actorUserId,
            BusinessKey = $"{EventSource.Operator}:{actorUserId:N}:{eventType}::",
        };

    internal static TrackingEvent ShipmentCreated(Guid shipmentId, DateTimeOffset now, short offsetMinutes, Guid actorUserId) =>
        new()
        {
            ShipmentId = shipmentId,
            EventType = TrackingEventType.Created,
            Source = EventSource.Operator,
            Outcome = TimelineOutcome.Applied,
            IsLate = false,
            OccurredAt = now,
            OccurredAtOffsetMinutes = offsetMinutes,
            ReceivedAt = now,
            StatusBefore = null,
            StatusAfter = ShipmentStatus.Created,
            ActorUserId = actorUserId,
            BusinessKey = $"{EventSource.Operator}:{actorUserId:N}:{TrackingEventType.Created}::",
        };
}
