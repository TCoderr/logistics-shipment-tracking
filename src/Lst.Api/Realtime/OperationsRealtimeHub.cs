using System.Text.RegularExpressions;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lst.Api.Realtime;

/// <summary>Sunucudan istemciye giden mesajlar: yalnız kimlik ve durum (PII yok). İstemci mesajı ipucu sayar ve kaynağı HTTP ile yeniden okur.</summary>
public sealed record ShipmentChangedMessage(Guid ShipmentId, string Status);

public sealed record ExceptionChangedMessage(Guid ExceptionId, Guid ShipmentId, string Status);

public sealed record SubscriptionRevokedMessage(string Kind, string Key);

public interface IOperationsRealtimeClient
{
    Task ShipmentChanged(ShipmentChangedMessage message);

    Task ExceptionChanged(ExceptionChangedMessage message);

    Task SubscriptionRevoked(SubscriptionRevokedMessage message);
}

/// <summary>Abonelik yetkisi: güncel DB rolü/hub ataması ve gönderi kapsamı (GET /api/shipments/{id} ile aynı kural).</summary>
internal sealed class RealtimeAuthorizer(LogisticsDbContext db)
{
    public async Task<bool> IsAllowedAsync(ActorAccess access, RealtimeSubscription subscription, CancellationToken cancellationToken)
    {
        switch (subscription.Kind)
        {
            case SubscriptionKind.Operations:
                return access.IsOperations;
            case SubscriptionKind.Hub:
                return access.Roles.Count > 0 && access.Hubs.ContainsKey(subscription.ResourceId!.Value);
            default:
                if (access.Roles.Count == 0)
                {
                    return false;
                }

                var shipmentId = subscription.ResourceId!.Value;
                var hubs = await db.Set<Shipment>().AsNoTracking()
                    .Where(s => s.Id == shipmentId)
                    .Select(s => new { s.OriginHubId, s.DestinationHubId, s.CurrentHubId })
                    .FirstOrDefaultAsync(cancellationToken);
                if (hubs is null)
                {
                    return false;
                }

                return access.IsOperations
                    || access.Hubs.ContainsKey(hubs.OriginHubId)
                    || access.Hubs.ContainsKey(hubs.DestinationHubId)
                    || (hubs.CurrentHubId is { } current && access.Hubs.ContainsKey(current));
        }
    }
}

/// <summary>
/// Canlı panel hub'ı (<c>/realtime/operations</c>). Abonelikler sunucu tarafından yetkilendirilir ve gruplar sunucuda adlandırılır.
/// Hata mesajları varlık sızdırmaz: yetkisiz ve bulunamayan gönderi aynı <c>not-found</c> yanıtını alır.
/// </summary>
[Authorize(Policy = Policies.InternalUser)]
internal sealed partial class OperationsRealtimeHub(
    RealtimeConnectionRegistry registry, IActorAccessReader accessReader, RealtimeAuthorizer authorizer, LogisticsDbContext db)
    : Hub<IOperationsRealtimeClient>
{
    public const string Path = "/realtime/operations";
    public const int MaxSubscriptionsPerConnection = 32;

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.GetUserId() ?? throw new HubException("unauthenticated");
        DateTimeOffset? expiresAt = null;
        if (Context.GetHttpContext() is { } httpContext)
        {
            var ticket = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            expiresAt = ticket.Properties?.ExpiresUtc;
        }

        registry.Add(new RealtimeConnection(Context.ConnectionId, userId, expiresAt, Context));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Tüm gönderi ve istisna değişiklikleri: yalnız OpsAgent/OpsManager.</summary>
    public Task SubscribeOperations() =>
        SubscribeAsync(new RealtimeSubscription(SubscriptionKind.Operations, string.Empty, null), "forbidden");

    /// <summary>Bir hub'la ilişkili gönderi değişiklikleri: yalnız o hub'a atanmış kullanıcı.</summary>
    public async Task SubscribeHub(string hubCode)
    {
        if (hubCode is null || !HubCodeRegex().IsMatch(hubCode))
        {
            throw new HubException("forbidden");
        }

        var hubId = await db.Set<Lst.Platform.Operations.Hub>().AsNoTracking().Where(h => h.Code == hubCode).Select(h => (Guid?)h.Id).FirstOrDefaultAsync(Context.ConnectionAborted);
        if (hubId is null)
        {
            // Bilinmeyen hub ile atanmamış hub ayırt edilmez.
            throw new HubException("forbidden");
        }

        await SubscribeAsync(new RealtimeSubscription(SubscriptionKind.Hub, hubCode, hubId), "forbidden");
    }

    /// <summary>Tek gönderi: detay okuma ile aynı kapsam kuralı.</summary>
    public Task SubscribeShipment(Guid shipmentId) =>
        SubscribeAsync(new RealtimeSubscription(SubscriptionKind.Shipment, shipmentId.ToString(), shipmentId), "not-found");

    public async Task UnsubscribeShipment(Guid shipmentId)
    {
        var group = RealtimeSubscription.ShipmentGroup(shipmentId);
        if (registry.Find(Context.ConnectionId)?.Remove(group) == true)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        }
    }

    private async Task SubscribeAsync(RealtimeSubscription subscription, string denialCode)
    {
        var connection = registry.Find(Context.ConnectionId) ?? throw new HubException("unauthenticated");
        var access = await accessReader.GetAsync(connection.UserId, Context.ConnectionAborted);
        if (!await authorizer.IsAllowedAsync(access, subscription, Context.ConnectionAborted))
        {
            throw new HubException(denialCode);
        }

        if (connection.SubscriptionCount >= MaxSubscriptionsPerConnection && !connection.Subscriptions.Any(s => s.Group == subscription.Group))
        {
            throw new HubException("too-many-subscriptions");
        }

        // Önce kayıt, sonra grup: yeniden doğrulama gruba eklenmiş ama kayıtsız bir abonelik görmez.
        connection.Add(subscription);
        await Groups.AddToGroupAsync(Context.ConnectionId, subscription.Group, Context.ConnectionAborted);
    }

    [GeneratedRegex("^[A-Z]{3}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex HubCodeRegex();
}
