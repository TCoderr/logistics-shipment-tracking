using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Lst.Platform.Http;

/// <summary>
/// Cookie ile kimliklenen ve durum değiştiren JSON endpoint'lerinde antiforgery doğrulaması.
/// SameSite=Strict bu kontrolün yerine geçmez. Yetkilendirme middleware'i bu filtreden önce çalışır.
/// </summary>
public sealed class AntiforgeryValidationFilter : IEndpointFilter
{
    public const string HeaderName = "X-CSRF-TOKEN";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Problems.Create(
                StatusCodes.Status400BadRequest,
                "csrf-validation-failed",
                "CSRF doğrulaması başarısız",
                "Geçerli bir X-CSRF-TOKEN header'ı gönderin. Token'ı oturum öncesi GET /api/auth/csrf, oturum sonrası GET /api/auth/me ile alın.");
        }

        return await next(context);
    }
}

public static class AntiforgeryEndpointExtensions
{
    public static TBuilder RequireCsrf<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, AntiforgeryValidationFilter>();
}
