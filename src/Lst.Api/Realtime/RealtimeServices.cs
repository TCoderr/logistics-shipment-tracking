using Lst.Modules.Identity;
using Lst.Platform.Outbox;
using Lst.Platform.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Lst.Api.Realtime;

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>Açık bağlantıların yetkisinin güncel DB durumuna göre yeniden doğrulanma aralığı. İptal gecikmesinin üst sınırıdır.</summary>
    public int RevalidateSeconds { get; set; } = 15;
}

/// <summary>
/// Outbox mesajlarını PII'siz geçersizleme ipuçlarına çevirir ve yalnız ilgili sunucu gruplarına yollar.
/// Outbox dağıtıcısı tarafından commit'ten SONRA çağrılır: API komutunun başarısı bu gönderime bağlı değildir. Gönderim hata verirse
/// outbox mesajı işlenmemiş kalır ve yeniden denenir; aynı ipucunun iki kez gitmesi zararsızdır (istemci yalnız yeniden okur).
/// </summary>
internal sealed class SignalRRealtimeNotifier(IHubContext<OperationsRealtimeHub, IOperationsRealtimeClient> hub) : IRealtimeNotifier
{
    private sealed record StatusChanged(Guid ShipmentId, string StatusAfter, IReadOnlyList<Guid>? HubIds);

    private sealed record ExceptionEvent(Guid ExceptionId, Guid ShipmentId, Guid? HubId);

    public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.Type)
        {
            case OutboxMessageTypes.ShipmentStatusChanged:
            {
                var payload = envelope.Read<StatusChanged>();
                var groups = Groups(payload.ShipmentId, payload.HubIds ?? []);
                return hub.Clients.Groups(groups).ShipmentChanged(new ShipmentChangedMessage(payload.ShipmentId, payload.StatusAfter));
            }

            case OutboxMessageTypes.ExceptionOpened:
                return PublishExceptionAsync(envelope, "Open");
            case OutboxMessageTypes.ExceptionAssigned:
                return PublishExceptionAsync(envelope, "Assigned");
            case OutboxMessageTypes.ExceptionResolved:
                return PublishExceptionAsync(envelope, "Resolved");
            default:
                // Bilinmeyen tip için sessizce "işlendi" denmez.
                throw new InvalidOperationException("Realtime notifier cannot handle outbox message type.");
        }
    }

    private Task PublishExceptionAsync(OutboxEnvelope envelope, string status)
    {
        var payload = envelope.Read<ExceptionEvent>();
        var groups = Groups(payload.ShipmentId, payload.HubId is { } hubId ? [hubId] : []);
        return hub.Clients.Groups(groups).ExceptionChanged(new ExceptionChangedMessage(payload.ExceptionId, payload.ShipmentId, status));
    }

    private static List<string> Groups(Guid shipmentId, IReadOnlyList<Guid> hubIds)
    {
        var groups = new List<string>(hubIds.Count + 2) { RealtimeSubscription.OperationsGroup, RealtimeSubscription.ShipmentGroup(shipmentId) };
        foreach (var hubId in hubIds.Distinct())
        {
            groups.Add(RealtimeSubscription.HubGroup(hubId));
        }

        return groups;
    }
}

internal sealed record RevalidationResult(int Connections, int ClosedConnections, int RevokedSubscriptions);

/// <summary>
/// Açık bağlantıların yetkisini güncel DB durumuna göre yeniden doğrular. Cookie güvenlik damgası veya bağlantı anındaki claim'ler
/// grup üyeliğini kendiliğinden temizlemez; bu yüzden: (1) kimlik bileti süresi dolan bağlantı kapatılır, (2) rolü kalmayan (veya kilitli)
/// kullanıcının bağlantısı kapatılır, (3) artık yetkili olunmayan abonelik gruptan çıkarılır ve istemciye bildirilir.
/// </summary>
internal sealed partial class RealtimeRevalidator(
    RealtimeConnectionRegistry registry,
    IServiceScopeFactory scopeFactory,
    IHubContext<OperationsRealtimeHub, IOperationsRealtimeClient> hub,
    TimeProvider timeProvider,
    ILogger<RealtimeRevalidator> logger)
{
    public async Task<RevalidationResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var connections = registry.Snapshot();
        var closed = 0;
        var revoked = 0;
        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connection.TicketExpiresAt is { } expiresAt && expiresAt <= now)
            {
                connection.Abort();
                closed++;
                continue;
            }

            // Kapsam başına taze okuma: ActorAccessReader istek kapsamında önbellek tutar.
            await using var scope = scopeFactory.CreateAsyncScope();
            var access = await scope.ServiceProvider.GetRequiredService<IActorAccessReader>().GetAsync(connection.UserId, cancellationToken);
            if (access.Roles.Count == 0)
            {
                connection.Abort();
                closed++;
                continue;
            }

            var authorizer = scope.ServiceProvider.GetRequiredService<RealtimeAuthorizer>();
            foreach (var subscription in connection.Subscriptions)
            {
                if (await authorizer.IsAllowedAsync(access, subscription, cancellationToken))
                {
                    continue;
                }

                connection.Remove(subscription.Group);
                await hub.Groups.RemoveFromGroupAsync(connection.ConnectionId, subscription.Group, cancellationToken);
                await hub.Clients.Client(connection.ConnectionId).SubscriptionRevoked(new SubscriptionRevokedMessage(subscription.Kind.ToString(), subscription.Key));
                revoked++;
            }
        }

        if (closed > 0 || revoked > 0)
        {
            LogRevalidated(logger, connections.Count, closed, revoked);
        }

        return new RevalidationResult(connections.Count, closed, revoked);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Realtime revalidation: {Connections} connections, {Closed} closed, {Revoked} subscriptions revoked")]
    private static partial void LogRevalidated(ILogger logger, int connections, int closed, int revoked);
}

internal sealed partial class RealtimeRevalidationWorker(
    RealtimeRevalidator revalidator, IOptions<RealtimeOptions> options, IConfiguration configuration, TimeProvider timeProvider, ILogger<RealtimeRevalidationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Workers:Enabled", true))
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(options.Value.RevalidateSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
                await revalidator.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Arka plan döngüsü tek hatayla ölmemeli; hata tipi loglanır, sonraki turda yeniden denenir.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogFailed(logger, exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime revalidation failed ({ErrorType}); retrying on next interval")]
    private static partial void LogFailed(ILogger logger, string errorType);
}

internal static class RealtimeRegistration
{
    public static IServiceCollection AddLstRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.SectionName))
            .Validate(o => o.RevalidateSeconds is >= 1 and <= 3600, "Realtime:RevalidateSeconds 1 ile 3600 arasında olmalı.")
            .ValidateOnStart();
        services.AddSignalR(options =>
        {
            // İstemciden yalnız küçük abonelik çağrıları gelir.
            options.MaximumReceiveMessageSize = 4 * 1024;
            options.EnableDetailedErrors = false;
        });
        services.AddSingleton<RealtimeConnectionRegistry>();
        services.Replace(ServiceDescriptor.Singleton<IRealtimeSessions>(sp => sp.GetRequiredService<RealtimeConnectionRegistry>()));
        services.AddScoped<RealtimeAuthorizer>();
        services.AddSingleton<RealtimeRevalidator>();
        services.AddHostedService<RealtimeRevalidationWorker>();
        // Outbox'ın varsayılan (hiçbir şey yayınlamayan) bildiricisinin yerine geçer.
        services.Replace(ServiceDescriptor.Singleton<IRealtimeNotifier, SignalRRealtimeNotifier>());
        return services;
    }

    /// <summary>
    /// Hub yalnız aynı origin'den kullanılabilir. Cookie'ler SameSite=Strict olduğundan siteler arası WebSocket el sıkışması zaten kimliksizdir;
    /// buna ek olarak Origin başlığı varsa isteğin host'uyla eşleşmelidir (cross-site WebSocket hijacking'e karşı ikinci savunma).
    /// </summary>
    public static IApplicationBuilder UseRealtimeOriginCheck(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/realtime", StringComparison.OrdinalIgnoreCase)
                && context.Request.Headers.Origin is { Count: > 0 } origin
                && !IsSameHost(origin.ToString(), context.Request.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

    public static void MapLstRealtime(this IEndpointRouteBuilder app) =>
        app.MapHub<OperationsRealtimeHub>(OperationsRealtimeHub.Path).RequireAuthorization(Policies.InternalUser);

    private static bool IsSameHost(string origin, HostString host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, host.Value, StringComparison.OrdinalIgnoreCase);
}
