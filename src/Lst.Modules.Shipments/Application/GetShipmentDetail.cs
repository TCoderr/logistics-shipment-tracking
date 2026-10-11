using Lst.Modules.Identity;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Shipments.Application;

public sealed record ShipmentDetailResponse(
    Guid Id,
    string TrackingNumber,
    string? ExternalReference,
    string Kind,
    string ServiceLevel,
    string Status,
    DateTime StatusOccurredAt,
    DateTime StatusChangedAt,
    bool IsDelayed,
    DateOnly PromisedDeliveryDate,
    DateTime? DeliveredAt,
    HubSummary OriginHub,
    HubSummary DestinationHub,
    HubSummary? CurrentHub,
    CarrierView? Carrier,
    DeliveryAttemptsSummary DeliveryAttempts,
    DeclaredValueResponse? DeclaredValue,
    PartyView Sender,
    PartyView Recipient,
    IReadOnlyList<PackageView> Packages,
    IReadOnlyList<TimelineItem> Timeline,
    IReadOnlyList<OpenExceptionView> OpenExceptions,
    IReadOnlyList<string> AllowedActions,
    DateTime CreatedAt,
    string? PublicTrackingCode,
    bool PiiRedacted);

public sealed record HubSummary(string Code, string Name, string City);

public sealed record CarrierView(string Code, string Name, DateTime? HandedOverAt);

public sealed record DeliveryAttemptsSummary(int Count, int Max, IReadOnlyList<DeliveryAttemptView> Attempts);

public sealed record DeliveryAttemptView(int AttemptNumber, string Outcome, string? FailureReason, DateTime OccurredAt);

public sealed record OpenExceptionView(Guid Id, string Type, string Severity, string Status, int OccurrenceCount, DateTime OpenedAt, DateTime DueAt, bool IsOverdue, Guid? AssignedToUserId);

public sealed record DeclaredValueResponse(decimal Amount, string Currency);

public sealed record PartyView(
    string FullName,
    string? CompanyName,
    string? Phone,
    string? Email,
    string? AddressLine1,
    string? AddressLine2,
    string? District,
    string City,
    string? PostalCode,
    string CountryCode);

public sealed record PackageView(
    Guid Id,
    int Sequence,
    string LabelCode,
    int WeightGrams,
    IReadOnlyList<int> DimensionsMm,
    string? ContentDescription,
    string? CurrentHubCode,
    string? LastScanType,
    DateTime? LastScannedAt);

public sealed record TimelineItem(
    long Id,
    string EventType,
    string Source,
    string Outcome,
    DateTime OccurredAt,
    DateTime ReceivedAt,
    TimelineLocation? Location,
    string? StatusAfter,
    string? ReasonCode,
    bool IsLate);

public sealed record TimelineLocation(string? HubCode, string? City);

public sealed record ShipmentDetailResult(ShipmentDetailResponse Response, string ETag);

/// <summary>
/// Yetkili detay okuma. Kapsam: OpsAgent/OpsManager tümü; HubOperator yalnız origin/destination/current hub'ı
/// güncel DB atamalarında olan gönderiler. Kapsam dışı ve yok aynı sonucu (null → 404) verir.
/// </summary>
public sealed class GetShipmentDetailHandler(LogisticsDbContext db, PromisedDeliveryCalculator calendar, TimeProvider timeProvider)
{
    public const int MaxDeliveryAttempts = 3;

    public async Task<ShipmentDetailResult?> HandleAsync(Guid shipmentId, ActorAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!access.IsOperations && !access.IsHubOperator)
        {
            return null;
        }

        var shipment = await db.Set<Shipment>()
            .AsNoTracking()
            .Include(s => s.Parties)
            .Include(s => s.Packages)
            .Include(s => s.TrackingEvents)
            .Include(s => s.DeliveryAttempts)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == shipmentId, cancellationToken);
        if (shipment is null)
        {
            return null;
        }

        var hubScoped = !access.IsOperations;
        if (hubScoped && !InHubScope(shipment, access))
        {
            return null;
        }

        var hubIds = new HashSet<Guid> { shipment.OriginHubId, shipment.DestinationHubId };
        if (shipment.CurrentHubId is { } current)
        {
            hubIds.Add(current);
        }

        foreach (var p in shipment.Packages)
        {
            if (p.CurrentHubId is { } ph)
            {
                hubIds.Add(ph);
            }
        }

        foreach (var e in shipment.TrackingEvents)
        {
            if (e.HubId is { } h)
            {
                hubIds.Add(h);
            }
        }

        var hubs = await db.Set<Hub>().AsNoTracking()
            .Where(h => hubIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);

        CarrierView? carrier = null;
        if (shipment.CarrierId is { } carrierId)
        {
            var row = await db.Set<CarrierRow>().AsNoTracking().FirstAsync(x => x.Id == carrierId, cancellationToken);
            carrier = new CarrierView(row.Code, row.Name, shipment.CarrierHandedOverAt?.UtcDateTime);
        }

        var nowUtc = timeProvider.GetUtcNow();
        var today = calendar.TodayLocal(nowUtc);
        var openExceptions = (await db.Set<OpenShipmentExceptionRow>().AsNoTracking()
                .Where(x => x.ShipmentId == shipment.Id)
                .OrderBy(x => x.DueAt)
                .ToListAsync(cancellationToken))
            .Select(x => new OpenExceptionView(x.Id, x.Type, x.Severity, x.Status, x.OccurrenceCount, x.OpenedAt.UtcDateTime, x.DueAt.UtcDateTime,
                x.DueAt < nowUtc, x.AssignedToUserId))
            .ToList();
        var response = new ShipmentDetailResponse(
            shipment.Id,
            shipment.TrackingNumber,
            shipment.ExternalReference,
            shipment.Kind.ToString(),
            shipment.ServiceLevel.ToString(),
            shipment.Status.ToString(),
            shipment.StatusOccurredAt.UtcDateTime,
            shipment.StatusChangedAt.UtcDateTime,
            shipment.Status.IsDelayed(shipment.PromisedDeliveryDate, today),
            shipment.PromisedDeliveryDate,
            shipment.DeliveredAt?.UtcDateTime,
            Summary(hubs[shipment.OriginHubId]),
            Summary(hubs[shipment.DestinationHubId]),
            shipment.CurrentHubId is { } c ? Summary(hubs[c]) : null,
            carrier,
            new DeliveryAttemptsSummary(shipment.DeliveryAttemptCount, shipment.MaxDeliveryAttempts,
                shipment.DeliveryAttempts.OrderBy(a => a.AttemptNumber)
                    .Select(a => new DeliveryAttemptView(a.AttemptNumber, a.Outcome.ToString(), a.FailureReason?.ToString(), a.OccurredAt.UtcDateTime))
                    .ToList()),
            shipment.DeclaredValueAmount is { } amount ? new DeclaredValueResponse(amount, shipment.DeclaredValueCurrency!) : null,
            SenderView(shipment.Sender, hubScoped),
            RecipientView(shipment, access, hubScoped),
            shipment.Packages.OrderBy(p => p.Sequence)
                .Select(p => new PackageView(p.Id, p.Sequence, p.LabelCode, p.WeightGrams, [p.LengthMm, p.WidthMm, p.HeightMm], p.ContentDescription,
                    p.CurrentHubId is { } ph ? hubs[ph].Code : null, p.LastScanType?.ToString(), p.LastScannedAt?.UtcDateTime))
                .ToList(),
            shipment.TrackingEvents.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
                .Select(e => new TimelineItem(
                    e.Id,
                    e.EventType.ToString(),
                    e.Source.ToString(),
                    e.Outcome.ToString(),
                    e.OccurredAt.UtcDateTime,
                    e.ReceivedAt.UtcDateTime,
                    e.HubId is { } h ? new TimelineLocation(hubs[h].Code, hubs[h].City) : null,
                    e.StatusAfter?.ToString(),
                    e.ReasonCode,
                    e.IsLate))
                .ToList(),
            openExceptions,
            AllowedActions: ShipmentActions.For(shipment, access),
            shipment.CreatedAt.UtcDateTime,
            // Public takip kodu yalnız operasyon personeline gösterilir (alıcıyla paylaşım ve yenileme için).
            access.IsOperations ? shipment.PublicTrackingCode : null,
            shipment.PiiRedactedAt is not null);

        return new ShipmentDetailResult(response, ShipmentETag.From(shipment.Version));
    }

    private static bool InHubScope(Shipment shipment, ActorAccess access) =>
        access.Hubs.ContainsKey(shipment.OriginHubId)
        || access.Hubs.ContainsKey(shipment.DestinationHubId)
        || (shipment.CurrentHubId is { } current && access.Hubs.ContainsKey(current));

    private static HubSummary Summary(Hub hub) => new(hub.Code, hub.Name, hub.City);

    /// <summary>docs/06 §2.3: hub görevlisi gönderen için yalnız ad ve şehir görür.</summary>
    private static PartyView SenderView(ShipmentParty sender, bool hubScoped) =>
        hubScoped
            ? new PartyView(sender.FullName, sender.CompanyName, null, null, null, null, null, sender.City, null, sender.CountryCode)
            : Full(sender, sender.PhoneE164);

    /// <summary>
    /// Alıcı telefonu: Ops rolleri tam görür. Hub görevlisi yalnız gönderi varış hub'ı kendi atamasındayken ve
    /// durum OutForDelivery/DeliveryFailed iken tam görür; aksi halde maskelenir (Dilim 1'de Created → maskeli).
    /// </summary>
    private static PartyView RecipientView(Shipment shipment, ActorAccess access, bool hubScoped)
    {
        var recipient = shipment.Recipient;
        if (!hubScoped)
        {
            return Full(recipient, recipient.PhoneE164);
        }

        var fullPhone = shipment.Status is ShipmentStatus.OutForDelivery or ShipmentStatus.DeliveryFailed
            && access.Hubs.ContainsKey(shipment.DestinationHubId);
        var phone = fullPhone || recipient.PhoneE164 is null ? recipient.PhoneE164 : PhoneMasking.Mask(recipient.PhoneE164);
        return new PartyView(
            recipient.FullName, recipient.CompanyName, phone, null,
            recipient.AddressLine1, recipient.AddressLine2, recipient.District, recipient.City, recipient.PostalCode, recipient.CountryCode);
    }

    private static PartyView Full(ShipmentParty p, string? phone) =>
        new(p.FullName, p.CompanyName, phone, p.Email, p.AddressLine1, p.AddressLine2, p.District, p.City, p.PostalCode, p.CountryCode);
}

/// <summary>
/// Kullanıcının o anda yapabileceği işlemler: durum makinesi + güncel DB rolü/kapsamından türetilir. UI butonları buna göre görünür;
/// sunucu her komutu ayrıca yeniden doğrular (gizlemek güvenlik kontrolü değildir).
/// </summary>
public static class ShipmentActions
{
    public const string Cancel = "Cancel";
    public const string ReturnToSender = "ReturnToSender";
    public const string CompleteReturn = "CompleteReturn";
    public const string RecordDeliveryAttempt = "RecordDeliveryAttempt";

    public static IReadOnlyList<string> For(Shipment shipment, ActorAccess access)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        ArgumentNullException.ThrowIfNull(access);
        var actions = new List<string>();
        if (access.IsOperations && shipment.CanCancel)
        {
            actions.Add(Cancel);
        }

        if (access.IsOperations && shipment.ReturnToSenderAllowed)
        {
            actions.Add(ReturnToSender);
        }

        if (access.IsOperations && shipment.Status == ShipmentStatus.ReturningToSender)
        {
            actions.Add(CompleteReturn);
        }

        if (shipment.Status == ShipmentStatus.OutForDelivery
            && (access.IsOperations || (access.IsHubOperator && access.Hubs.ContainsKey(shipment.DestinationHubId))))
        {
            actions.Add(RecordDeliveryAttempt);
        }

        return actions;
    }
}