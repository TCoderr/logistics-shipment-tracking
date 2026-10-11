using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Platform.Operations;

public sealed record HubSeed(string Code, string Name, string City, string? District, bool IsActive);

public static class HubSeeding
{
    /// <summary>Development ve test referans verisi. Üretim hub listesi ayrı yönetilir.</summary>
    public static readonly IReadOnlyList<HubSeed> Defaults =
    [
        new("IST-01", "İstanbul Avrupa Aktarma", "İstanbul", "Bağcılar", true),
        new("ANK-02", "Ankara Çankaya Dağıtım", "Ankara", "Çankaya", true),
        new("IZM-01", "İzmir Aktarma", "İzmir", "Bornova", true),
        new("BRS-01", "Bursa Aktarma", "Bursa", "Nilüfer", false),
    ];

    public static async Task<IReadOnlyDictionary<string, Guid>> EnsureAsync(
        LogisticsDbContext db,
        IEnumerable<HubSeed> hubs,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hubs);

        var existing = await db.Set<Hub>().ToDictionaryAsync(h => h.Code, h => h.Id, StringComparer.Ordinal, cancellationToken);
        foreach (var seed in hubs.Where(h => !existing.ContainsKey(h.Code)))
        {
            var hub = new Hub(Guid.CreateVersion7(now), seed.Code, seed.Name, seed.City, seed.District, seed.IsActive, now);
            db.Add(hub);
            existing[seed.Code] = hub.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }
}
