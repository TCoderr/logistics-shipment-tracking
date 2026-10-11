using Lst.Modules.Identity;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lst.Modules.Shipments.Endpoints;

/// <summary>Durum değiştiren operatör ve hub komutları (dilim 4+).</summary>
internal static class ShipmentCommandEndpoints
{
    public const int MaxCommandBodyBytes = 8 * 1024;
    private static readonly HashSet<string> OccurredAtReasons = new(StringComparer.Ordinal) { "future-timestamp", "too-old" };

    public static void MapShipmentCommands(RouteGroupBuilder shipments, IEndpointRouteBuilder app)
    {
        shipments.MapPost("/{id:guid}/cancel", CancelAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .RequireCsrf()
            .WithName("CancelShipment")
            .Accepts<CancelShipmentRequest>("application/json")
            .Produces<ShipmentCommandResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        shipments.MapPost("/{id:guid}/return-to-sender", ReturnToSenderAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .RequireCsrf()
            .WithName("StartReturnToSender")
            .Accepts<CancelShipmentRequest>("application/json")
            .Produces<ShipmentCommandResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        shipments.MapPost("/{id:guid}/return-completed", CompleteReturnAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .RequireCsrf()
            .WithName("CompleteReturnToSender")
            .Produces<ShipmentCommandResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        shipments.MapPost("/{id:guid}/delivery-attempts", RecordDeliveryAttemptAsync)
            .RequireAuthorization(Policies.InternalUser)
            .RequireCsrf()
            .WithName("RecordDeliveryAttempt")
            .Accepts<DeliveryAttemptRequest>("application/json")
            .Produces<DeliveryAttemptResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        app.MapGroup("/api/hubs").WithTags("Hub scans")
            .MapPost("/{hubCode}/scans", ScanAsync)
            .RequireAuthorization(Policies.HubScanner)
            .RequireCsrf()
            .WithName("RecordHubScan")
            .Accepts<HubScanRequest>("application/json")
            .Produces<HubScanResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>If-Match zorunlu; yoksa 428. Tek ve tam eşleşen değer beklenir (opak ETag).</summary>
    public static IResult? RequireIfMatch(HttpContext httpContext, out string ifMatch)
    {
        var values = httpContext.Request.Headers.IfMatch;
        ifMatch = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return string.IsNullOrWhiteSpace(ifMatch)
            ? Problems.Create(StatusCodes.Status428PreconditionRequired, "precondition-required", "If-Match gerekli",
                "Gönderinin son okunan ETag değerini If-Match header'ında gönderin.")
            : null;
    }

    public static IResult MapOperatorOutcome(HttpContext httpContext, OperatorCommandOutcome outcome)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        switch (outcome)
        {
            case OperatorCommandOutcome.Succeeded ok:
                httpContext.Response.Headers.ETag = ok.ETag;
                return TypedResults.Ok(ok.Response);
            case OperatorCommandOutcome.PreconditionFailed stale:
                httpContext.Response.Headers.ETag = stale.CurrentETag;
                return Problems.Create(StatusCodes.Status412PreconditionFailed, "precondition-failed", "Gönderi siz bakarken değişti",
                    "Güncel durumu yeniden okuyup işlemi tekrar değerlendirin.");
            case OperatorCommandOutcome.NotAllowed notAllowed:
                return Problems.Create(StatusCodes.Status409Conflict, notAllowed.Code, "İşlem bu durumda yapılamaz", null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["currentStatus"] = notAllowed.CurrentStatus,
                        ["returnToSenderAllowed"] = notAllowed.ReturnToSenderAllowed,
                    });
            case OperatorCommandOutcome.Busy:
                httpContext.Response.Headers.RetryAfter = "1";
                return Problems.Create(StatusCodes.Status503ServiceUnavailable, "shipment-busy", "Gönderi şu an işleniyor", "Kısa süre sonra tekrar deneyin.");
            default:
                return Problems.Create(StatusCodes.Status404NotFound, "not-found", "Gönderi bulunamadı", "Gönderi bulunamadı veya erişiminiz yok.");
        }
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext httpContext, IActorAccessReader accessReader, ShipmentOperatorCommands handler)
    {
        if (RequireIfMatch(httpContext, out var ifMatch) is { } missing)
        {
            return missing;
        }

        var (body, error) = await ShipmentCommandSupport.ReadStrictJsonAsync<CancelShipmentRequest>(httpContext.Request, MaxCommandBodyBytes, httpContext.RequestAborted);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(body!.Reason) || body.Reason.Length > ShipmentOperatorCommands.MaxReasonLength)
        {
            return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["reason"] = ["İptal nedeni zorunludur (en fazla 500 karakter)."] });
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        return MapOperatorOutcome(httpContext, await handler.CancelAsync(id, ifMatch, body.Reason, access, httpContext.RequestAborted));
    }

    private static async Task<IResult> ReturnToSenderAsync(Guid id, HttpContext httpContext, IActorAccessReader accessReader, ShipmentOperatorCommands handler)
    {
        if (RequireIfMatch(httpContext, out var ifMatch) is { } missing)
        {
            return missing;
        }

        var (body, error) = await ShipmentCommandSupport.ReadStrictJsonAsync<CancelShipmentRequest>(httpContext.Request, MaxCommandBodyBytes, httpContext.RequestAborted);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(body!.Reason) || body.Reason.Length > ShipmentOperatorCommands.MaxReasonLength)
        {
            return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["reason"] = ["İade nedeni zorunludur (en fazla 500 karakter)."] });
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        return MapOperatorOutcome(httpContext, await handler.ReturnToSenderAsync(id, ifMatch, access, httpContext.RequestAborted));
    }

    private static async Task<IResult> CompleteReturnAsync(Guid id, HttpContext httpContext, IActorAccessReader accessReader, ShipmentOperatorCommands handler)
    {
        if (RequireIfMatch(httpContext, out var ifMatch) is { } missing)
        {
            return missing;
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        return MapOperatorOutcome(httpContext, await handler.CompleteReturnAsync(id, ifMatch, access, httpContext.RequestAborted));
    }

    private static async Task<IResult> RecordDeliveryAttemptAsync(Guid id, HttpContext httpContext, IActorAccessReader accessReader, RecordDeliveryAttemptHandler handler)
    {
        if (IdempotencyHeader.Read(httpContext) is { Error: { } keyError })
        {
            return keyError;
        }

        var key = IdempotencyHeader.Read(httpContext).Key!;
        var (body, error) = await ShipmentCommandSupport.ReadStrictJsonAsync<DeliveryAttemptRequest>(httpContext.Request, MaxCommandBodyBytes, httpContext.RequestAborted);
        if (error is not null)
        {
            return error;
        }

        var (attempt, errors) = DeliveryAttemptValidator.Validate(body!);
        if (attempt is null)
        {
            return Problems.Validation(errors);
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        var outcome = await handler.HandleAsync(id, key, attempt, access, httpContext.RequestAborted);
        return IdempotencyHeader.MapOutcome(httpContext, outcome, reason => reason switch
        {
            RecordDeliveryAttemptHandler.NotFound => Problems.Create(StatusCodes.Status404NotFound, "not-found", "Gönderi bulunamadı", "Gönderi bulunamadı veya erişiminiz yok."),
            RecordDeliveryAttemptHandler.Forbidden => Problems.Create(StatusCodes.Status403Forbidden, "forbidden", "Teslimat denemesi yalnız varış hub'ı görevlisi veya operasyon personeli tarafından kaydedilir"),
            DeliveryAttemptPolicy.FutureTimestamp or DeliveryAttemptPolicy.BeforeOutForDelivery =>
                Problems.Create(StatusCodes.Status422UnprocessableEntity, "invalid-occurred-at", "Deneme zamanı geçersiz", null,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["reasonCode"] = reason }),
            DeliveryAttemptPolicy.ReasonRequired or DeliveryAttemptPolicy.NoteRequired =>
                Problems.Create(StatusCodes.Status422UnprocessableEntity, reason, "Deneme bilgisi eksik"),
            _ => Problems.Create(StatusCodes.Status409Conflict, reason, "Teslimat denemesi bu durumda kaydedilemez"),
        });
    }

    private static async Task<IResult> ScanAsync(string hubCode, HttpContext httpContext, IActorAccessReader accessReader, RecordHubScanHandler handler)
    {
        var (body, error) = await ShipmentCommandSupport.ReadStrictJsonAsync<HubScanRequest>(httpContext.Request, MaxCommandBodyBytes, httpContext.RequestAborted);
        if (error is not null)
        {
            return error;
        }

        var (scan, errors) = HubScanValidator.Validate(body!);
        if (scan is null)
        {
            return Problems.Validation(errors);
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        var outcome = await handler.HandleAsync(hubCode, scan, access, httpContext.RequestAborted);
        httpContext.Response.Headers.CacheControl = "no-store";
        switch (outcome)
        {
            case HubScanOutcome.Recorded recorded:
                return TypedResults.Ok(recorded.Response);
            case HubScanOutcome.Rejected rejected:
                var reason = rejected.Response.ReasonCode ?? "transition-not-allowed";
                var invalidTime = OccurredAtReasons.Contains(reason);
                return Problems.Create(
                    invalidTime ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status409Conflict,
                    invalidTime ? "invalid-occurred-at" : reason == "terminal-state" ? "transition-not-allowed" : reason,
                    "Tarama reddedildi",
                    null,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["outcome"] = "Rejected",
                        ["reasonCode"] = reason,
                        ["shipment"] = rejected.Response.Shipment,
                        ["package"] = rejected.Response.Package,
                        ["consolidation"] = rejected.Response.Consolidation,
                    });
            case HubScanOutcome.Forbidden:
                return Problems.Create(StatusCodes.Status403Forbidden, "forbidden", "Bu hub'da tarama yetkiniz yok");
            case HubScanOutcome.HubInactive:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, "hub-inactive", "Hub aktif değil");
            case HubScanOutcome.ClientScanIdConflict:
                return Problems.Create(StatusCodes.Status409Conflict, "client-scan-id-conflict", "clientScanId başka bir tarama için kullanılmış",
                    "Her tarama için el terminalinde yeni bir clientScanId üretin.");
            case HubScanOutcome.Busy:
                httpContext.Response.Headers.RetryAfter = "1";
                return Problems.Create(StatusCodes.Status503ServiceUnavailable, "shipment-busy", "Gönderi şu an işleniyor", "Kısa süre sonra tekrar deneyin.");
            default:
                return Problems.Create(StatusCodes.Status404NotFound, "not-found", "Hub veya koli etiketi bulunamadı");
        }
    }
}
