using Lst.Platform.Http;
using Lst.Platform.Identity;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lst.Modules.Identity;

public static class IdentityModule
{
    public const string AuthCookieName = "__Host-lst.auth";
    public const string AntiforgeryCookieName = "__Host-lst.af";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, IdentityModelContributor>();

        services.AddIdentity<LstUser, LstRole>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<LogisticsDbContext>();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = AuthCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context => AuthProblems.WriteAsync(
                    context.HttpContext, StatusCodes.Status401Unauthorized, "unauthenticated", "Oturum gerekli"),
                OnRedirectToAccessDenied = context => AuthProblems.WriteAsync(
                    context.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "Bu işlem için yetkiniz yok"),
            };
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryValidationFilter.HeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddScoped<IActorAccessReader, ActorAccessReader>();
        // Canlı kanal host'ta kayıtlıysa (SignalR) onun uygulaması kullanılır; değilse çıkışta yapılacak iş yoktur.
        services.TryAddSingleton<Lst.Platform.Realtime.IRealtimeSessions, Lst.Platform.Realtime.NullRealtimeSessions>();
        services.AddScoped<IAuthorizationHandler, CurrentRoleHandler>();
        services.AddSingleton<DummyPasswordHash>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.InternalUser, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new CurrentRoleRequirement(Roles.All)))
            .AddPolicy(Policies.CanCreateShipment, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new CurrentRoleRequirement([Roles.OpsAgent, Roles.OpsManager])))
            .AddPolicy(Policies.OperationsStaff, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new CurrentRoleRequirement([Roles.OpsAgent, Roles.OpsManager])))
            .AddPolicy(Policies.OpsManagerOnly, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new CurrentRoleRequirement([Roles.OpsManager])))
            .AddPolicy(Policies.HubScanner, p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new CurrentRoleRequirement([Roles.HubOperator])));

        return services;
    }
}

internal static class AuthProblems
{
    public static async Task WriteAsync(HttpContext httpContext, int statusCode, string code, string title)
    {
        httpContext.Response.StatusCode = statusCode;
        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        var problemContext = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = statusCode,
                Title = title,
                Type = Problems.TypePrefix + code,
            },
        };
        problemContext.ProblemDetails.Extensions["code"] = code;
        await problemDetailsService.WriteAsync(problemContext);
    }
}
