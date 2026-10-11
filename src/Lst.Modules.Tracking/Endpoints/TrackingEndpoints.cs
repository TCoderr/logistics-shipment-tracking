using System.Text.Json;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Modules.Shipments.Endpoints;
using Lst.Modules.Tracking.Application;
using Lst.Modules.Tracking.Domain;
using Lst.Platform.Http;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lst.Modules.Tracking.Endpoints;

public sealed record AssignCarrierRequest
{
    public string? CarrierCode { get; init; }
}

public sealed record CarrierReceiptView(
    Guid Id,
    string CarrierCode,
    string ExternalEventId,
    string? EventType,
    DateTime? OccurredAt,
    DateTime ReceivedAt,
    string ProcessingStatus,
    string? Outcome,
    string? OutcomeReason,
    int Attempts,
    DateTime? ProcessedAt,
    string? LastError,
    int ConflictCount);

/// <summary>Carrier referans verisi: operatörün carrier atama seçimi için.</summary>
public sealed record CarrierReferenceItem(string Code, string Name, bool IsActive);

public sealed record CarrierReferenceResponse(IReadOnlyList<CarrierReferenceItem> Items);

internal static partial class TrackingEndpoints
{
    public const string WebhookRateLimitPolicy = "carrier-webhook";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/carrier-webhooks/{carrierCode}", ReceiveWebhookAsync)
            .AllowAnonymous()
            .RequireRateLimiting(WebhookRateLimitPolicy)
            .WithTags("Carrier webhooks")
            .WithName("ReceiveCarrierWebhook")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        var shipments = app.MapGroup("/api/shipments").WithTags("Shipments");
        shipments.MapPost("/{id:guid}/carrier", AssignCarrierAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .RequireCsrf()
            .WithName("AssignCarrier")
            .Accepts<AssignCarrierRequest>("application/json")
            .Produces<ShipmentCommandResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        shipments.MapGet("/{id:guid}/carrier-receipts", ListShipmentReceiptsAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .WithName("ListShipmentCarrierReceipts")
            .Produces<IReadOnlyList<CarrierReceiptView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/reference/carriers", ListCarriersAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .WithTags("Reference")
            .WithName("ListCarriers")
            .Produces<CarrierReferenceResponse>();

        app.MapGet("/api/carrier-receipts", ListAttentionReceiptsAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .WithTags("Carrier webhooks")
            .WithName("ListCarrierReceiptsNeedingAttention")
            .Produces<IReadOnlyList<CarrierReceiptView>>()
            .ProducesValidationProblem();
    }

    private static async Task<IResult> ReceiveWebhookAsync(
        string carrierCode,
        HttpContext httpContext,
        LogisticsDbContext db,
        IOptions<CarrierOptions> options,
        TimeProvider timeProvider,
        CarrierReceiptWriter handler,
        ILoggerFactory loggerFactory)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var logger = loggerFactory.CreateLogger("Lst.Tracking.Webhook");
        if (!JsonContent.IsJson(httpContext.Request.ContentType))
        {
            return Problems.Create(StatusCodes.Status415UnsupportedMediaType, "unsupported-media-type", "İçerik tipi desteklenmiyor",
                "İstek gövdesi application/json olmalı.");
        }

        var body = await JsonContent.ReadBodyAsync(httpContext.Request, CarrierWebhookParser.MaxBodyBytes, httpContext.RequestAborted);
        if (body is null)
        {
            return Problems.Create(StatusCodes.Status413PayloadTooLarge, "payload-too-large", "İstek gövdesi çok büyük",
                "Webhook gövdesi en fazla 64 KB olabilir.");
        }

        try
        {
            var carrier = await db.Set<Carrier>().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Code == carrierCode && c.IsActive, httpContext.RequestAborted);
            var secrets = carrier is not null && options.Value.Secrets.TryGetValue(carrier.Code, out var configured)
                ? configured.Values.Where(s => s.Length >= 32).ToArray()
                : [];
            // Bilinmeyen carrier, secret'ı olmayan carrier ve hatalı imza aynı yanıtı alır (neden ayrıntısı yok).
            if (carrier is null || secrets.Length == 0
                || !CarrierWebhookParser.VerifySignature(httpContext.Request.Headers[CarrierWebhookParser.SignatureHeader], body, secrets,
                    timeProvider.GetUtcNow(), options.Value.SignatureToleranceSeconds, out var signedAt))
            {
                LogRejected(logger, "signature");
                return InvalidSignature();
            }

            CarrierWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<CarrierWebhookPayload>(body, CarrierWebhookParser.Options);
            }
            catch (JsonException ex)
            {
                return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path] = ["Gövde geçerli JSON değil, yinelenen alan, aşırı derinlik veya yanlış tipte değer içeriyor."],
                });
            }

            if (payload is null)
            {
                return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["$"] = ["Gövde zorunludur."] });
            }

            // Provider binding: gövdedeki carrier, route ve imzayı doğrulayan secret'ın sahibiyle aynı olmalı.
            if (!string.Equals(payload.CarrierCode, carrier.Code, StringComparison.Ordinal))
            {
                LogRejected(logger, "carrier-binding");
                return InvalidSignature();
            }

            var (normalized, errors) = CarrierWebhookParser.Normalize(payload);
            if (normalized is null)
            {
                return Problems.Validation(errors);
            }

            var outcome = await handler.HandleAsync(carrier, normalized, body, signedAt, httpContext.RequestAborted);
            return outcome switch
            {
                ReceiveOutcome.Received received => TypedResults.Json(new { receiptId = received.ReceiptId, status = "Received" },
                    statusCode: StatusCodes.Status202Accepted),
                ReceiveOutcome.Duplicate duplicate => TypedResults.Ok(new { receiptId = duplicate.ReceiptId, status = "Duplicate" }),
                ReceiveOutcome.Conflict conflict => Problems.Create(StatusCodes.Status409Conflict, "carrier-event-id-conflict",
                    "Aynı event ID farklı içerikle gönderildi", "İlk olay değiştirilmedi; olay operasyon incelemesine alındı. Tekrar denemeyin.",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["receiptId"] = conflict.ReceiptId }),
                _ => throw new InvalidOperationException("Unknown receive outcome."),
            };
        }
        catch (Exception ex) when (CarrierInboxProcessor.IsTransient(ex) && !httpContext.RequestAborted.IsCancellationRequested)
        {
            // Kalıcılık sağlanamadı: ack verilmez, carrier yeniden dener.
            LogRejected(logger, "storage-unavailable");
            httpContext.Response.Headers.RetryAfter = "5";
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, "service-unavailable", "Olay şu an kaydedilemedi",
                "Kısa süre sonra aynı gövdeyle tekrar deneyin.");
        }
    }

    private static async Task<IResult> AssignCarrierAsync(
        Guid id, HttpContext httpContext, IActorAccessReader accessReader, ShipmentOperatorCommands commands, LogisticsDbContext db)
    {
        if (ShipmentCommandEndpoints.RequireIfMatch(httpContext, out var ifMatch) is { } missing)
        {
            return missing;
        }

        var (body, error) = await ShipmentCommandSupport.ReadStrictJsonAsync<AssignCarrierRequest>(
            httpContext.Request, ShipmentCommandEndpoints.MaxCommandBodyBytes, httpContext.RequestAborted);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(body!.CarrierCode) || body.CarrierCode.Length > 16)
        {
            return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["carrierCode"] = ["Carrier kodu zorunludur."] });
        }

        var carrier = await db.Set<Carrier>().AsNoTracking().FirstOrDefaultAsync(c => c.Code == body.CarrierCode, httpContext.RequestAborted);
        if (carrier is null || !carrier.IsActive)
        {
            return Problems.Create(StatusCodes.Status422UnprocessableEntity, carrier is null ? "unknown-carrier" : "carrier-inactive", "Carrier geçersiz");
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        return ShipmentCommandEndpoints.MapOperatorOutcome(httpContext,
            await commands.AssignCarrierAsync(id, ifMatch, carrier.Id, carrier.Code, access, httpContext.RequestAborted));
    }

    private static async Task<IResult> ListShipmentReceiptsAsync(Guid id, HttpContext httpContext, LogisticsDbContext db)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var shipment = await db.Set<Shipment>().AsNoTracking()
            .Where(s => s.Id == id).Select(s => new { s.TrackingNumber, s.CarrierId }).FirstOrDefaultAsync(httpContext.RequestAborted);
        if (shipment is null)
        {
            return Problems.Create(StatusCodes.Status404NotFound, "not-found", "Gönderi bulunamadı", "Gönderi bulunamadı veya erişiminiz yok.");
        }

        // Henüz işlenmemiş makbuzlar gönderiye bağlanmamıştır; atanmış carrier'ın aynı tracking numaralı makbuzları da gösterilir.
        var receipts = db.Set<CarrierEventReceipt>().AsNoTracking()
            .Where(r => r.ShipmentId == id
                || (r.ShipmentId == null && r.TrackingNumber == shipment.TrackingNumber && r.CarrierId == shipment.CarrierId));
        return TypedResults.Ok(await ProjectAsync(db, receipts, httpContext.RequestAborted));
    }

    private static async Task<IResult> ListCarriersAsync(HttpContext httpContext, LogisticsDbContext db)
    {
        var items = await db.Set<Carrier>().AsNoTracking()
            .OrderBy(c => c.Code)
            .Select(c => new CarrierReferenceItem(c.Code, c.Name, c.IsActive))
            .ToListAsync(httpContext.RequestAborted);
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new CarrierReferenceResponse(items));
    }

    private static async Task<IResult> ListAttentionReceiptsAsync(string? status, HttpContext httpContext, LogisticsDbContext db)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        IQueryable<CarrierEventReceipt> receipts = db.Set<CarrierEventReceipt>().AsNoTracking();
        switch (status ?? "DeadLettered")
        {
            case "DeadLettered":
                receipts = receipts.Where(r => r.ProcessingStatus == ReceiptProcessingStatus.DeadLettered);
                break;
            case "Conflicted":
                receipts = receipts.Where(r => db.Set<CarrierEventConflict>().Any(c => c.ReceiptId == r.Id));
                break;
            case "PendingReview":
                receipts = receipts.Where(r => r.Outcome == nameof(CarrierOutcome.PendingReview));
                break;
            default:
                return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["status"] = ["Desteklenen değerler: DeadLettered, Conflicted, PendingReview."],
                });
        }

        return TypedResults.Ok(await ProjectAsync(db, receipts, httpContext.RequestAborted));
    }

    private static async Task<List<CarrierReceiptView>> ProjectAsync(LogisticsDbContext db, IQueryable<CarrierEventReceipt> receipts, CancellationToken cancellationToken)
    {
        var rows = await receipts
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id)
            .Take(100)
            .Join(db.Set<Carrier>(), r => r.CarrierId, c => c.Id, (r, c) => new
            {
                r.Id,
                c.Code,
                r.ExternalEventId,
                r.EventType,
                r.OccurredAt,
                r.ReceivedAt,
                r.ProcessingStatus,
                r.Outcome,
                r.OutcomeReason,
                r.Attempts,
                r.ProcessedAt,
                r.LastError,
                Conflicts = db.Set<CarrierEventConflict>().Count(x => x.ReceiptId == r.Id),
            })
            .ToListAsync(cancellationToken);
        return rows
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id)
            .Select(r => new CarrierReceiptView(r.Id, r.Code, r.ExternalEventId, r.EventType, r.OccurredAt?.UtcDateTime, r.ReceivedAt.UtcDateTime,
                r.ProcessingStatus.ToString(), r.Outcome, r.OutcomeReason, r.Attempts, r.ProcessedAt?.UtcDateTime, r.LastError, r.Conflicts))
            .ToList();
    }

    private static ProblemHttpResult InvalidSignature() =>
        Problems.Create(StatusCodes.Status401Unauthorized, "invalid-signature", "İmza doğrulanamadı");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Carrier webhook rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}
