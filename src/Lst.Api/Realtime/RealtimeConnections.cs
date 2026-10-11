using System.Collections.Concurrent;
using Lst.Platform.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Lst.Api.Realtime;

internal enum SubscriptionKind
{
    Operations,
    Hub,
    Shipment,
}

/// <summary>
/// Bir bağlantının sunucuda tutulan aboneliği. Grup adı yalnız sunucuda üretilir; istemci grup adı veremez.
/// <paramref name="Key"/> istemciye geri bildirilen anlamlı anahtardır (hub kodu / gönderi kimliği), <paramref name="ResourceId"/> yetki kontrolünde kullanılır.
/// </summary>
internal sealed record RealtimeSubscription(SubscriptionKind Kind, string Key, Guid? ResourceId)
{
    public const string OperationsGroup = "ops";

    public string Group => Kind switch
    {
        SubscriptionKind.Operations => OperationsGroup,
        SubscriptionKind.Hub => HubGroup(ResourceId!.Value),
        _ => ShipmentGroup(ResourceId!.Value),
    };

    public static string HubGroup(Guid hubId) => "hub:" + hubId.ToString("N");

    public static string ShipmentGroup(Guid shipmentId) => "shipment:" + shipmentId.ToString("N");
}

internal sealed class RealtimeConnection(string connectionId, Guid userId, DateTimeOffset? ticketExpiresAt, HubCallerContext context)
{
    private readonly ConcurrentDictionary<string, RealtimeSubscription> _subscriptions = new(StringComparer.Ordinal);

    public string ConnectionId { get; } = connectionId;

    public Guid UserId { get; } = userId;

    /// <summary>Bağlantı kurulurken geçerli olan kimlik biletinin bitiş zamanı. Bağlantı bu andan sonra açık tutulmaz.</summary>
    public DateTimeOffset? TicketExpiresAt { get; } = ticketExpiresAt;

    public IReadOnlyCollection<RealtimeSubscription> Subscriptions => _subscriptions.Values.ToArray();

    public int SubscriptionCount => _subscriptions.Count;

    public bool Add(RealtimeSubscription subscription) => _subscriptions.TryAdd(subscription.Group, subscription);

    public bool Remove(string group) => _subscriptions.TryRemove(group, out _);

    public void Abort() => context.Abort();
}

/// <summary>
/// Açık bağlantılar ve abonelikleri (tek instance, bellek içi). Yetki kaynağı değildir: yetki her abonelikte ve periyodik yeniden
/// doğrulamada güncel DB rol/hub atamasından okunur; kayıt yalnız "kim neye abone" bilgisini tutar ki iptal uygulanabilsin.
/// </summary>
internal sealed class RealtimeConnectionRegistry : IRealtimeSessions
{
    private readonly ConcurrentDictionary<string, RealtimeConnection> _connections = new(StringComparer.Ordinal);

    public int Count => _connections.Count;

    public void Add(RealtimeConnection connection) => _connections[connection.ConnectionId] = connection;

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public RealtimeConnection? Find(string connectionId) => _connections.GetValueOrDefault(connectionId);

    public IReadOnlyList<RealtimeConnection> Snapshot() => _connections.Values.ToArray();

    public int DisconnectUser(Guid userId)
    {
        var closed = 0;
        foreach (var connection in _connections.Values)
        {
            if (connection.UserId == userId)
            {
                connection.Abort();
                closed++;
            }
        }

        return closed;
    }
}
