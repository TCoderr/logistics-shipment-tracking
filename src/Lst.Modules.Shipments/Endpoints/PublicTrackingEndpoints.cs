using Lst.Modules.Identity;
using Lst.Modules.Shipments.Application;
using Lst.Platform.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lst.Modules.Shipments.Endpoints;

/// <summary>UC-11 public takip (anonim) ve E16 kod yenileme (OpsManager).</summary>
internal static class PublicTrackingEndpoints
{
    /// <summary>Geçersiz biçim ve bulunamayan kod için byte düzeyinde aynı gövde (traceId dahil değişken alan yok).</summary>
    private const string NotFoundBody =
        "{\"type\":\"urn:lst:problem:not-found\",\"title\":\"Gönderi bulunamadı\",\"status\":404,\"code\":\"not-found\"}";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public/tracking/{code}", GetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicTracking)
            .WithTags("Public")
            .WithName("GetPublicTracking")
            .Produces<PublicTrackingView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapPost("/api/shipments/{id:guid}/public-tracking-code/rotate", RotateAsync)
            .RequireAuthorization(Policies.OpsManagerOnly)
            .RequireCsrf()
            .WithTags("Shipments")
            .WithName("RotatePublicTrackingCode")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> GetAsync(string code, PublicTrackingHandler handler, CancellationToken cancellationToken)
    {
        var view = await handler.HandleAsync(code, cancellationToken);
        return view is null
            ? Results.Text(NotFoundBody, "application/problem+json", System.Text.Encoding.UTF8, StatusCodes.Status404NotFound)
            : TypedResults.Ok(view);
    }

    private static async Task<IResult> RotateAsync(Guid id, HttpContext httpContext, RotatePublicTrackingCodeHandler handler)
    {
        if (ShipmentCommandEndpoints.RequireIfMatch(httpContext, out var ifMatch) is { } missing)
        {
            return missing;
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        switch (await handler.HandleAsync(id, ifMatch, httpContext.User.GetUserId()!.Value, httpContext.RequestAborted))
        {
            case RotateCodeOutcome.Succeeded ok:
                httpContext.Response.Headers.ETag = ok.ETag;
                return TypedResults.Ok(new { shipmentId = ok.ShipmentId, publicTrackingCode = ok.PublicTrackingCode });
            case RotateCodeOutcome.PreconditionFailed stale:
                httpContext.Response.Headers.ETag = stale.CurrentETag;
                return Problems.Create(StatusCodes.Status412PreconditionFailed, "precondition-failed", "Gönderi siz bakarken değişti",
                    "Güncel durumu yeniden okuyup işlemi tekrar değerlendirin.");
            default:
                return Problems.Create(StatusCodes.Status404NotFound, "not-found", "Gönderi bulunamadı");
        }
    }
}
