using System.Diagnostics;
using Lst.Platform.Auditing;
using Lst.Platform.Http;
using Lst.Platform.Identity;
using Lst.Platform.Persistence;
using Lst.Platform.Realtime;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;

namespace Lst.Modules.Identity;

public sealed record LoginRequest(string? UserName, string? Password);

public sealed record CsrfTokenResponse(string CsrfToken);

public sealed record CurrentUserResponse(
    Guid Id,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> HubCodes,
    string CsrfToken);

/// <summary>
/// Bilinmeyen kullanıcı adında da parola doğrulaması kadar iş yapılır; yanıt süresi ve gövdesi kullanıcı varlığını sızdırmaz.
/// </summary>
internal sealed class DummyPasswordHash
{
    public DummyPasswordHash()
    {
        // Identity'nin varsayılan (scoped) hasher'ıyla aynı algoritma; singleton olduğu için kendi örneğini kullanır.
        User = new LstUser();
        Hash = new PasswordHasher<LstUser>().HashPassword(
            User, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
    }

    public LstUser User { get; }

    public string Hash { get; }
}

public static class IdentityAuditActions
{
    public const string LoginFailed = "LoginFailed";
}

public static class LoginFailureReasons
{
    public const string InvalidCredentials = "invalid-credentials";
    public const string LockedOut = "locked-out";
    public const string NotAllowed = "not-allowed";
}

public static class AuthEndpoints
{
    private const int MaxCredentialLength = 256;
    private const string UnknownUserEntityId = "unknown";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapGet("/csrf", GetCsrfToken)
            .AllowAnonymous()
            .WithName("GetCsrfToken")
            .WithSummary("Oturum öncesi antiforgery cookie'si ve request token'ı üretir.");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireCsrf()
            .WithName("Login")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(Policies.Authenticated)
            .RequireCsrf()
            .WithName("Logout");

        group.MapGet("/me", GetCurrentUserAsync)
            .RequireAuthorization(Policies.Authenticated)
            .WithName("GetCurrentUser");

        return app;
    }

    private static Ok<CsrfTokenResponse> GetCsrfToken(HttpContext httpContext, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new CsrfTokenResponse(tokens.RequestToken!));
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> LoginAsync(
        LoginRequest? request,
        UserManager<LstUser> userManager,
        SignInManager<LstUser> signInManager,
        IPasswordHasher<LstUser> passwordHasher,
        DummyPasswordHash dummy,
        LogisticsDbContext db,
        TimeProvider timeProvider,
        HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request?.UserName) || request.UserName.Length > MaxCredentialLength)
        {
            errors["userName"] = ["Kullanıcı adı zorunludur."];
        }

        if (string.IsNullOrEmpty(request?.Password) || request.Password.Length > MaxCredentialLength)
        {
            errors["password"] = ["Parola zorunludur."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var user = await userManager.FindByNameAsync(request!.UserName!);
        if (user is null)
        {
            passwordHasher.VerifyHashedPassword(dummy.User, dummy.Hash, request.Password!);
            await AuditLoginFailedAsync(db, timeProvider, httpContext, UnknownUserEntityId, LoginFailureReasons.InvalidCredentials);
            return InvalidCredentials();
        }

        var result = await signInManager.PasswordSignInAsync(user, request.Password!, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return TypedResults.NoContent();
        }

        var reason = result.IsLockedOut ? LoginFailureReasons.LockedOut
            : result.IsNotAllowed ? LoginFailureReasons.NotAllowed
            : LoginFailureReasons.InvalidCredentials;
        await AuditLoginFailedAsync(db, timeProvider, httpContext, user.Id.ToString(), reason);
        return InvalidCredentials();
    }

    /// <summary>
    /// docs/06 §1.2: başarısız giriş audit'e yazılır. Kullanıcı adı, parola ve istek gövdesi yazılmaz
    /// (kullanıcı adı alanına yanlışlıkla parola girilmiş olabilir). İç neden yalnız audit'tedir; HTTP yanıtı geneldir.
    /// </summary>
    private static async Task AuditLoginFailedAsync(
        LogisticsDbContext db,
        TimeProvider timeProvider,
        HttpContext httpContext,
        string entityId,
        string reasonCode)
    {
        db.Add(AuditEntryFactory.AnonymousRejected(
            timeProvider.GetUtcNow(),
            IdentityAuditActions.LoginFailed,
            "User",
            entityId,
            reasonCode,
            Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier));
        await db.SaveChangesAsync(httpContext.RequestAborted);
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<LstUser> signInManager, IRealtimeSessions realtimeSessions, HttpContext httpContext)
    {
        var userId = httpContext.User.GetUserId();
        await signInManager.SignOutAsync();
        if (userId is { } id)
        {
            // Çıkışla birlikte kullanıcının canlı bağlantıları da kapatılır (cookie silinse de açık WebSocket kendiliğinden kapanmaz).
            // Kullanıcının başka oturumu varsa onun istemcisi geçerli cookie'siyle yeniden bağlanır.
            realtimeSessions.DisconnectUser(id);
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUserResponse>, ProblemHttpResult>> GetCurrentUserAsync(
        HttpContext httpContext,
        IActorAccessReader accessReader,
        IAntiforgery antiforgery)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var userId = httpContext.User.GetUserId();
        if (userId is null)
        {
            return Problems.Create(StatusCodes.Status401Unauthorized, "unauthenticated", "Oturum gerekli");
        }

        var access = await accessReader.GetAsync(userId.Value, httpContext.RequestAborted);
        var tokens = antiforgery.GetAndStoreTokens(httpContext);

        // Antiforgery kendi önbellek header'ını yazar; sözleşmedeki değer token üretiminden sonra kesinleştirilir.
        httpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new CurrentUserResponse(
            access.UserId,
            access.DisplayName,
            access.Roles.Order(StringComparer.Ordinal).ToList(),
            access.Hubs.Values.Order(StringComparer.Ordinal).ToList(),
            tokens.RequestToken!));
    }

    /// <summary>Hatalı parola, bilinmeyen kullanıcı ve kilitli hesap aynı yanıtı alır.</summary>
    private static ProblemHttpResult InvalidCredentials() =>
        Problems.Create(
            StatusCodes.Status401Unauthorized,
            "invalid-credentials",
            "Giriş yapılamadı",
            "Kullanıcı adı veya parola hatalı ya da hesap geçici olarak kilitli.");
}
