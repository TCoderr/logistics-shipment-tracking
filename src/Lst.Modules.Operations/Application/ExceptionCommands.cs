using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Lst.Modules.Identity;
using Lst.Modules.Operations.Domain;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Auditing;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Modules.Operations.Application;

public sealed record AssignExceptionRequest
{
    public Guid? AssigneeUserId { get; init; }
}

public sealed record ResolveExceptionRequest
{
    public string? ResolutionCode { get; init; }

    public string? Note { get; init; }
}

public abstract record ExceptionCommandOutcome
{
    private ExceptionCommandOutcome()
    {
    }

    public sealed record Succeeded(ExceptionItem Item) : ExceptionCommandOutcome;

    public sealed record NotFound : ExceptionCommandOutcome;

    public sealed record PreconditionFailed(string CurrentETag) : ExceptionCommandOutcome;

    public sealed record Forbidden(string Detail) : ExceptionCommandOutcome;

    public sealed record Invalid(string Code, string Detail) : ExceptionCommandOutcome;

    public sealed record Conflict(string Code, string? CurrentStatus) : ExceptionCommandOutcome;

    public sealed record Busy : ExceptionCommandOutcome;
}

/// <summary>
/// İstisna atama/çözüm. Kilit sırası her zaman gönderi → istisna (teslimat komutlarıyla aynı sıra; deadlock yok).
/// Gönderi aksiyonu gerektiren çözüm (ReattemptApproved, ReturnToSenderInitiated) aynı transaction'da <see cref="IShipmentOperations"/> ile yapılır;
/// aksiyon reddedilirse çözüm de geri alınır.
/// </summary>
public sealed class ExceptionCommandHandler(
    LogisticsDbContext db,
    IShipmentOperations shipmentOperations,
    IActorAccessReader accessReader,
    TimeProvider timeProvider)
{
    public const int MaxNoteLength = 1000;

    public Task<ExceptionCommandOutcome> AssignAsync(Guid exceptionId, string ifMatch, Guid assigneeUserId, ActorAccess actor, CancellationToken cancellationToken) =>
        RunAsync(exceptionId, ifMatch, lockShipment: false, async (exception, now, ct) =>
        {
            if (!actor.Roles.Contains(Roles.OpsManager) && assigneeUserId != actor.UserId)
            {
                return new ExceptionCommandOutcome.Forbidden("Operasyon görevlisi yalnız kendini atayabilir.");
            }

            var assignee = await accessReader.GetAsync(assigneeUserId, ct);
            if (!assignee.IsOperations)
            {
                return new ExceptionCommandOutcome.Invalid("invalid-assignee", "Atanacak kullanıcı operasyon personeli olmalı.");
            }

            exception.Assign(assigneeUserId, now);
            Audit(actor.UserId, now, OperationsAuditActions.ExceptionAssigned, exception, new { assigneeUserId });
            return null;
        }, cancellationToken);

    public Task<ExceptionCommandOutcome> ResolveAsync(
        Guid exceptionId, string ifMatch, ResolutionCode code, string note, ActorAccess actor, CancellationToken cancellationToken) =>
        RunAsync(exceptionId, ifMatch, lockShipment: true, async (exception, now, ct) =>
        {
            if (!actor.Roles.Contains(Roles.OpsManager) && exception.AssignedToUserId != actor.UserId)
            {
                return new ExceptionCommandOutcome.Forbidden("İstisnayı çözmek için önce üzerinize alın (veya operasyon yöneticisi olun).");
            }

            if (!ExceptionResolutionPolicy.IsAllowed(exception.Type, code))
            {
                return new ExceptionCommandOutcome.Invalid("resolution-not-allowed",
                    $"{exception.Type} için geçerli çözümler: {string.Join(", ", ExceptionResolutionPolicy.AllowedFor(exception.Type))}.");
            }

            var actorContext = new ActorContext(actor.UserId, nameof(EventSource.Operator));
            var action = code switch
            {
                ResolutionCode.ReattemptApproved => await shipmentOperations.GrantExtraAttemptAsync(exception.ShipmentId, actorContext, ct),
                ResolutionCode.ReturnToSenderInitiated => await shipmentOperations.StartReturnToSenderAsync(exception.ShipmentId, actorContext, ct),
                _ => ShipmentOperationResult.Ok,
            };
            if (!action.Succeeded)
            {
                return new ExceptionCommandOutcome.Conflict(action.Code ?? "transition-not-allowed", action.CurrentStatus);
            }

            exception.Resolve(code, note, actor.UserId, now);
            // Not serbest metindir; audit'e yalnız varlığı yazılır.
            Audit(actor.UserId, now, OperationsAuditActions.ExceptionResolved, exception, new { resolutionCode = code.ToString(), noteProvided = true });
            return null;
        }, cancellationToken);

    private async Task<ExceptionCommandOutcome> RunAsync(
        Guid exceptionId,
        string ifMatch,
        bool lockShipment,
        Func<ShipmentException, DateTimeOffset, CancellationToken, Task<ExceptionCommandOutcome?>> apply,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNowMicroseconds();
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);
            var shipmentId = await db.Set<ShipmentException>().AsNoTracking()
                .Where(x => x.Id == exceptionId).Select(x => (Guid?)x.ShipmentId).FirstOrDefaultAsync(cancellationToken);
            if (shipmentId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ExceptionCommandOutcome.NotFound();
            }

            if (lockShipment)
            {
                await LockAsync("shipments.shipments", shipmentId.Value, cancellationToken);
            }

            await LockAsync("operations.shipment_exceptions", exceptionId, cancellationToken);
            var exception = await db.Set<ShipmentException>().FirstAsync(x => x.Id == exceptionId, cancellationToken);
            var current = ExceptionETag.From(exception.Version);
            if (!string.Equals(current, ifMatch, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ExceptionCommandOutcome.PreconditionFailed(current);
            }

            if (!exception.IsOpen)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ExceptionCommandOutcome.Conflict("exception-resolved", null);
            }

            var refusal = await apply(exception, now, cancellationToken);
            if (refusal is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return refusal;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);

            var shipment = await db.Set<Shipment>().AsNoTracking().Where(s => s.Id == exception.ShipmentId)
                .Select(s => new { s.TrackingNumber, s.Status }).FirstAsync(cancellationToken);
            var hubs = await db.Set<Hub>().AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, cancellationToken);
            var assignee = exception.AssignedToUserId is { } a
                ? await db.Users.AsNoTracking().Where(u => u.Id == a).Select(u => new AssigneeView(u.Id, u.DisplayName)).FirstOrDefaultAsync(cancellationToken)
                : null;
            return new ExceptionCommandOutcome.Succeeded(ExceptionQueueHandler.ToItem(exception, shipment.TrackingNumber, shipment.Status, assignee, hubs, now));
        }
        catch (Exception ex) when (PostgresErrors.IsLockTimeout(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new ExceptionCommandOutcome.Busy();
        }
    }

    private async Task LockAsync(string table, Guid id, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
#pragma warning disable CA2100 // Tablo adı sabit iki değerden biridir; id parametredir.
        await using var command = new NpgsqlCommand($"SELECT 1 FROM {table} WHERE id = @id FOR UPDATE", connection,
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
#pragma warning restore CA2100
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private void Audit(Guid userId, DateTimeOffset now, string action, ShipmentException exception, object changes) =>
        db.Add(AuditEntry.UserSucceeded(now, userId, action, "ShipmentException", exception.Id.ToString(), Activity.Current?.TraceId.ToString(),
            JsonSerializer.Serialize(changes)));
}
