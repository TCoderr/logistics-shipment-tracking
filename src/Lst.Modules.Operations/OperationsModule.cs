using Lst.Modules.Identity;
using Lst.Modules.Operations.Application;
using Lst.Modules.Operations.Domain;
using Lst.Modules.Operations.Infrastructure;
using Lst.Modules.Shipments.Application;
using Lst.Platform.Http;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Lst.Modules.Operations;

public static class OperationsModule
{
    public static IServiceCollection AddOperationsModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, OperationsModelContributor>();
        services.AddSingleton<IInterceptor, ExceptionOutboxInterceptor>();
        services.AddScoped<ExceptionEventSink>();
        services.AddScoped<IShipmentEventSink>(sp => sp.GetRequiredService<ExceptionEventSink>());
        services.AddScoped<ExceptionQueueHandler>();
        services.AddScoped<ExceptionCommandHandler>();
        services.AddScoped<OperationsSummaryHandler>();
        services.AddScoped<ShipmentAuditHandler>();
        services.AddOptions<SlaMonitorOptions>()
            .BindConfiguration(SlaMonitorOptions.SectionName)
            .Validate(o => o.IntervalSeconds >= 10 && o.NoMovementBusinessHours is > 0 and <= 720 && o.OverdueHighAfterDays > 0 && o.BatchSize is > 0 and <= 5000,
                "Workers:SlaMonitor ayarları geçersiz.")
            .ValidateOnStart();
        services.AddSingleton<SlaMonitor>();
        services.AddScoped<Lst.Platform.Retention.IRetentionTask, ExceptionNoteRetentionTask>();
        services.AddHostedService<SlaMonitorWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        MapSlice7(app);
        // Referans veri (Dilim 9): oluşturma formu ve filtreler hub listesini buradan alır. Kişisel veri içermez.
        app.MapGet("/api/reference/hubs", ListHubsAsync)
            .RequireAuthorization(Policies.InternalUser)
            .WithTags("Reference")
            .WithName("ListHubs")
            .Produces<HubReferenceResponse>();
        var group = app.MapGroup("/api/exceptions").WithTags("Exceptions").RequireAuthorization(Policies.OperationsStaff);

        group.MapGet("/", ListAsync)
            .WithName("ListExceptions")
            .Produces<ExceptionQueueResponse>()
            .ProducesValidationProblem();

        group.MapPost("/{id:guid}/assign", AssignAsync)
            .RequireCsrf()
            .WithName("AssignException")
            .Accepts<AssignExceptionRequest>("application/json")
            .Produces<ExceptionItem>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{id:guid}/resolve", ResolveAsync)
            .RequireCsrf()
            .WithName("ResolveException")
            .Accepts<ResolveExceptionRequest>("application/json")
            .Produces<ExceptionItem>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return app;
    }

    /// <summary>UC-10 özet, UC-12 audit ve outbox yönetimi (Dilim 7).</summary>
    private static void MapSlice7(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/operations/summary", SummaryAsync)
            .RequireAuthorization(Policies.OperationsStaff)
            .WithTags("Operations")
            .WithName("GetOperationsSummary")
            .Produces<OperationsSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        app.MapGet("/api/shipments/{id:guid}/audit", AuditAsync)
            .RequireAuthorization(Policies.OpsManagerOnly)
            .WithTags("Shipments")
            .WithName("GetShipmentAudit")
            .Produces<AuditPage>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var outbox = app.MapGroup("/api/operations/outbox").WithTags("Operations").RequireAuthorization(Policies.OpsManagerOnly);
        outbox.MapGet("/", OutboxListAsync)
            .WithName("ListOutboxMessages")
            .Produces<IReadOnlyList<Lst.Platform.Outbox.OutboxMessageView>>()
            .ProducesValidationProblem();
        app.MapPost("/api/operations/retention/run", RetentionRunAsync)
            .RequireAuthorization(Policies.OpsManagerOnly)
            .RequireCsrf()
            .WithTags("Operations")
            .WithName("RunRetention")
            .ProducesProblem(StatusCodes.Status409Conflict);

        outbox.MapPost("/{messageId:guid}/replay", OutboxReplayAsync)
            .RequireCsrf()
            .WithName("ReplayOutboxMessage")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> SummaryAsync(string? hubCode, string? date, HttpContext httpContext, OperationsSummaryHandler handler)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (hubCode is not null && !System.Text.RegularExpressions.Regex.IsMatch(hubCode, "^[A-Z]{3}-[0-9]{2}$"))
        {
            errors["hubCode"] = ["Biçim: ABC-01."];
        }

        DateOnly? day = null;
        if (date is not null)
        {
            if (DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                day = parsed;
            }
            else
            {
                errors["date"] = ["Biçim: yyyy-MM-dd."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        return await handler.HandleAsync(hubCode, day, httpContext.RequestAborted) switch
        {
            SummaryOutcome.Ok ok => TypedResults.Ok(ok.Summary),
            _ => Problems.Create(StatusCodes.Status422UnprocessableEntity, "unknown-hub", "Hub bulunamadı"),
        };
    }

    private static async Task<IResult> AuditAsync(Guid id, long? before, int? limit, HttpContext httpContext, ShipmentAuditHandler handler)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var page = await handler.HandleAsync(id, before, limit ?? 50, httpContext.RequestAborted);
        return page is null
            ? Problems.Create(StatusCodes.Status404NotFound, "not-found", "Gönderi bulunamadı")
            : TypedResults.Ok(page);
    }

    private static async Task<IResult> OutboxListAsync(string? status, int? limit, HttpContext httpContext, Lst.Platform.Outbox.OutboxAdministration admin)
    {
        if (status is not (null or "DeadLettered" or "Pending"))
        {
            return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["status"] = ["Desteklenen değerler: DeadLettered, Pending."] });
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(await admin.ListAsync(status is null or "DeadLettered", limit ?? 50, httpContext.RequestAborted));
    }

    private static async Task<IResult> RetentionRunAsync(HttpContext httpContext, Lst.Platform.Retention.RetentionJob job)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var result = await job.RunOnceAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        return result is null
            ? Problems.Create(StatusCodes.Status409Conflict, "retention-running", "Saklama işi şu an başka bir yerde çalışıyor")
            : TypedResults.Ok(new { affected = result.Affected, total = result.Total });
    }

    private static async Task<IResult> OutboxReplayAsync(Guid messageId, HttpContext httpContext, Lst.Platform.Outbox.OutboxAdministration admin)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return await admin.ReplayAsync(messageId, httpContext.User.GetUserId()!.Value, httpContext.RequestAborted) switch
        {
            Lst.Platform.Outbox.OutboxReplayResult.Replayed => TypedResults.Accepted((string?)null, new { messageId, status = "Pending" }),
            Lst.Platform.Outbox.OutboxReplayResult.NotDeadLettered => Problems.Create(StatusCodes.Status409Conflict, "outbox-message-not-dead-lettered",
                "Yalnız dead-letter mesajlar yeniden kuyruğa alınabilir"),
            _ => Problems.Create(StatusCodes.Status404NotFound, "not-found", "Mesaj bulunamadı"),
        };
    }

    private static async Task<IResult> ListHubsAsync(HttpContext httpContext, LogisticsDbContext db)
    {
        var items = await db.Set<Lst.Platform.Operations.Hub>().AsNoTracking()
            .OrderBy(h => h.Code)
            .Select(h => new HubReferenceItem(h.Code, h.Name, h.City, h.IsActive))
            .ToListAsync(httpContext.RequestAborted);
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new HubReferenceResponse(items));
    }

    private static async Task<IResult> ListAsync([AsParameters] ExceptionQueueQuery query, HttpContext httpContext, IActorAccessReader accessReader, ExceptionQueueHandler handler)
    {
        if (!ExceptionQueueRequest.TryCreate(query, out var request, out var errors))
        {
            return Problems.Validation(errors);
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        httpContext.Response.Headers.CacheControl = "no-store";
        return await handler.HandleAsync(request!, access, httpContext.RequestAborted) switch
        {
            ExceptionQueueOutcome.Page page => TypedResults.Ok(page.Response),
            _ => Problems.Create(StatusCodes.Status400BadRequest, "invalid-cursor", "Cursor geçersiz", "İlk sayfadan (cursor olmadan) başlayın."),
        };
    }

    private static async Task<IResult> AssignAsync(Guid id, HttpContext httpContext, IActorAccessReader accessReader, ExceptionCommandHandler handler)
    {
        if (ReadIfMatch(httpContext, out var ifMatch) is { } missing)
        {
            return missing;
        }

        var body = await ReadJsonAsync<AssignExceptionRequest>(httpContext);
        if (body is null || body.AssigneeUserId is not { } assignee || assignee == Guid.Empty)
        {
            return Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["assigneeUserId"] = ["Zorunludur."] });
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        return Map(httpContext, await handler.AssignAsync(id, ifMatch, assignee, access, httpContext.RequestAborted));
    }

    private static async Task<IResult> ResolveAsync(Guid id, HttpContext httpContext, IActorAccessReader accessReader, ExceptionCommandHandler handler)
    {
        if (ReadIfMatch(httpContext, out var ifMatch) is { } missing)
        {
            return missing;
        }

        var body = await ReadJsonAsync<ResolveExceptionRequest>(httpContext);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ResolutionCode code = default;
        if (body?.ResolutionCode is null || !Enum.TryParse(body.ResolutionCode, ignoreCase: false, out code)
            || body.ResolutionCode != code.ToString() || code == ResolutionCode.AutoResolved)
        {
            errors["resolutionCode"] = ["Geçerli bir çözüm kodu olmalı."];
        }

        if (string.IsNullOrWhiteSpace(body?.Note) || body.Note.Length > ExceptionCommandHandler.MaxNoteLength)
        {
            errors["note"] = ["Çözüm notu zorunludur (en fazla 1000 karakter)."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var access = await accessReader.GetAsync(httpContext.User.GetUserId()!.Value, httpContext.RequestAborted);
        return Map(httpContext, await handler.ResolveAsync(id, ifMatch, code, body!.Note!, access, httpContext.RequestAborted));
    }

    private static IResult Map(HttpContext httpContext, ExceptionCommandOutcome outcome)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        switch (outcome)
        {
            case ExceptionCommandOutcome.Succeeded ok:
                httpContext.Response.Headers.ETag = ok.Item.ETag;
                return TypedResults.Ok(ok.Item);
            case ExceptionCommandOutcome.PreconditionFailed stale:
                httpContext.Response.Headers.ETag = stale.CurrentETag;
                return Problems.Create(StatusCodes.Status412PreconditionFailed, "precondition-failed", "İstisna siz bakarken değişti",
                    "Güncel durumu yeniden okuyup işlemi tekrar değerlendirin.");
            case ExceptionCommandOutcome.Forbidden forbidden:
                return Problems.Create(StatusCodes.Status403Forbidden, "forbidden", "Bu işlem için yetkiniz yok", forbidden.Detail);
            case ExceptionCommandOutcome.Invalid invalid:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, invalid.Code, "İşlem geçersiz", invalid.Detail);
            case ExceptionCommandOutcome.Conflict conflict:
                return Problems.Create(StatusCodes.Status409Conflict, conflict.Code, "İşlem bu durumda yapılamaz", null,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["currentShipmentStatus"] = conflict.CurrentStatus });
            case ExceptionCommandOutcome.Busy:
                httpContext.Response.Headers.RetryAfter = "1";
                return Problems.Create(StatusCodes.Status503ServiceUnavailable, "exception-busy", "İstisna şu an işleniyor", "Kısa süre sonra tekrar deneyin.");
            default:
                return Problems.Create(StatusCodes.Status404NotFound, "not-found", "İstisna bulunamadı");
        }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? ReadIfMatch(HttpContext httpContext, out string ifMatch)
    {
        var values = httpContext.Request.Headers.IfMatch;
        ifMatch = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return string.IsNullOrWhiteSpace(ifMatch)
            ? Problems.Create(StatusCodes.Status428PreconditionRequired, "precondition-required", "If-Match gerekli", "İstisnanın son okunan ETag değerini gönderin.")
            : null;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext httpContext)
        where T : class
    {
        try
        {
            return await httpContext.Request.ReadFromJsonAsync<T>(Shipments.Application.ShipmentsJson.StrictRequest, httpContext.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Hub referans verisi (kod, ad, şehir, aktiflik). Kişisel veri içermez.</summary>
public sealed record HubReferenceItem(string Code, string Name, string City, bool IsActive);

public sealed record HubReferenceResponse(IReadOnlyList<HubReferenceItem> Items);
