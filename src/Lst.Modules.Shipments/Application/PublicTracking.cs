using System.Diagnostics;
using System.Text.Json;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Shipments.Application;

/// <summary>
/// Public takip yanıtı (docs/05 §6). Alan allowlist'idir ve entity'den otomatik eşlenmez: ad, telefon, e-posta, adres satırı, ilçe, posta kodu,
/// harici referans, hub kodu/adı, carrier facility kodu, not, istisna, kullanıcı ve uygulanmamış olaylar asla yer almaz. Contract testi alan kümesini sabitler.
/// </summary>
public sealed record PublicTrackingView(
    string TrackingNumberMasked,
    string Status,
    string StatusLabel,
    DateOnly PromisedDeliveryDate,
    bool IsDelayed,
    int PackageCount,
    DateTime LastUpdatedAt,
    string DestinationCity,
    IReadOnlyList<PublicTimelineItem> Timeline);

public sealed record PublicTimelineItem(DateTime OccurredAt, string Label, string? City);

public static class PublicTrackingCodes
{
    /// <summary>128 bit rastgele değerin Crockford Base32 gösterimi: tam 26 karakter, büyük harf.</summary>
    public static bool IsWellFormed(string? code) =>
        code is { Length: 26 } && code.All(c => CrockfordBase32.Alphabet.Contains(c, StringComparison.Ordinal));
}

public sealed class PublicTrackingHandler(LogisticsDbContext db, PromisedDeliveryCalculator calendar, TimeProvider timeProvider)
{
    private static readonly Dictionary<ShipmentStatus, string> StatusLabels = new()
    {
        [ShipmentStatus.Created] = "Kayıt oluşturuldu",
        [ShipmentStatus.AtHub] = "Aktarma merkezinde",
        [ShipmentStatus.InTransit] = "Yolda",
        [ShipmentStatus.OutForDelivery] = "Dağıtımda",
        [ShipmentStatus.DeliveryFailed] = "Teslim edilemedi",
        [ShipmentStatus.Delivered] = "Teslim edildi",
        [ShipmentStatus.ReturningToSender] = "Göndericiye iade ediliyor",
        [ShipmentStatus.ReturnedToSender] = "Göndericiye iade edildi",
        [ShipmentStatus.Cancelled] = "İptal edildi",
    };

    private static readonly Dictionary<TrackingEventType, string> EventLabels = new()
    {
        [TrackingEventType.Created] = "Gönderi kaydı oluşturuldu",
        [TrackingEventType.ArrivedAtHub] = "Aktarma merkezine ulaştı",
        [TrackingEventType.DepartedHub] = "Aktarma merkezinden çıktı",
        [TrackingEventType.HandedToCarrier] = "Taşıyıcı firmaya teslim edildi",
        [TrackingEventType.CarrierInTransit] = "Yolda",
        [TrackingEventType.OutForDelivery] = "Dağıtıma çıktı",
        // Başarısızlık nedeni (adreste yok, reddetti…) public görünümde yer almaz.
        [TrackingEventType.DeliveryFailed] = "Teslim edilemedi, yeniden denenecek",
        [TrackingEventType.Delivered] = "Teslim edildi",
        [TrackingEventType.ReturnInitiated] = "Göndericiye iade ediliyor",
        [TrackingEventType.ReturnedToSender] = "Göndericiye iade edildi",
        [TrackingEventType.Cancelled] = "Gönderi iptal edildi",
    };

    /// <summary>Geçersiz biçim ve bulunamayan kod aynı sonucu (null) verir.</summary>
    public async Task<PublicTrackingView?> HandleAsync(string? code, CancellationToken cancellationToken)
    {
        if (!PublicTrackingCodes.IsWellFormed(code))
        {
            return null;
        }

        var shipment = await db.Set<Shipment>().AsNoTracking()
            .Where(s => s.PublicTrackingCode == code)
            .Select(s => new
            {
                s.Id,
                s.TrackingNumber,
                s.Status,
                s.PromisedDeliveryDate,
                s.PackageCount,
                s.StatusChangedAt,
                City = s.Parties.Where(p => p.Role == PartyRole.Recipient).Select(p => p.City).First(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (shipment is null)
        {
            return null;
        }

        var events = await db.Set<TrackingEvent>().AsNoTracking()
            .Where(e => e.ShipmentId == shipment.Id && e.Outcome == TimelineOutcome.Applied)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .Select(e => new { e.EventType, e.OccurredAt, e.HubId, e.LocationCity })
            .ToListAsync(cancellationToken);
        var hubIds = events.Where(e => e.HubId is not null).Select(e => e.HubId!.Value).Distinct().ToList();
        var hubCities = await db.Set<Hub>().AsNoTracking().Where(h => hubIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.City, cancellationToken);

        var timeline = new List<PublicTimelineItem>();
        foreach (var e in events)
        {
            var label = EventLabels[e.EventType];
            var city = e.HubId is { } hub ? hubCities[hub] : e.LocationCity;
            // Çok paketli gönderide aynı hub'a her paketin varışı ayrı olaydır; public görünümde tek satır yeterlidir.
            if (timeline.Count > 0 && timeline[^1].Label == label && timeline[^1].City == city)
            {
                continue;
            }

            timeline.Add(new PublicTimelineItem(e.OccurredAt.UtcDateTime, label, city));
        }

        var today = calendar.TodayLocal(timeProvider.GetUtcNow());
        var lastUpdated = events.Count > 0 ? events.Max(e => e.OccurredAt) : shipment.StatusChangedAt;
        return new PublicTrackingView(
            MaskTrackingNumber(shipment.TrackingNumber),
            shipment.Status.ToString(),
            StatusLabels[shipment.Status],
            shipment.PromisedDeliveryDate,
            shipment.Status.IsDelayed(shipment.PromisedDeliveryDate, today),
            shipment.PackageCount,
            lastUpdated.UtcDateTime,
            shipment.City,
            timeline);
    }

    /// <summary>LS4829105735 → LS48******35.</summary>
    public static string MaskTrackingNumber(string trackingNumber)
    {
        ArgumentNullException.ThrowIfNull(trackingNumber);
        return trackingNumber.Length <= 6
            ? new string('*', trackingNumber.Length)
            : string.Concat(trackingNumber.AsSpan(0, 4), new string('*', trackingNumber.Length - 6), trackingNumber.AsSpan(trackingNumber.Length - 2));
    }
}

public abstract record RotateCodeOutcome
{
    private RotateCodeOutcome()
    {
    }

    public sealed record Succeeded(Guid ShipmentId, string PublicTrackingCode, string ETag) : RotateCodeOutcome;

    public sealed record PreconditionFailed(string CurrentETag) : RotateCodeOutcome;

    public sealed record NotFound : RotateCodeOutcome;
}

/// <summary>Public kod yenileme (yalnız OpsManager, If-Match). Eski kod aynı transaction'da geçersiz olur; audit kod değerlerini içermez.</summary>
public sealed class RotatePublicTrackingCodeHandler(LogisticsDbContext db, IShipmentCodeGenerator codes, TimeProvider timeProvider)
{
    public async Task<RotateCodeOutcome> HandleAsync(Guid shipmentId, string ifMatch, Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNowMicroseconds();
        await using var transaction = await ShipmentCommandSupport.BeginAsync(db, cancellationToken);
        if (!await ShipmentCommandSupport.LockShipmentAsync(db, shipmentId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RotateCodeOutcome.NotFound();
        }

        var shipment = await db.Set<Shipment>().FirstAsync(s => s.Id == shipmentId, cancellationToken);
        var current = ShipmentETag.From(shipment.Version);
        if (!string.Equals(current, ifMatch, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RotateCodeOutcome.PreconditionFailed(current);
        }

        shipment.RotatePublicTrackingCode(codes.NextPublicTrackingCode(), now);
        db.Add(AuditEntry.UserSucceeded(now, userId, ShipmentAuditActions.PublicTrackingCodeRotated, "Shipment", shipment.Id.ToString(),
            Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(new { publicTrackingCode = "changed" })));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return new RotateCodeOutcome.Succeeded(shipment.Id, shipment.PublicTrackingCode, ShipmentETag.From(shipment.Version));
    }
}
