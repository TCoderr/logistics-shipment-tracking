using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Application;
using Lst.Platform.Http;
using Lst.Platform.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Lst.Modules.Shipments.Endpoints;

public static partial class ShipmentEndpoints
{
    public const int MaxCreateBodyBytes = 64 * 1024;
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";

    public static IEndpointRouteBuilder MapShipmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shipments").WithTags("Shipments");

        // Sıra: kimlik doğrulama + güncel DB rolü (yetkilendirme middleware'i) → CSRF filtresi → idempotency/replay.
        group.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanCreateShipment)
            .RequireCsrf()
            .WithName("CreateShipment")
            .Accepts<CreateShipmentRequest>("application/json")
            .Produces<CreateShipmentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.InternalUser)
            .WithName("ListShipments")
            .Produces<ShipmentListResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.InternalUser)
            .WithName("GetShipment")
            .Produces<ShipmentDetailResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ShipmentCommandEndpoints.MapShipmentCommands(group, app);
        PublicTrackingEndpoints.Map(app);
        return app;
    }

    private static async Task<IResult> CreateAsync(HttpContext httpContext, CreateShipmentHandler handler)
    {
        var cancellationToken = httpContext.RequestAborted;
        var keyValues = httpContext.Request.Headers[IdempotencyKeyHeader];
        if (keyValues.Count == 0 || string.IsNullOrEmpty(keyValues[0]))
        {
            return Problems.Create(StatusCodes.Status400BadRequest, "idempotency-key-required", "Idempotency-Key gerekli",
                "Her yeni gönderi için yeni bir Idempotency-Key (16–128 karakter, [A-Za-z0-9_-]) gönderin.");
        }

        var key = keyValues[0]!;
        if (keyValues.Count > 1 || !IdempotencyKeyRegex().IsMatch(key))
        {
            return Problems.Create(StatusCodes.Status400BadRequest, "idempotency-key-invalid", "Idempotency-Key biçimi geçersiz",
                "Anahtar 16–128 karakter olmalı ve yalnız harf, rakam, '-' ve '_' içermeli.");
        }

        if (!IsJson(httpContext.Request.ContentType))
        {
            return Problems.Create(StatusCodes.Status415UnsupportedMediaType, "unsupported-media-type", "İçerik tipi desteklenmiyor",
                "İstek gövdesi application/json olmalı.");
        }

        var body = await ReadBodyAsync(httpContext.Request, MaxCreateBodyBytes, cancellationToken);
        if (body is null)
        {
            return Problems.Create(StatusCodes.Status413PayloadTooLarge, "payload-too-large", "İstek gövdesi çok büyük",
                "İstek gövdesi en fazla 64 KB olabilir.");
        }

        CreateShipmentRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CreateShipmentRequest>(body, ShipmentsJson.StrictRequest);
        }
        catch (JsonException ex)
        {
            // Gönderilen değer yankılanmaz; yalnız JSON yolu bildirilir.
            return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path] =
                    ["Gövde geçerli JSON değil, tanımsız veya yinelenen alan ya da yanlış tipte değer içeriyor."],
            });
        }

        if (request is null)
        {
            return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["$"] = ["Gövde zorunludur."] });
        }

        var (valid, errors) = CreateShipmentValidator.Validate(request);
        if (valid is null)
        {
            return Problems.Validation(errors);
        }

        var command = new CreateShipmentCommand(
            httpContext.User.GetUserId()!.Value,
            key,
            valid,
            CreateShipmentRequestHash.Compute(valid),
            Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);

        var outcome = await handler.HandleAsync(command, cancellationToken);
        return outcome switch
        {
            CreateShipmentOutcome.Created created => new StoredResponseResult(created.Response, replayed: false),
            CreateShipmentOutcome.Replayed replayed => new StoredResponseResult(replayed.Response, replayed: true),
            CreateShipmentOutcome.KeyReused => Problems.Create(StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused",
                "Idempotency-Key başka bir istekle kullanılmış",
                "Bu anahtar farklı bir istek gövdesiyle kullanıldı. Yeni bir iş için yeni anahtar üretin."),
            CreateShipmentOutcome.InProgress => InProgress(httpContext),
            CreateShipmentOutcome.UnknownHub unknown => Problems.Create(StatusCodes.Status422UnprocessableEntity, "unknown-hub",
                "Hub bulunamadı", "Belirtilen hub kodu kayıtlı değil.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["hubCodes"] = unknown.HubCodes }),
            CreateShipmentOutcome.HubInactive inactive => Problems.Create(StatusCodes.Status422UnprocessableEntity, "hub-inactive",
                "Hub aktif değil", "Pasif hub için gönderi oluşturulamaz.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["hubCodes"] = inactive.HubCodes }),
            CreateShipmentOutcome.CommitOutcomeUnknown => Problems.Create(StatusCodes.Status503ServiceUnavailable, "commit-outcome-unknown",
                "İşlem sonucu doğrulanamadı",
                "Gönderinin kaydedilip kaydedilmediği doğrulanamadı. Aynı Idempotency-Key ile tekrar deneyin; kayıt oluştuysa aynı yanıt döner."),
            _ => throw new UnreachableException(),
        };
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ShipmentListQuery query,
        HttpContext httpContext,
        IActorAccessReader accessReader,
        ListShipmentsHandler handler)
    {
        if (!ShipmentListRequest.TryCreate(query, out var request, out var errors))
        {
            return Problems.Validation(errors);
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        var outcome = await handler.HandleAsync(request!, access, httpContext.RequestAborted);
        httpContext.Response.Headers.CacheControl = "no-store";
        return outcome switch
        {
            ShipmentListOutcome.Page page => TypedResults.Ok(page.Response),
            ShipmentListOutcome.CursorMismatch => Problems.Create(StatusCodes.Status400BadRequest, "cursor-mismatch",
                "Cursor bu sorguya ait değil", "Filtre veya sıralama değiştiyse ilk sayfadan (cursor olmadan) başlayın."),
            _ => Problems.Create(StatusCodes.Status400BadRequest, "invalid-cursor", "Cursor geçersiz",
                "Cursor bozulmuş veya süresi geçmiş. İlk sayfadan (cursor olmadan) başlayın."),
        };
    }

    private static async Task<Results<Ok<ShipmentDetailResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        HttpContext httpContext,
        IActorAccessReader accessReader,
        GetShipmentDetailHandler handler)
    {
        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        var result = await handler.HandleAsync(id, access, httpContext.RequestAborted);
        if (result is null)
        {
            return Problems.Create(StatusCodes.Status404NotFound, "not-found", "Gönderi bulunamadı",
                "Gönderi bulunamadı veya erişiminiz yok.");
        }

        httpContext.Response.Headers.ETag = result.ETag;
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(result.Response);
    }

    private static ProblemHttpResult InProgress(HttpContext httpContext)
    {
        httpContext.Response.Headers.RetryAfter = "1";
        return Problems.Create(StatusCodes.Status409Conflict, "idempotency-request-in-progress",
            "Aynı anahtarlı istek işleniyor", "Aynı Idempotency-Key ile kısa süre sonra tekrar deneyin.");
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        && (!mediaType.Charset.HasValue || mediaType.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyKeyRegex();
}
