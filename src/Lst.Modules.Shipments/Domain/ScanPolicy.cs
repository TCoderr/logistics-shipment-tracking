namespace Lst.Modules.Shipments.Domain;

public enum ScanType
{
    Arrival,
    Departure,
    OutForDelivery,
}

public enum DecisionOutcome
{
    Applied,
    Duplicate,
    Stale,
    Rejected,
}

/// <summary>Karar anındaki gönderi özeti (kilit altında okunmuş).</summary>
public sealed record ShipmentSnapshot(
    ShipmentStatus Status,
    DateTimeOffset StatusOccurredAt,
    Guid? CurrentHubId,
    Guid DestinationHubId,
    bool HasDeparted,
    short DeliveryAttemptCount,
    short ExtraAttemptsGranted);

public sealed record PackageSnapshot(Guid Id, Guid? CurrentHubId, ScanType? LastScanType, DateTimeOffset? LastScannedAt);

public sealed record ScanInput(
    Guid PackageId,
    ScanType Type,
    Guid HubId,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Tarama kararı. <see cref="StatusAfter"/> yalnız durum değişiyorsa doludur; konum alanları yalnız <see cref="DecisionOutcome.Applied"/> için anlamlıdır.
/// </summary>
public sealed record ScanDecision(
    DecisionOutcome Outcome,
    string? ReasonCode,
    TrackingEventType EventType,
    ShipmentStatus? StatusAfter = null,
    bool ShipmentHubChanges = false,
    Guid? ShipmentHubAfter = null,
    bool MarksDeparted = false,
    Guid? PackageHubAfter = null)
{
    public bool ChangesStatus => StatusAfter is not null;
}

public static class ScanReasons
{
    public const string FutureTimestamp = "future-timestamp";
    public const string TooOld = "too-old";
    public const string BusinessDuplicate = "business-duplicate";
    public const string PackageStale = "package-stale";
    public const string ShipmentStale = "shipment-stale";
    public const string TerminalState = "terminal-state";
    public const string TransitionNotAllowed = "transition-not-allowed";
    public const string PackagesNotConsolidated = "packages-not-consolidated";
    public const string PackageNotAtHub = "package-not-at-hub";
    public const string NotDestinationHub = "not-destination-hub";
    public const string MaxAttemptsReached = "max-attempts-reached";
    public const string PackageLocationOnly = "PackageLocationOnly";
    public const string MissingDepartureScan = "MissingDepartureScan";
}

/// <summary>
/// Hub taraması karar fonksiyonu (docs/02 §3.2, §3.4, §5.3). Saf: DB ve saat erişimi yoktur.
/// Sıra: saat sapması → iş tekrarı → paket düzeyi stale → terminal koruma → geçiş/ön koşullar.
/// Gönderi düzeyi zaman sırası yalnız durumu değiştirecek taramaya uygulanır; paket-yalnız konum güncellemesi yalnız paket düzeyi stale kuralına tabidir.
/// Konsolidasyon, taranan paketin konumu değişmeden önceki durum üzerinden değerlendirilir.
/// </summary>
public static class ScanPolicy
{
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan BusinessDuplicateWindow = TimeSpan.FromSeconds(120);
    public const int MaxDeliveryAttempts = 3;

    public static TrackingEventType EventTypeFor(ScanType type) => type switch
    {
        ScanType.Arrival => TrackingEventType.ArrivedAtHub,
        ScanType.Departure => TrackingEventType.DepartedHub,
        _ => TrackingEventType.OutForDelivery,
    };

    /// <param name="sameFactOccurrences">Aynı paket + tip + hub için daha önce kaydedilmiş olayların gerçekleşme zamanları.</param>
    public static ScanDecision Decide(
        ShipmentSnapshot shipment,
        IReadOnlyList<PackageSnapshot> packages,
        ScanInput input,
        IEnumerable<DateTimeOffset> sameFactOccurrences)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sameFactOccurrences);

        var eventType = EventTypeFor(input.Type);
        var package = packages.Single(p => p.Id == input.PackageId);

        if (input.OccurredAt > input.ReceivedAt + MaxFutureSkew)
        {
            return Reject(eventType, ScanReasons.FutureTimestamp);
        }

        if (input.OccurredAt < input.ReceivedAt - MaxAge)
        {
            return Reject(eventType, ScanReasons.TooOld);
        }

        if (sameFactOccurrences.Any(t => (t - input.OccurredAt).Duration() <= BusinessDuplicateWindow))
        {
            return new ScanDecision(DecisionOutcome.Duplicate, ScanReasons.BusinessDuplicate, eventType);
        }

        if (package.LastScannedAt is { } lastScan && input.OccurredAt < lastScan)
        {
            return new ScanDecision(DecisionOutcome.Stale, ScanReasons.PackageStale, eventType);
        }

        if (shipment.Status.IsTerminal())
        {
            return input.OccurredAt < shipment.StatusOccurredAt
                ? new ScanDecision(DecisionOutcome.Stale, ScanReasons.ShipmentStale, eventType)
                : Reject(eventType, ScanReasons.TerminalState);
        }

        var candidate = input.Type switch
        {
            ScanType.Arrival => DecideArrival(shipment, input, eventType),
            ScanType.Departure => DecideDeparture(shipment, packages, package, input, eventType),
            _ => DecideOutForDelivery(shipment, packages, package, input, eventType),
        };

        // Durumu değiştirecek olay güncel durumdan eskiyse geçmişe yazılır, durum korunur (körlemesine geri sarma yok).
        if (candidate.Outcome == DecisionOutcome.Applied && candidate.ChangesStatus && input.OccurredAt < shipment.StatusOccurredAt)
        {
            return new ScanDecision(DecisionOutcome.Stale, ScanReasons.ShipmentStale, eventType);
        }

        return candidate;
    }

    private static ScanDecision DecideArrival(ShipmentSnapshot s, ScanInput input, TrackingEventType eventType) => s.Status switch
    {
        // T1, T9, T15
        ShipmentStatus.Created or ShipmentStatus.InTransit or ShipmentStatus.DeliveryFailed =>
            Applied(eventType, ShipmentStatus.AtHub, input.HubId, packageHub: input.HubId),
        ShipmentStatus.AtHub when s.CurrentHubId == input.HubId =>
            LocationOnly(eventType, packageHub: input.HubId),
        // T8: çıkış taraması atlanmış; gönderi yeni hub'a geçer, durum aynı kalır.
        ShipmentStatus.AtHub => new ScanDecision(DecisionOutcome.Applied, ScanReasons.MissingDepartureScan, eventType,
            ShipmentHubChanges: true, ShipmentHubAfter: input.HubId, MarksDeparted: true, PackageHubAfter: input.HubId),
        // T18: iade yolunda konum güncellemesi
        ShipmentStatus.ReturningToSender => new ScanDecision(DecisionOutcome.Applied, null, eventType,
            ShipmentHubChanges: true, ShipmentHubAfter: input.HubId, PackageHubAfter: input.HubId),
        _ => Reject(eventType, ScanReasons.TransitionNotAllowed),
    };

    private static ScanDecision DecideDeparture(
        ShipmentSnapshot s, IReadOnlyList<PackageSnapshot> packages, PackageSnapshot package, ScanInput input, TrackingEventType eventType)
    {
        if (package.CurrentHubId != input.HubId)
        {
            return Reject(eventType, ScanReasons.PackageNotAtHub);
        }

        switch (s.Status)
        {
            case ShipmentStatus.AtHub when s.CurrentHubId == input.HubId:
                // T4: konsolidasyon, bu paket henüz taşınmadan önceki konumlara göre.
                if (packages.Any(p => p.CurrentHubId != input.HubId))
                {
                    return Reject(eventType, ScanReasons.PackagesNotConsolidated);
                }

                return Applied(eventType, ShipmentStatus.InTransit, shipmentHub: null, packageHub: null, marksDeparted: true);
            case ShipmentStatus.InTransit:
                // Aynı gönderinin diğer kolileri: yalnız paket konumu.
                return LocationOnly(eventType, packageHub: null);
            case ShipmentStatus.ReturningToSender when s.CurrentHubId == input.HubId:
                return new ScanDecision(DecisionOutcome.Applied, null, eventType,
                    ShipmentHubChanges: true, ShipmentHubAfter: null, PackageHubAfter: null);
            case ShipmentStatus.ReturningToSender:
                return LocationOnly(eventType, packageHub: null);
            default:
                return Reject(eventType, ScanReasons.TransitionNotAllowed);
        }
    }

    private static ScanDecision DecideOutForDelivery(
        ShipmentSnapshot s, IReadOnlyList<PackageSnapshot> packages, PackageSnapshot package, ScanInput input, TrackingEventType eventType)
    {
        if (input.HubId != s.DestinationHubId)
        {
            return Reject(eventType, ScanReasons.NotDestinationHub);
        }

        if (s.Status == ShipmentStatus.OutForDelivery)
        {
            return package.CurrentHubId == input.HubId
                ? LocationOnly(eventType, packageHub: null)
                : Reject(eventType, ScanReasons.PackageNotAtHub);
        }

        if (s.Status is not (ShipmentStatus.AtHub or ShipmentStatus.DeliveryFailed))
        {
            return Reject(eventType, ScanReasons.TransitionNotAllowed);
        }

        if (s.DeliveryAttemptCount >= MaxDeliveryAttempts + s.ExtraAttemptsGranted)
        {
            return Reject(eventType, ScanReasons.MaxAttemptsReached);
        }

        // T5 / T16: tüm koliler bu hub'da (veya başarısız denemeden sonra hâlâ kuryede) olmalı.
        var everyPackageAvailable = packages.All(p =>
            p.CurrentHubId == input.HubId
            || (s.Status == ShipmentStatus.DeliveryFailed && p.CurrentHubId is null && p.LastScanType == ScanType.OutForDelivery));
        if (!everyPackageAvailable || (package.CurrentHubId != input.HubId && s.Status == ShipmentStatus.AtHub))
        {
            return Reject(eventType, ScanReasons.PackagesNotConsolidated);
        }

        return Applied(eventType, ShipmentStatus.OutForDelivery, shipmentHub: null, packageHub: null);
    }

    private static ScanDecision Applied(
        TrackingEventType eventType, ShipmentStatus statusAfter, Guid? shipmentHub, Guid? packageHub, bool marksDeparted = false) =>
        new(DecisionOutcome.Applied, null, eventType, statusAfter, ShipmentHubChanges: true, ShipmentHubAfter: shipmentHub,
            MarksDeparted: marksDeparted, PackageHubAfter: packageHub);

    private static ScanDecision LocationOnly(TrackingEventType eventType, Guid? packageHub) =>
        new(DecisionOutcome.Applied, ScanReasons.PackageLocationOnly, eventType, PackageHubAfter: packageHub);

    private static ScanDecision Reject(TrackingEventType eventType, string reason) =>
        new(DecisionOutcome.Rejected, reason, eventType);
}
