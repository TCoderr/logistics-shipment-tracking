using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lst.Modules.Shipments.Application;

public sealed record HubScanRequest
{
    public string? ClientScanId { get; init; }

    public string? LabelCode { get; init; }

    public string? ScanType { get; init; }

    public string? OccurredAt { get; init; }
}

public sealed record ValidHubScan(string ClientScanId, string LabelCode, ScanType ScanType, DateTimeOffset OccurredAt);

public sealed record ScanShipmentView(Guid Id, string TrackingNumber, string Status, string? CurrentHubCode);

public sealed record ScanPackageView(string LabelCode, string? CurrentHubCode, string? LastScanType);

public sealed record ConsolidationView(int PackagesAtHub, int PackageCount);

/// <summary>Tarama yanıtı: yalnız operasyonel alanlar; alıcı/gönderen bilgisi içermez.</summary>
public sealed record HubScanResponse(
    string Outcome,
    string? ReasonCode,
    ScanShipmentView Shipment,
    ScanPackageView Package,
    ConsolidationView Consolidation);

public abstract record HubScanOutcome
{
    private HubScanOutcome()
    {
    }

    public sealed record Recorded(HubScanResponse Response) : HubScanOutcome;

    public sealed record Rejected(HubScanResponse Response) : HubScanOutcome;

    public sealed record HubNotFound : HubScanOutcome;

    public sealed record HubInactive : HubScanOutcome;

    public sealed record Forbidden : HubScanOutcome;

    public sealed record LabelNotFound : HubScanOutcome;

    public sealed record ClientScanIdConflict : HubScanOutcome;

    public sealed record Busy : HubScanOutcome;
}

public static partial class HubScanValidator
{
    public static (ValidHubScan? Value, Dictionary<string, string[]> Errors) Validate(HubScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.ClientScanId is null || !ClientScanIdRegex().IsMatch(request.ClientScanId))
        {
            errors["clientScanId"] = ["1–64 karakter; harf, rakam ve '-', '_', '.', ':' olmalı."];
        }

        if (request.LabelCode is null || !LabelRegex().IsMatch(request.LabelCode))
        {
            errors["labelCode"] = ["Koli etiketi 'LS0000000000-01' biçiminde olmalı."];
        }

        ScanType? type = request.ScanType switch
        {
            "Arrival" => Domain.ScanType.Arrival,
            "Departure" => Domain.ScanType.Departure,
            "OutForDelivery" => Domain.ScanType.OutForDelivery,
            _ => null,
        };
        if (type is null)
        {
            errors["scanType"] = ["'Arrival', 'Departure' veya 'OutForDelivery' olmalı."];
        }

        DateTimeOffset occurredAt = default;
        if (request.OccurredAt is null
            || !OffsetRegex().IsMatch(request.OccurredAt)
            || !DateTimeOffset.TryParse(request.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out occurredAt))
        {
            errors["occurredAt"] = ["Ofsetli ISO-8601 zaman olmalı (ör. 2026-10-09T19:02:00+03:00)."];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new ValidHubScan(request.ClientScanId!, request.LabelCode!, type!.Value, occurredAt), errors);
    }

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientScanIdRegex();

    [GeneratedRegex("^LS[0-9]{10}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();
}

/// <summary>
/// Hub taraması (docs/02 §3.4, §5.3). Gönderi satırı kilitlenir; teknik tekrar (hub + clientScanId), iş tekrarı (paket+tip+hub ±120 sn),
/// karar ve yazım aynı kilit altındadır. Rejected kararında paket/gönderi yazılmaz, yalnız audit kaydı tutulur.
/// </summary>
public sealed partial class RecordHubScanHandler(
    LogisticsDbContext db,
    TimeProvider timeProvider,
    ShipmentEventPublisher publisher,
    ILogger<RecordHubScanHandler> logger)
{
    public async Task<HubScanOutcome> HandleAsync(string hubCode, ValidHubScan scan, ActorAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(access);

        var hub = await db.Set<Hub>().AsNoTracking().FirstOrDefaultAsync(h => h.Code == hubCode, cancellationToken);
        if (hub is null)
        {
            return new HubScanOutcome.HubNotFound();
        }

        // Yetki, etiket aranmadan önce: atanmamış hub'da etiketin varlığı da sızmaz.
        if (!access.IsHubOperator || !access.Hubs.ContainsKey(hub.Id))
        {
            return new HubScanOutcome.Forbidden();
        }

        if (!hub.IsActive)
        {
            return new HubScanOutcome.HubInactive();
        }

        var now = timeProvider.GetUtcNowMicroseconds();
        await using var transaction = await ShipmentCommandSupport.BeginAsync(db, cancellationToken);
        try
        {
            var target = await db.Set<Package>().AsNoTracking()
                .Where(p => p.LabelCode == scan.LabelCode)
                .Select(p => new { p.Id, p.ShipmentId })
                .FirstOrDefaultAsync(cancellationToken);
            if (target is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new HubScanOutcome.LabelNotFound();
            }

            await ShipmentCommandSupport.LockShipmentAsync(db, target.ShipmentId, cancellationToken);
            var shipment = await db.Set<Shipment>()
                .Include(s => s.Packages)
                .FirstAsync(s => s.Id == target.ShipmentId, cancellationToken);

            var eventType = ScanPolicy.EventTypeFor(scan.ScanType);
            var technical = await db.Set<TrackingEvent>().AsNoTracking()
                .Where(e => e.HubId == hub.Id && e.ClientScanId == scan.ClientScanId)
                .Select(e => new { e.PackageId, e.EventType })
                .FirstOrDefaultAsync(cancellationToken);
            if (technical is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return technical.PackageId == target.Id && technical.EventType == eventType
                    ? new HubScanOutcome.Recorded(View(shipment, target.Id, hub.Id, "Duplicate", "technical-duplicate", await HubCodesAsync(cancellationToken)))
                    : new HubScanOutcome.ClientScanIdConflict();
            }

            var businessKey = TrackingEvent.ScanBusinessKey(hub.Code, eventType, target.Id);
            var windowStart = scan.OccurredAt - ScanPolicy.BusinessDuplicateWindow;
            var windowEnd = scan.OccurredAt + ScanPolicy.BusinessDuplicateWindow;
            var sameFact = await db.Set<TrackingEvent>().AsNoTracking()
                .Where(e => e.ShipmentId == shipment.Id && e.BusinessKey == businessKey && e.OccurredAt >= windowStart && e.OccurredAt <= windowEnd)
                .Select(e => e.OccurredAt)
                .ToListAsync(cancellationToken);

            var input = new ScanInput(target.Id, scan.ScanType, hub.Id, scan.OccurredAt, now);
            var decision = ScanPolicy.Decide(
                shipment.ToSnapshot(),
                shipment.Packages.Select(p => p.ToSnapshot()).ToList(),
                input,
                sameFact);

            var hubCodes = await HubCodesAsync(cancellationToken);
            var correlationId = Activity.Current?.TraceId.ToString();
            switch (decision.Outcome)
            {
                case DecisionOutcome.Duplicate:
                    await transaction.RollbackAsync(cancellationToken);
                    return new HubScanOutcome.Recorded(View(shipment, target.Id, hub.Id, "Duplicate", decision.ReasonCode, hubCodes));

                case DecisionOutcome.Rejected:
                    if (decision.ReasonCode == ScanReasons.PackagesNotConsolidated)
                    {
                        // Paket/gönderi yazılmaz; yalnız Operations istisnası (PackageSplit) aynı transaction'da açılır/güncellenir.
                        await publisher.PublishAsync([new ConsolidationRejected(shipment.Id, scan.OccurredAt, hub.Id)],
                            new ActorContext(access.UserId, nameof(EventSource.HubScan)), cancellationToken);
                    }

                    db.Add(AuditEntry.UserRejected(now, access.UserId, ShipmentAuditActions.HubScanRejected, "Shipment",
                        shipment.Id.ToString(), decision.ReasonCode!, correlationId, AuditChanges(decision, shipment.Status, null, hub.Code, scan)));
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(CancellationToken.None);
                    return new HubScanOutcome.Rejected(View(shipment, target.Id, hub.Id, "Rejected", decision.ReasonCode, hubCodes));

                default:
                    var before = shipment.Status;
                    shipment.ApplyScan(decision, input, new ScanContext(hub.Code, hub.City, scan.ClientScanId, access.UserId));
                    db.Add(AuditEntry.UserSucceeded(now, access.UserId, ShipmentAuditActions.HubScanRecorded, "Shipment",
                        shipment.Id.ToString(), correlationId, AuditChanges(decision, before, shipment.Status, hub.Code, scan)));
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(CancellationToken.None);
                    LogScan(logger, decision.Outcome, eventType, shipment.Id);
                    return new HubScanOutcome.Recorded(View(shipment, target.Id, hub.Id, decision.Outcome.ToString(), decision.ReasonCode, hubCodes));
            }
        }
        catch (Exception ex) when (PostgresErrors.IsLockTimeout(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new HubScanOutcome.Busy();
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, "ux_tracking_events_hub_client_scan"))
        {
            // Aynı clientScanId başka bir gönderinin taramasıyla eşzamanlı kullanıldı (farklı kilitler).
            await transaction.RollbackAsync(CancellationToken.None);
            return new HubScanOutcome.ClientScanIdConflict();
        }
    }

    private async Task<Dictionary<Guid, string>> HubCodesAsync(CancellationToken cancellationToken) =>
        await db.Set<Hub>().AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, cancellationToken);

    private static HubScanResponse View(Shipment shipment, Guid packageId, Guid hubId, string outcome, string? reason, Dictionary<Guid, string> hubs)
    {
        var package = shipment.Packages.Single(p => p.Id == packageId);
        return new HubScanResponse(
            outcome,
            reason,
            new ScanShipmentView(shipment.Id, shipment.TrackingNumber, shipment.Status.ToString(),
                shipment.CurrentHubId is { } c ? hubs[c] : null),
            new ScanPackageView(package.LabelCode, package.CurrentHubId is { } p ? hubs[p] : null, package.LastScanType?.ToString()),
            new ConsolidationView(shipment.Packages.Count(x => x.CurrentHubId == hubId), shipment.PackageCount));
    }

    private static string AuditChanges(ScanDecision decision, ShipmentStatus before, ShipmentStatus? after, string hubCode, ValidHubScan scan) =>
        JsonSerializer.Serialize(new
        {
            outcome = decision.Outcome.ToString(),
            eventType = decision.EventType.ToString(),
            hubCode,
            labelCode = scan.LabelCode,
            statusBefore = before.ToString(),
            statusAfter = after?.ToString(),
            reasonCode = decision.ReasonCode,
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub scan {Outcome} {EventType} for shipment {ShipmentId}")]
    private static partial void LogScan(ILogger logger, DecisionOutcome outcome, TrackingEventType eventType, Guid shipmentId);
}
