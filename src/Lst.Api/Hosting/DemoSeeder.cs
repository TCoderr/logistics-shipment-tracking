using Lst.Modules.Identity;
using Lst.Platform.Identity;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Lst.Api.Hosting;

/// <summary>
/// Yalnız Development demo verisi: hub'lar, roller ve parolası özel olarak verilen demo kullanıcıları.
/// Parolalar user-secrets veya secret dosyalarından okunur (Seed:Passwords:&lt;kullanıcı adı&gt;); hiçbir çıktıya yazılmaz.
/// Parolası verilmeyen kullanıcı oluşturulmaz.
/// </summary>
internal static partial class DemoSeeder
{
    public static readonly (string UserName, string DisplayName, string Role, string[] HubCodes)[] Users =
    [
        ("ops.agent", "Ops Agent (demo)", Roles.OpsAgent, []),
        ("ops.manager", "Ops Manager (demo)", Roles.OpsManager, []),
        ("hub.ist", "Hub Operator IST-01 (demo)", Roles.HubOperator, ["IST-01"]),
        ("hub.ank", "Hub Operator ANK-02 (demo)", Roles.HubOperator, ["ANK-02"]),
        ("hub.izm", "Hub Operator IZM-01 (demo)", Roles.HubOperator, ["IZM-01"]),
    ];

    public static async Task<int> SeedAsync(IServiceProvider services, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<LogisticsDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var hubs = await HubSeeding.EnsureAsync(db, HubSeeding.Defaults, now, cancellationToken);
        await IdentitySeeding.EnsureRolesAsync(services.GetRequiredService<RoleManager<LstRole>>());

        var userManager = services.GetRequiredService<UserManager<LstUser>>();
        var created = 0;
        foreach (var (userName, displayName, role, hubCodes) in Users)
        {
            var password = configuration[$"Seed:Passwords:{userName}"];
            if (string.IsNullOrEmpty(password))
            {
                LogSeedPasswordMissing(logger, userName);
                continue;
            }

            await IdentitySeeding.EnsureUserAsync(
                userManager, db, userName, displayName, role, password,
                hubCodes.Select(c => hubs[c]).ToList(), now, cancellationToken);
            created++;
        }

        LogSeedCompleted(logger, created);
        return created;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo user {UserName} skipped: no private password configured (Seed:Passwords:{UserName})")]
    private static partial void LogSeedPasswordMissing(ILogger logger, string userName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo seed completed; {Count} demo users ensured")]
    private static partial void LogSeedCompleted(ILogger logger, int count);
}
