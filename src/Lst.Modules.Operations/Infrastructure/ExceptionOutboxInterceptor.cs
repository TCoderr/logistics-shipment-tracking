using Lst.Modules.Operations.Domain;
using Lst.Platform.Outbox;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lst.Modules.Operations.Infrastructure;

public sealed record ExceptionOpenedPayload(Guid ExceptionId, Guid ShipmentId, string Type, string Severity, Guid? HubId) : IOutboxPayload
{
    public static string MessageType => OutboxMessageTypes.ExceptionOpened;
}

/// <summary>Atama değişikliği: canlı kuyruğun tazelenmesi için. Atanan kullanıcı kimliği yazılmaz.</summary>
public sealed record ExceptionAssignedPayload(Guid ExceptionId, Guid ShipmentId, Guid? HubId) : IOutboxPayload
{
    public static string MessageType => OutboxMessageTypes.ExceptionAssigned;
}

public sealed record ExceptionResolvedPayload(Guid ExceptionId, Guid ShipmentId, string? ResolutionCode) : IOutboxPayload
{
    public static string MessageType => OutboxMessageTypes.ExceptionResolved;
}

/// <summary>İstisna açılış/atama/çözümünü aynı SaveChanges'a outbox mesajı olarak ekler (ADR-017). Not ve kullanıcı bilgisi yazılmaz.</summary>
internal sealed class ExceptionOutboxInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
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
        foreach (var entry in context.ChangeTracker.Entries<ShipmentException>().ToList())
        {
            var exception = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                context.Add(OutboxMessage.Create(
                    new ExceptionOpenedPayload(exception.Id, exception.ShipmentId, exception.Type.ToString(), exception.Severity.ToString(), exception.HubId), now));
            }
            else if (entry.State == EntityState.Modified && entry.Property(e => e.Status) is { IsModified: true } status
                && status.OriginalValue != ExceptionStatus.Resolved && status.CurrentValue == ExceptionStatus.Resolved)
            {
                context.Add(OutboxMessage.Create(
                    new ExceptionResolvedPayload(exception.Id, exception.ShipmentId, exception.ResolutionCode?.ToString()), now));
            }
            else if (entry.State == EntityState.Modified && entry.Property(e => e.AssignedToUserId).IsModified
                && exception.Status != ExceptionStatus.Resolved)
            {
                context.Add(OutboxMessage.Create(new ExceptionAssignedPayload(exception.Id, exception.ShipmentId, exception.HubId), now));
            }
        }
    }
}
