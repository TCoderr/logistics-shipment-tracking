using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lst.Platform.Http;

public static class RateLimitPolicies
{
    public const string PublicTracking = "public-tracking";
    public const string PublicPathPrefix = "/api/public";
}

/// <summary>"ReverseProxy" bölümü: yalnız burada listelenen proxy/ağlardan gelen X-Forwarded-For dikkate alınır.</summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    public IList<string> TrustedProxies { get; } = [];

    public IList<string> TrustedNetworks { get; } = [];
}

public sealed class PublicRateLimitOptions
{
    public const string SectionName = "RateLimiting:Public";

    /// <summary>İstemci IP'si başına dakikadaki istek (sliding window).</summary>
    public int PerIpPerMinute { get; set; } = 20;

    /// <summary>Tüm public istekler için dakikadaki üst sınır.</summary>
    public int GlobalPerMinute { get; set; } = 600;
}

public static class RateLimitingRegistration
{
    /// <summary>
    /// Rate limiter: reddedilen istek <c>429 rate-limited</c> + <c>Retry-After</c>. Public takip: istemci IP'si başına sliding window + global sınır.
    /// İstemci IP'si ForwardedHeaders sonrası <see cref="ConnectionInfo.RemoteIpAddress"/>'tir; güvenilmeyen kaynaktan gelen X-Forwarded-For yok sayılır.
    /// </summary>
    public static IServiceCollection AddLstRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PublicRateLimitOptions>()
            .Bind(configuration.GetSection(PublicRateLimitOptions.SectionName))
            .Validate(o => o.PerIpPerMinute is > 0 and <= 10_000 && o.GlobalPerMinute >= o.PerIpPerMinute, "RateLimiting:Public ayarları geçersiz.")
            .ValidateOnStart();

        // Konfigürasyon çözümleme anında okunur (test/ortam ayarları kayıt sırasından bağımsız uygulanır).
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, config) =>
        {
            var proxies = new ReverseProxyOptions();
            config.GetSection(ReverseProxyOptions.SectionName).Bind(proxies);
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            // Varsayılan loopback güveni kaldırılır: hiçbir kaynak açıkça listelenmedikçe güvenilir değildir.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies.TrustedProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in proxies.TrustedNetworks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 1;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new ValueTask(Problems.Create(StatusCodes.Status429TooManyRequests, "rate-limited", "Çok fazla istek",
                    "Kısa süre sonra tekrar deneyin.").ExecuteAsync(context.HttpContext));
            };
            options.AddPolicy(RateLimitPolicies.PublicTracking, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<PublicRateLimitOptions>>().Value;
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetSlidingWindowLimiter("ip:" + ip, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.PerIpPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                if (!httpContext.Request.Path.StartsWithSegments(RateLimitPolicies.PublicPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetNoLimiter("internal");
                }

                var limits = httpContext.RequestServices.GetRequiredService<IOptions<PublicRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter("public-global", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.GlobalPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });

        return services;
    }

    /// <summary>
    /// ForwardedHeaders yalnız en az bir güvenilir proxy/ağ tanımlıysa eklenir. ASP.NET Core, iki liste de boşsa her kaynağa güvenir;
    /// bu yüzden varsayılan (loopback) listeler temizlenip middleware hiç eklenmez: X-Forwarded-For yok sayılır, istemci IP'si bağlantı IP'sidir.
    /// </summary>
    public static IApplicationBuilder UseLstForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        return options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0 ? app.UseForwardedHeaders() : app;
    }

    /// <summary>
    /// Public yanıtlar (200, 404, 429 dahil): önbelleğe alınmaz, referrer sızdırılmaz, arama motoru dizinlemez.
    /// Pipeline'da rate limiter'dan önce çağrılır ki reddedilen yanıtlar da başlıkları taşısın.
    /// </summary>
    public static IApplicationBuilder UsePublicResponseHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(RateLimitPolicies.PublicPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    headers.CacheControl = "no-store";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers["X-Robots-Tag"] = "noindex, nofollow";
                    headers.XContentTypeOptions = "nosniff";
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
}
