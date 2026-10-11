using Microsoft.Extensions.FileProviders;

namespace Lst.Api.Hosting;

/// <summary>
/// Operasyon paneli ve public takip arayüzü (React SPA, <c>src/web</c>) API ile aynı origin'den sunulur; ayrı origin, CORS veya
/// token aktarımı yoktur. Statik dosyalar yoksa (yalnız API çalıştırma, testler) hiçbir şey eklenmez.
/// Public takip kodu SPA yolundadır (<c>/t/{kod}</c>): belge yanıtları referrer sızdırmaz, önbelleğe alınmaz ve istek yolu loglanmaz.
/// </summary>
internal static class SpaHosting
{
    /// <summary>Bu ön eklerle başlayan yollar asla SPA kabuğuna düşmez; bilinmeyen API yolu 404 ProblemDetails kalır.</summary>
    private static readonly string[] ReservedPrefixes = ["/api", "/health", "/realtime", "/openapi"];

    // İnline script/stil yok; yalnız kendi origin'i. SignalR (dilim 10) aynı origin WebSocket kullanır.
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// <c>Spa:DistPath</c> (içerik köküne göre; ör. yerel çalıştırmada <c>../web/dist</c>) veya yayınlanmış <c>wwwroot</c>.
    /// <c>index.html</c> bulunamazsa <c>null</c>.
    /// </summary>
    public static IFileProvider? ResolveFiles(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Spa:DistPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured, environment.ContentRootPath);
            return File.Exists(Path.Combine(fullPath, "index.html")) ? new PhysicalFileProvider(fullPath) : null;
        }

        var webRoot = environment.WebRootFileProvider;
        return webRoot.GetFileInfo("index.html").Exists ? webRoot : null;
    }

    public static void UseLstSpaStaticFiles(this WebApplication app, IFileProvider files) =>
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                var response = context.Context.Response;
                ApplyDocumentHeaders(response.Headers);
                // Vite, /assets altında içerik özetli dosya adları üretir: değişmez önbellek güvenlidir. Diğerleri her seferinde doğrulanır.
                response.Headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            },
        });

    public static void MapLstSpaFallback(this WebApplication app, IFileProvider files)
    {
        var index = ReadIndex(files);
        app.MapFallback(async context =>
        {
            var request = context.Request;
            var isRead = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
            if (!isRead || IsReserved(request.Path) || Path.HasExtension(request.Path.Value))
            {
                // Gövde yazılmaz: StatusCodePages standart 404 ProblemDetails üretir.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var response = context.Response;
            ApplyDocumentHeaders(response.Headers);
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength = index.Length;
            if (HttpMethods.IsGet(request.Method))
            {
                await response.Body.WriteAsync(index, context.RequestAborted);
            }
        }).AllowAnonymous().ExcludeFromDescription();
    }

    private static bool IsReserved(PathString path)
    {
        foreach (var prefix in ReservedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyDocumentHeaders(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
    }

    private static byte[] ReadIndex(IFileProvider files)
    {
        using var stream = files.GetFileInfo("index.html").CreateReadStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
