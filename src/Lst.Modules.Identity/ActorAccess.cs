using System.Security.Claims;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Identity;

/// <summary>
/// Kullanıcının o anki rolleri ve hub atamaları. Cookie içindeki rol claim'lerine güvenilmez;
/// değerler her istekte DB'den okunur (Karar değişikliği Dilim 1, docs/06 §2).
/// </summary>
public sealed record ActorAccess(
    Guid UserId,
    string DisplayName,
    IReadOnlySet<string> Roles,
    IReadOnlyDictionary<Guid, string> Hubs)
{
    public static ActorAccess None(Guid userId) =>
        new(userId, string.Empty, new HashSet<string>(StringComparer.Ordinal), new Dictionary<Guid, string>());

    public bool IsOperations => Roles.Contains(Identity.Roles.OpsAgent) || Roles.Contains(Identity.Roles.OpsManager);

    public bool IsHubOperator => Roles.Contains(Identity.Roles.HubOperator);

    public bool HasAnyRole(IEnumerable<string> roles) => roles.Any(Roles.Contains);
}

public interface IActorAccessReader
{
    Task<ActorAccess> GetAsync(Guid userId, CancellationToken cancellationToken);
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}

/// <summary>İstek kapsamlı (scoped); aynı istekte tekrar sorgu yapmaz.</summary>
internal sealed class ActorAccessReader(LogisticsDbContext db, TimeProvider timeProvider) : IActorAccessReader
{
    private ActorAccess? _cached;

    public async Task<ActorAccess> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_cached is not null && _cached.UserId == userId)
        {
            return _cached;
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.LockoutEnd })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null || (user.LockoutEnd is { } lockoutEnd && lockoutEnd > timeProvider.GetUtcNow()))
        {
            _cached = ActorAccess.None(userId);
            return _cached;
        }

        var roles = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                where userRole.UserId == userId && role.Name != null
                select role.Name!)
            .ToListAsync(cancellationToken);

        var hubs = await (
                from assignment in db.Set<UserHubAssignment>()
                join hub in db.Set<Hub>() on assignment.HubId equals hub.Id
                where assignment.UserId == userId
                select new { hub.Id, hub.Code })
            .ToListAsync(cancellationToken);

        _cached = new ActorAccess(
            userId,
            user.DisplayName,
            roles.ToHashSet(StringComparer.Ordinal),
            hubs.ToDictionary(h => h.Id, h => h.Code));
        return _cached;
    }
}
