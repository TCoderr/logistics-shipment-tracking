using Lst.Platform.Identity;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Identity;

/// <summary>Development seed'i ve test fixture'ı için idempotent rol/kullanıcı kurulumu. Parolalar dışarıdan verilir, hiçbir yere yazılmaz.</summary>
public static class IdentitySeeding
{
    public static async Task EnsureRolesAsync(RoleManager<LstRole> roleManager)
    {
        ArgumentNullException.ThrowIfNull(roleManager);
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new LstRole(role) { Id = Guid.CreateVersion7() }), "role");
            }
        }
    }

    public static async Task<LstUser> EnsureUserAsync(
        UserManager<LstUser> userManager,
        LogisticsDbContext db,
        string userName,
        string displayName,
        string role,
        string password,
        IReadOnlyCollection<Guid> hubIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hubIds);

        var user = await userManager.FindByNameAsync(userName);
        if (user is null)
        {
            user = new LstUser { Id = Guid.CreateVersion7(), UserName = userName, DisplayName = displayName };
            EnsureSucceeded(await userManager.CreateAsync(user, password), "user");
        }

        if (!await userManager.IsInRoleAsync(user, role))
        {
            EnsureSucceeded(await userManager.AddToRoleAsync(user, role), "user-role");
        }

        var existing = await db.Set<UserHubAssignment>()
            .Where(a => a.UserId == user.Id)
            .Select(a => a.HubId)
            .ToListAsync(cancellationToken);
        foreach (var hubId in hubIds.Except(existing))
        {
            db.Add(new UserHubAssignment(user.Id, hubId, now, assignedByUserId: null));
        }

        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private static void EnsureSucceeded(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            // Yalnız hata kodları; parola veya kullanıcı verisi mesaja girmez.
            var codes = string.Join(",", result.Errors.Select(e => e.Code));
            throw new InvalidOperationException($"Seed başarısız ({what}): {codes}");
        }
    }
}
