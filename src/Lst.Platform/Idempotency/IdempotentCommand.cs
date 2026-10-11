using System.Data;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Platform.Idempotency;

/// <summary>İdempotent komutun iş adımının sonucu: başarı (saklanacak yanıt) veya ret (transaction geri alınır, saklanmaz).</summary>
public abstract record IdempotentWorkResult
{
    private IdempotentWorkResult()
    {
    }

    public sealed record Success(StoredResponse Response, Guid? ResourceId) : IdempotentWorkResult;

    public sealed record Refused(object Reason) : IdempotentWorkResult;
}

public abstract record IdempotentCommandOutcome
{
    private IdempotentCommandOutcome()
    {
    }

    public sealed record Completed(StoredResponse Response) : IdempotentCommandOutcome;

    public sealed record Replayed(StoredResponse Response) : IdempotentCommandOutcome;

    public sealed record KeyReused : IdempotentCommandOutcome;

    public sealed record InProgress : IdempotentCommandOutcome;

    public sealed record Refused(object Reason) : IdempotentCommandOutcome;

    public sealed record CommitOutcomeUnknown : IdempotentCommandOutcome;
}

/// <summary>
/// Kalıcı idempotency ile tek transaction'da komut: lock_timeout → claim (ON CONFLICT + FOR UPDATE) → iş → yanıtın kaydı → COMMIT.
/// Yalnız başarı yanıtları saklanır; ret ve hata rollback olur. Lock timeout (55P03) <see cref="IdempotentCommandOutcome.InProgress"/> olur;
/// aborted transaction'da devam edilmez. COMMIT hatasında başarı varsayılmaz.
/// </summary>
public static class IdempotentCommand
{
    public static async Task<IdempotentCommandOutcome> RunAsync(
        DbContext db,
        IdempotencyScope scope,
        byte[] requestHash,
        short requestHashVersion,
        DateTimeOffset nowUtc,
        TimeSpan retention,
        Func<CancellationToken, Task<IdempotentWorkResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(work);

        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        StoredResponse response;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", cancellationToken);
            var claim = await IdempotencyStore.ClaimAsync(db, scope, requestHash, requestHashVersion, nowUtc, retention, cancellationToken);
            switch (claim)
            {
                case ClaimResult.Replay replay:
                    await transaction.RollbackAsync(cancellationToken);
                    return new IdempotentCommandOutcome.Replayed(replay.Response);
                case ClaimResult.KeyReused:
                    await transaction.RollbackAsync(cancellationToken);
                    return new IdempotentCommandOutcome.KeyReused();
                case ClaimResult.Busy:
                    await transaction.RollbackAsync(cancellationToken);
                    return new IdempotentCommandOutcome.InProgress();
            }

            var result = await work(cancellationToken);
            if (result is IdempotentWorkResult.Refused refused)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new IdempotentCommandOutcome.Refused(refused.Reason);
            }

            var success = (IdempotentWorkResult.Success)result;
            response = success.Response;
            await IdempotencyStore.CompleteAsync(db, ((ClaimResult.Claimed)claim).RecordId, response, success.ResourceId, nowUtc, cancellationToken);
        }
        catch (Exception ex) when (PostgresErrors.IsLockTimeout(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new IdempotentCommandOutcome.InProgress();
        }

        try
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Commit belirsizliği ayrı sonuçla raporlanır; başarı varsayılmaz.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new IdempotentCommandOutcome.CommitOutcomeUnknown();
        }

        return new IdempotentCommandOutcome.Completed(response);
    }
}
