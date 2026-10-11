using System.Text.RegularExpressions;
using Lst.Platform.Http;
using Lst.Platform.Idempotency;
using Microsoft.AspNetCore.Http;

namespace Lst.Modules.Shipments.Endpoints;

/// <summary>Idempotency-Key header doğrulaması ve idempotent komut sonuçlarının HTTP eşlemesi (docs/05 §3).</summary>
internal static partial class IdempotencyHeader
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotency-Replayed";

    public static (string? Key, IResult? Error) Read(HttpContext httpContext)
    {
        var values = httpContext.Request.Headers[HeaderName];
        if (values.Count == 0 || string.IsNullOrEmpty(values[0]))
        {
            return (null, Problems.Create(StatusCodes.Status400BadRequest, "idempotency-key-required", "Idempotency-Key gerekli",
                "Her yeni işlem için yeni bir Idempotency-Key (16–128 karakter, [A-Za-z0-9_-]) gönderin."));
        }

        var key = values[0]!;
        return values.Count > 1 || !KeyRegex().IsMatch(key)
            ? (null, Problems.Create(StatusCodes.Status400BadRequest, "idempotency-key-invalid", "Idempotency-Key biçimi geçersiz",
                "Anahtar 16–128 karakter olmalı ve yalnız harf, rakam, '-' ve '_' içermeli."))
            : (key, null);
    }

    public static IResult MapOutcome(HttpContext httpContext, IdempotentCommandOutcome outcome, Func<string, IResult> mapRefusal)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(mapRefusal);
        switch (outcome)
        {
            case IdempotentCommandOutcome.Completed completed:
                return new StoredResponseResult(completed.Response, replayed: false);
            case IdempotentCommandOutcome.Replayed replayed:
                return new StoredResponseResult(replayed.Response, replayed: true);
            case IdempotentCommandOutcome.KeyReused:
                return Problems.Create(StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "Idempotency-Key başka bir istekle kullanılmış",
                    "Bu anahtar farklı bir istek gövdesiyle kullanıldı. Yeni bir işlem için yeni anahtar üretin.");
            case IdempotentCommandOutcome.InProgress:
                httpContext.Response.Headers.RetryAfter = "1";
                return Problems.Create(StatusCodes.Status409Conflict, "idempotency-request-in-progress", "Aynı anahtarlı istek işleniyor",
                    "Aynı Idempotency-Key ile kısa süre sonra tekrar deneyin.");
            case IdempotentCommandOutcome.Refused refused:
                httpContext.Response.Headers.CacheControl = "no-store";
                return mapRefusal(refused.Reason.ToString() ?? string.Empty);
            default:
                return Problems.Create(StatusCodes.Status503ServiceUnavailable, "commit-outcome-unknown", "İşlem sonucu doğrulanamadı",
                    "İşlemin kaydedilip kaydedilmediği doğrulanamadı. Aynı Idempotency-Key ile tekrar deneyin; kayıt oluştuysa aynı yanıt döner.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}

/// <summary>Saklanan yanıtı byte-byte yazar; replay yalnız ek bir header ile ayrışır.</summary>
internal sealed class StoredResponseResult(StoredResponse response, bool replayed) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var http = httpContext.Response;
        http.StatusCode = response.StatusCode;
        http.ContentType = response.ContentType;
        http.ContentLength = response.Body.Length;
        http.Headers.CacheControl = "no-store";
        if (response.Location is not null)
        {
            http.Headers.Location = response.Location;
        }

        if (response.ETag is not null)
        {
            http.Headers.ETag = response.ETag;
        }

        if (replayed)
        {
            http.Headers[IdempotencyHeader.ReplayedHeader] = "true";
        }

        await http.Body.WriteAsync(response.Body, httpContext.RequestAborted);
    }
}
