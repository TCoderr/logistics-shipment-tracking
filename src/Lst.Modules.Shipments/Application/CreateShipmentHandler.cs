using System.Data;
using System.Text.Json;
using Lst.Modules.Shipments.Domain;
using Lst.Modules.Shipments.Infrastructure;
using Lst.Platform.Auditing;
using Lst.Platform.Idempotency;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lst.Modules.Shipments.Application;

public sealed record CreateShipmentCommand(
    Guid ActorUserId,
    string IdempotencyKey,
    ValidCreateShipment Request,
    byte[] RequestHash,
    string? CorrelationId);

public abstract record CreateShipmentOutcome
{
    private CreateShipmentOutcome()
    {
    }

    public sealed record Created(StoredResponse Response) : CreateShipmentOutcome;

    public sealed record Replayed(StoredResponse Response) : CreateShipmentOutcome;

    public sealed record KeyReused : CreateShipmentOutcome;

    public sealed record InProgress : CreateShipmentOutcome;

    public sealed record UnknownHub(IReadOnlyList<string> HubCodes) : CreateShipmentOutcome;

    public sealed record HubInactive(IReadOnlyList<string> HubCodes) : CreateShipmentOutcome;

    /// <summary>COMMIT sırasında bağlantı/sunucu hatası: işlemin kalıcı olup olmadığı bilinmiyor; aynı anahtarla tekrar denenmeli.</summary>
    public sealed record CommitOutcomeUnknown : CreateShipmentOutcome;
}

/// <summary>
/// Gönderi oluşturma. Tek READ COMMITTED transaction: lock_timeout → idempotency claim → hub kontrolü →
/// (savepoint) gönderi + taraflar + paketler + 'Created' olayı + audit → idempotency yanıtı → COMMIT.
/// Tracking/public kod çakışmasında savepoint'e dönülür ve en fazla <see cref="MaxCodeAttempts"/> kez yeniden üretilir.
/// </summary>
public sealed partial class CreateShipmentHandler(
    LogisticsDbContext db,
    IShipmentCodeGenerator codeGenerator,
    PromisedDeliveryCalculator promisedDeliveryCalculator,
    TimeProvider timeProvider,
    ILogger<CreateShipmentHandler> logger)
{
    public const int MaxCodeAttempts = 3;
    public static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(48);
    public const string ResponseContentType = "application/json; charset=utf-8";
    private const string ShipmentSavepoint = "create_shipment";

    public async Task<CreateShipmentOutcome> HandleAsync(CreateShipmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = timeProvider.GetUtcNowMicroseconds();
        var scope = new IdempotencyScope($"user:{command.ActorUserId}", CreateShipmentRequestHash.Endpoint, command.IdempotencyKey);

        // Savepoint'ler bu akışta açıkça yönetilir; EF'in otomatik savepoint'i kapatılır.
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        StoredResponse response;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);

            var claim = await IdempotencyStore.ClaimAsync(
                db, scope, command.RequestHash, CreateShipmentRequestHash.Version, now, IdempotencyRetention, cancellationToken);

            switch (claim)
            {
                case ClaimResult.Replay replay:
                    await transaction.RollbackAsync(cancellationToken);
                    LogReplayed(logger, command.ActorUserId);
                    return new CreateShipmentOutcome.Replayed(replay.Response);
                case ClaimResult.KeyReused:
                    await transaction.RollbackAsync(cancellationToken);
                    return new CreateShipmentOutcome.KeyReused();
                case ClaimResult.Busy:
                    await transaction.RollbackAsync(cancellationToken);
                    return new CreateShipmentOutcome.InProgress();
            }

            var claimed = (ClaimResult.Claimed)claim;
            var request = command.Request;

            var hubCodes = new[] { request.OriginHubCode, request.DestinationHubCode }.Distinct(StringComparer.Ordinal).ToArray();
            var hubs = await db.Set<Hub>().AsNoTracking()
                .Where(h => hubCodes.Contains(h.Code))
                .ToDictionaryAsync(h => h.Code, StringComparer.Ordinal, cancellationToken);

            var unknown = hubCodes.Where(c => !hubs.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreateShipmentOutcome.UnknownHub(unknown);
            }

            var inactive = hubCodes.Where(c => !hubs[c].IsActive).ToList();
            if (inactive.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreateShipmentOutcome.HubInactive(inactive);
            }

            var shipment = await InsertWithCodeRetryAsync(command, hubs, now, transaction, cancellationToken);

            var body = JsonSerializer.SerializeToUtf8Bytes(ToResponse(shipment), ShipmentsJson.Response);
            response = new StoredResponse(
                StatusCode: 201,
                ContentType: ResponseContentType,
                Body: body,
                Location: $"/api/shipments/{shipment.Id}",
                ETag: ShipmentETag.From(shipment.Version));

            await IdempotencyStore.CompleteAsync(db, claimed.RecordId, response, shipment.Id, now, cancellationToken);
        }
        catch (Exception ex) when (PostgresErrors.IsLockTimeout(ex))
        {
            // 55P03 transaction'ı abort eder; aynı transaction'da devam edilmez.
            await transaction.RollbackAsync(CancellationToken.None);
            return new CreateShipmentOutcome.InProgress();
        }

        try
        {
            // Commit başladıktan sonra istemci iptali işlemi geri almaz.
            await transaction.CommitAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Commit belirsizliği bilinçli olarak ayrı sonuçla raporlanır; başarı varsayılmaz.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCommitUncertain(logger, ex.GetType().Name);
            return new CreateShipmentOutcome.CommitOutcomeUnknown();
        }

        LogCreated(logger, command.ActorUserId);
        return new CreateShipmentOutcome.Created(response);
    }

    private async Task<Shipment> InsertWithCodeRetryAsync(
        CreateShipmentCommand command,
        Dictionary<string, Hub> hubs,
        DateTimeOffset now,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var request = command.Request;
        var promised = promisedDeliveryCalculator.Calculate(now, request.ServiceLevel);
        var offsetMinutes = promisedDeliveryCalculator.OffsetMinutes(now);

        for (var attempt = 1; ; attempt++)
        {
            await transaction.CreateSavepointAsync(ShipmentSavepoint, cancellationToken);

            var shipment = Shipment.Create(new NewShipment(
                Guid.CreateVersion7(now),
                codeGenerator.NextTrackingNumber(),
                codeGenerator.NextPublicTrackingCode(),
                request.ServiceLevel,
                request.ExternalReference,
                hubs[request.OriginHubCode].Id,
                hubs[request.DestinationHubCode].Id,
                promised,
                request.Sender,
                request.Recipient,
                request.Packages,
                request.DeclaredValue,
                command.ActorUserId,
                now,
                offsetMinutes));

            db.Add(shipment);
            db.Add(AuditEntry.UserSucceeded(
                now,
                command.ActorUserId,
                ShipmentAuditActions.ShipmentCreated,
                "Shipment",
                shipment.Id.ToString(),
                command.CorrelationId,
                AuditChanges(shipment, request)));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return shipment;
            }
            catch (DbUpdateException ex) when (attempt < MaxCodeAttempts && IsCodeCollision(ex))
            {
                await transaction.RollbackToSavepointAsync(ShipmentSavepoint, cancellationToken);
                db.ChangeTracker.Clear();
                LogCodeCollision(logger, attempt);
            }
        }
    }

    private static bool IsCodeCollision(Exception ex) =>
        PostgresErrors.IsUniqueViolation(ex, ShipmentsModelContributor.TrackingNumberUniqueIndex)
        || PostgresErrors.IsUniqueViolation(ex, ShipmentsModelContributor.PublicTrackingCodeUniqueIndex)
        || PostgresErrors.IsUniqueViolation(ex, "ux_packages_label_code");

    private static CreateShipmentResponse ToResponse(Shipment shipment) =>
        new(
            shipment.Id,
            shipment.TrackingNumber,
            shipment.PublicTrackingCode,
            shipment.Status.ToString(),
            shipment.ServiceLevel.ToString(),
            shipment.PromisedDeliveryDate,
            shipment.Packages.Select(p => new CreatedPackageResponse(p.Id, p.Sequence, p.LabelCode)).ToList(),
            shipment.CreatedAt.UtcDateTime);

    /// <summary>Audit değişiklik alanları allowlist'tir: PII (ad, telefon, adres, e-posta) yazılmaz.</summary>
    private static string AuditChanges(Shipment shipment, ValidCreateShipment request) =>
        JsonSerializer.Serialize(new
        {
            status = shipment.Status.ToString(),
            serviceLevel = shipment.ServiceLevel.ToString(),
            originHubCode = request.OriginHubCode,
            destinationHubCode = request.DestinationHubCode,
            packageCount = shipment.PackageCount,
            promisedDeliveryDate = shipment.PromisedDeliveryDate,
            hasExternalReference = shipment.ExternalReference is not null,
            hasDeclaredValue = shipment.DeclaredValueAmount is not null,
        });

    [LoggerMessage(Level = LogLevel.Information, Message = "Shipment created by user {ActorUserId}")]
    private static partial void LogCreated(ILogger logger, Guid actorUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotent replay served to user {ActorUserId}")]
    private static partial void LogReplayed(ILogger logger, Guid actorUserId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shipment code collision on attempt {Attempt}; regenerating")]
    private static partial void LogCodeCollision(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Commit outcome unknown ({ExceptionType}); client must retry with the same Idempotency-Key")]
    private static partial void LogCommitUncertain(ILogger logger, string exceptionType);
}

public static class ShipmentAuditActions
{
    public const string ShipmentCreated = "ShipmentCreated";
    public const string HubScanRecorded = "HubScanRecorded";
    public const string HubScanRejected = "HubScanRejected";
    public const string ShipmentCancelled = "ShipmentCancelled";
    public const string ReturnToSenderStarted = "ReturnToSenderStarted";
    public const string ReturnCompleted = "ReturnCompleted";
    public const string ExtraAttemptGranted = "ExtraAttemptGranted";
    public const string DeliveryAttemptRecorded = "DeliveryAttemptRecorded";
    public const string CarrierEventRecorded = "CarrierEventRecorded";
    public const string CarrierAssigned = "CarrierAssigned";
    public const string PublicTrackingCodeRotated = "PublicTrackingCodeRotated";
}

public static class ShipmentETag
{
    /// <summary>ETag DB'de kalıcı xmin değerinden üretilir; ilk yanıt, replay ve detay GET aynı değeri gösterir.</summary>
    public static string From(uint version) => $"\"{version}\"";
}
