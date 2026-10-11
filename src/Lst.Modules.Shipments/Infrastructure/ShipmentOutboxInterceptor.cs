using Lst.Modules.Shipments.Domain;
using Lst.Platform.Outbox;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lst.Modules.Shipments.Infrastructure;

/// <summary>Durum değişikliği outbox payload'ı: yalnız kimlik, durum ve hub kimlikleri (PII yok).</summary>
public sealed record ShipmentStatusChangedPayload(Guid ShipmentId, string? StatusBefore, string StatusAfter, DateTimeOffset OccurredAt, IReadOnlyList<Guid> HubIds)
    : IOutboxPayload
{
    public static string MessageType => OutboxMessageTypes.ShipmentStatusChanged;
}

public sealed record RecipientNotificationOutboxPayload(Guid ShipmentId, string Template) : IOutboxPayload
{
    public static string MessageType => OutboxMessageTypes.RecipientNotification;
}

/// <summary>
/// Gönderi durumu değiştiğinde (veya gönderi oluştuğunda) outbox mesajını aynı SaveChanges'a ekler; böylece durum değişikliği ve mesaj
/// aynı transaction'da commit olur ya da birlikte geri alınır (ADR-017). Tek merkez: hiçbir komut yolu mesajı unutamaz.
/// </summary>
internal sealed class ShipmentOutboxInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    private static readonly Dictionary<ShipmentStatus, string> NotificationTemplates = new()
    {
        [ShipmentStatus.OutForDelivery] = "OutForDelivery",
        [ShipmentStatus.Delivered] = "Delivered",
        [ShipmentStatus.DeliveryFailed] = "DeliveryFailed",
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Enqueue(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Enqueue(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Enqueue(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNowMicroseconds();
        foreach (var entry in context.ChangeTracker.Entries<Shipment>().ToList())
        {
            string? before;
            if (entry.State == EntityState.Added)
            {
                before = null;
            }
            else if (entry.State == EntityState.Modified && entry.Property(s => s.Status) is { IsModified: true } status
                && status.OriginalValue != status.CurrentValue)
            {
                before = status.OriginalValue.ToString();
            }
            else
            {
                continue;
            }

            var shipment = entry.Entity;
            var hubs = new[] { shipment.OriginHubId, shipment.DestinationHubId }
                .Concat(shipment.CurrentHubId is { } current ? new[] { current } : Array.Empty<Guid>())
                .Distinct()
                .ToArray();
            context.Add(OutboxMessage.Create(
                new ShipmentStatusChangedPayload(shipment.Id, before, shipment.Status.ToString(), shipment.StatusOccurredAt, hubs), now));
            if (NotificationTemplates.TryGetValue(shipment.Status, out var template))
            {
                context.Add(OutboxMessage.Create(new RecipientNotificationOutboxPayload(shipment.Id, template), now));
            }
        }
    }
}
