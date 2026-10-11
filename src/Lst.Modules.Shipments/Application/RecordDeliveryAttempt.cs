using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Idempotency;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Shipments.Application;

public sealed record DeliveryAttemptRequest
{
    public string? Outcome { get; init; }

    public string? FailureReason { get; init; }

    public string? OccurredAt { get; init; }

    public string? Note { get; init; }
}

public sealed record ValidDeliveryAttempt(DeliveryOutcome Outcome, DeliveryFailureReason? FailureReason, DateTimeOffset OccurredAt, string? Note);

public sealed record DeliveryAttemptResponse(int AttemptNumber, string Outcome, string? FailureReason, DateTime OccurredAt, ShipmentCommandResponse Shipment);

public static partial class DeliveryAttemptValidator
{
    public const int MaxNoteLength = 500;

    public static (ValidDeliveryAttempt? Value, Dictionary<string, string[]> Errors) Validate(DeliveryAttemptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        DeliveryOutcome? outcome = request.Outcome switch
        {
            "Delivered" => DeliveryOutcome.Delivered,
            "Failed" => DeliveryOutcome.Failed,
            _ => null,
        };
        if (outcome is null)
        {
            errors["outcome"] = ["'Delivered' veya 'Failed' olmalı."];
        }

        DeliveryFailureReason? reason = null;
        if (request.FailureReason is not null)
        {
            if (Enum.TryParse<DeliveryFailureReason>(request.FailureReason, ignoreCase: false, out var parsed)
                && request.FailureReason == parsed.ToString())
            {
                reason = parsed;
            }
            else
            {
                errors["failureReason"] = ["Geçerli nedenler: " + string.Join(", ", Enum.GetNames<DeliveryFailureReason>()) + "."];
            }
        }

        if (outcome == DeliveryOutcome.Failed && request.FailureReason is null)
        {
            errors["failureReason"] = ["Başarısız denemede neden zorunludur."];
        }

        if (outcome == DeliveryOutcome.Delivered && request.FailureReason is not null)
        {
            errors["failureReason"] = ["Başarılı teslimde neden gönderilmez."];
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note;
        if (note is { Length: > MaxNoteLength })
        {
            errors["note"] = ["En fazla 500 karakter olmalı."];
        }

        if (reason == DeliveryFailureReason.Other && note is null)
        {
            errors["note"] = ["'Other' nedeninde not zorunludur."];
        }

        DateTimeOffset occurredAt = default;
        if (request.OccurredAt is null || !OffsetRegex().IsMatch(request.OccurredAt)
            || !DateTimeOffset.TryParse(request.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out occurredAt))
        {
            errors["occurredAt"] = ["Ofsetli ISO-8601 zaman olmalı."];
        }

        return errors.Count > 0 ? (null, errors) : (new ValidDeliveryAttempt(outcome!.Value, reason, occurredAt, note), errors);
    }

    /// <summary>"lst.delivery-attempt.v1" kanonik istek hash'i (gönderi kimliği dahil; JCS değildir).</summary>
    public static byte[] Hash(Guid shipmentId, ValidDeliveryAttempt request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = JsonSerializer.Serialize(new
        {
            outcome = request.Outcome.ToString(),
            failureReason = request.FailureReason?.ToString(),
            occurredAtUtcTicks = request.OccurredAt.UtcTicks,
            note = request.Note,
        });
        return SHA256.HashData(Encoding.UTF8.GetBytes(
            $"lst.delivery-attempt.v1\n{RecordDeliveryAttemptHandler.Endpoint}\n{shipmentId:D}\n{canonical}"));
    }

    [GeneratedRegex(@"(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();
}

/// <summary>
/// Teslimat denemesi (T13/T14). Kalıcı idempotency (aynı protokol, endpoint + gönderi kapsamlı) ve gönderi kilidi aynı transaction'da;
/// eşzamanlı iki farklı anahtarlı deneme aynı numarayı üretemez (ikincisi kilitten sonra durumu DeliveryFailed/Delivered görür).
/// </summary>
public sealed class RecordDeliveryAttemptHandler(
    LogisticsDbContext db,
    TimeProvider timeProvider,
    ShipmentEventPublisher publisher)
{
    public const string Endpoint = "POST /api/shipments/{id}/delivery-attempts";
    public const short HashVersion = 1;
    public const string NotFound = "not-found";
    public const string Forbidden = "forbidden";

    public async Task<IdempotentCommandOutcome> HandleAsync(
        Guid shipmentId, string idempotencyKey, ValidDeliveryAttempt request, ActorAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);
        var now = timeProvider.GetUtcNowMicroseconds();
        var scope = new IdempotencyScope($"user:{access.UserId}", Endpoint, idempotencyKey);

        // Kaynak yetkisi replay'den ÖNCE (hub alanları değişmez): anahtar kaydı yetki kazandırmaz.
        var hubs = await db.Set<Shipment>().AsNoTracking()
            .Where(s => s.Id == shipmentId)
            .Select(s => new { s.OriginHubId, s.DestinationHubId, s.CurrentHubId })
            .FirstOrDefaultAsync(cancellationToken);
        if (hubs is null)
        {
            return new IdempotentCommandOutcome.Refused(NotFound);
        }

        if (!access.IsOperations && !(access.IsHubOperator && access.Hubs.ContainsKey(hubs.DestinationHubId)))
        {
            var visible = access.IsHubOperator && (access.Hubs.ContainsKey(hubs.OriginHubId) || (hubs.CurrentHubId is { } h && access.Hubs.ContainsKey(h)));
            return new IdempotentCommandOutcome.Refused(visible ? Forbidden : NotFound);
        }

        return await IdempotentCommand.RunAsync(db, scope, DeliveryAttemptValidator.Hash(shipmentId, request), HashVersion, now,
            CreateShipmentHandler.IdempotencyRetention, async ct =>
            {
                if (!await ShipmentCommandSupport.LockShipmentAsync(db, shipmentId, ct))
                {
                    return new IdempotentWorkResult.Refused(NotFound);
                }

                var shipment = await db.Set<Shipment>().Include(s => s.Packages).FirstAsync(s => s.Id == shipmentId, ct);
                var hubAllowed = access.IsHubOperator && access.Hubs.ContainsKey(shipment.DestinationHubId);
                if (!access.IsOperations && !hubAllowed)
                {
                    var inScope = access.Hubs.ContainsKey(shipment.OriginHubId) || (shipment.CurrentHubId is { } c && access.Hubs.ContainsKey(c));
                    return new IdempotentWorkResult.Refused(inScope ? Forbidden : NotFound);
                }

                var violation = DeliveryAttemptPolicy.Validate(shipment.ToSnapshot(), shipment.Packages.Select(p => p.ToSnapshot()).ToList(),
                    request.Outcome, request.FailureReason, request.Note, request.OccurredAt, now);
                if (violation is not null)
                {
                    return new IdempotentWorkResult.Refused(violation);
                }

                var source = access.IsOperations ? EventSource.Operator : EventSource.HubScan;
                var (attempt, events) = shipment.RecordDeliveryAttempt(
                    request.Outcome, request.FailureReason, request.Note, request.OccurredAt, now, source, access.UserId);
                await publisher.PublishAsync(events, new ActorContext(access.UserId, source.ToString()), ct);
                db.Add(AuditEntry.UserSucceeded(now, access.UserId, ShipmentAuditActions.DeliveryAttemptRecorded, "Shipment", shipment.Id.ToString(),
                    Activity.Current?.TraceId.ToString(),
                    JsonSerializer.Serialize(new
                    {
                        attemptNumber = attempt.AttemptNumber,
                        outcome = attempt.Outcome.ToString(),
                        failureReason = attempt.FailureReason?.ToString(),
                        statusAfter = shipment.Status.ToString(),
                        noteProvided = attempt.Note is not null,
                    })));
                await db.SaveChangesAsync(ct);

                var body = JsonSerializer.SerializeToUtf8Bytes(new DeliveryAttemptResponse(
                    attempt.AttemptNumber, attempt.Outcome.ToString(), attempt.FailureReason?.ToString(), attempt.OccurredAt.UtcDateTime,
                    new ShipmentCommandResponse(shipment.Id, shipment.TrackingNumber, shipment.Status.ToString(), shipment.StatusChangedAt.UtcDateTime)),
                    ShipmentsJson.Response);
                return new IdempotentWorkResult.Success(
                    new StoredResponse(201, CreateShipmentHandler.ResponseContentType, body,
                        string.Create(CultureInfo.InvariantCulture, $"/api/shipments/{shipment.Id}/delivery-attempts/{attempt.AttemptNumber}"),
                        ShipmentETag.From(shipment.Version)),
                    attempt.Id);
            }, cancellationToken);
    }
}
