using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lst.Platform.Http;

/// <summary>RFC 9457 ProblemDetails üretimi: type = urn:lst:problem:&lt;code&gt;, "code" ve "traceId" uzantıları.</summary>
public static class Problems
{
    public const string TypePrefix = "urn:lst:problem:";

    public static ProblemHttpResult Create(
        int statusCode,
        string code,
        string title,
        string? detail = null,
        IDictionary<string, object?>? extensions = null)
    {
        var ext = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code };
        if (extensions is not null)
        {
            foreach (var pair in extensions)
            {
                ext[pair.Key] = pair.Value;
            }
        }

        return TypedResults.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title,
            type: TypePrefix + code,
            extensions: ext);
    }

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "İstek doğrulanamadı",
            type: TypePrefix + "validation",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation" });

    /// <summary>Tüm ProblemDetails yanıtlarına traceId ve eksikse code ekler (AddProblemDetails ile kayıtlıdır).</summary>
    public static void Customize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        if (!problem.Extensions.ContainsKey("code"))
        {
            var code = problem.Status switch
            {
                StatusCodes.Status400BadRequest => "bad-request",
                StatusCodes.Status401Unauthorized => "unauthenticated",
                StatusCodes.Status403Forbidden => "forbidden",
                StatusCodes.Status404NotFound => "not-found",
                StatusCodes.Status405MethodNotAllowed => "method-not-allowed",
                StatusCodes.Status415UnsupportedMediaType => "unsupported-media-type",
                _ => "internal-error",
            };
            problem.Extensions["code"] = code;
            problem.Type = TypePrefix + code;
        }
    }
}
