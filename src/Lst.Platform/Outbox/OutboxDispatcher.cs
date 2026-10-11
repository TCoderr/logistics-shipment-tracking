using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Platform.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Workers:Outbox";

    public int BatchSize { get; set; } = 50;

    public int LeaseSeconds { get; set; } = 60;

    public int MaxAttempts { get; set; } = 8;

    public int PollMilliseconds { get; set; } = 1000;

    public int HandlerTimeoutSeconds { get; set; } = 10;
}

public sealed record OutboxEnvelope(Guid MessageId, string Type, string Payload, DateTimeOffset OccurredAt)
{
    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload, OutboxJson.Options)
        ?? throw new InvalidDataException("Outbox payload is empty.");
}

/// <summary>Commit sonrası yan etki. At-least-once çağrılır; uygulama <see cref="OutboxEnvelope.MessageId"/> ile tekrarı zararsız kılmalıdır.</summary>
public interface IOutboxHandler
{
    string MessageType { get; }

    Task HandleAsync(OutboxEnvelope envelope, CancellationToken cancellationToken);
}

public sealed record ClaimedOutboxMessage(long Id, Guid MessageId, string Type, string Payload, DateTimeOffset OccurredAt, Guid LockToken, short Attempts)
{
    public OutboxEnvelope Envelope => new(MessageId, Type, Payload, OccurredAt);
}

public enum OutboxResult
{
    Processed,
    LostLease,
    TransientFailure,
    RetryScheduled,
    DeadLettered,
}

/// <summary>
/// Outbox dağıtıcısı (ADR-017). Claim kısa transaction'da lease + yeni lock_token; handler'lar DB transaction'ı dışında ve zaman aşımıyla çalışır;
/// sonuç token koşuluyla yazılır (fencing). Handler'ı olmayan mesaj işlenmiş sayılmaz: hata olarak kaydedilir, sonunda dead-letter olur ve
/// handler eklendikten sonra yetkili replay ile yeniden kuyruğa alınabilir. Transient DB hatası deneme sayısını artırmaz.
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger)
{
    public const string NoHandlerError = "no-handler";
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private readonly string workerId = $"outbox:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(options.Value.BatchSize, null, cancellationToken);
        foreach (var message in claimed)
        {
            await ProcessAsync(message, cancellationToken);
        }

        return claimed.Count;
    }

    /// <summary><paramref name="onlyMessageIds"/> verilirse yalnız bu mesajlar arasından claim edilir (testler ve operasyon aracı).</summary>
    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(int batchSize, IReadOnlyCollection<Guid>? onlyMessageIds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNowMicroseconds();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE platform.outbox_messages o
               SET lock_token = gen_random_uuid(), locked_until = @lease_until, locked_by = @worker
             WHERE o.id IN (
                   SELECT id FROM platform.outbox_messages
                    WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND available_at <= @now
                      AND (locked_until IS NULL OR locked_until < @now)
                      AND (@only::uuid[] IS NULL OR message_id = ANY(@only))
                    ORDER BY available_at, id
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
            RETURNING o.id, o.message_id, o.type, o.payload::text, o.occurred_at, o.lock_token, o.attempts
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        command.Parameters.Add(new NpgsqlParameter("lease_until", NpgsqlDbType.TimestampTz) { Value = now.AddSeconds(options.Value.LeaseSeconds) });
        command.Parameters.Add(new NpgsqlParameter("worker", NpgsqlDbType.Varchar) { Value = workerId });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter("only", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = onlyMessageIds is null ? DBNull.Value : onlyMessageIds.ToArray(),
        });
        var result = new List<ClaimedOutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ClaimedOutboxMessage(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetGuid(5), reader.GetInt16(6)));
        }

        return result.OrderBy(m => m.Id).ToList();
    }

    public async Task<OutboxResult> ProcessAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var handled = await RunHandlersAsync(message, cancellationToken);
            if (!handled)
            {
                LogNoHandler(logger, message.MessageId, message.Type);
                return await RecordFailureAsync(message, NoHandlerError, cancellationToken);
            }

            var updated = await ExecuteFencedAsync(message,
                "processed_at = @now, last_error = NULL, lock_token = NULL, locked_until = NULL, locked_by = NULL",
                cancellationToken,
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = timeProvider.GetUtcNowMicroseconds() });
            return updated == 1 ? OutboxResult.Processed : OutboxResult.LostLease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (TransientFailures.IsTransient(ex))
        {
            LogTransient(logger, message.MessageId, ex.GetType().Name);
            await TryReleaseAsync(message, ex.GetType().Name);
            return OutboxResult.TransientFailure;
        }
#pragma warning disable CA1031 // Handler hatası mesaj üzerinde kayıt altına alınır; dağıtıcı durmaz.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHandlerFailed(logger, message.MessageId, message.Type, ex.GetType().Name);
            try
            {
                return await RecordFailureAsync(message, ex.GetType().FullName ?? ex.GetType().Name, cancellationToken);
            }
            catch (Exception recordEx) when (TransientFailures.IsTransient(recordEx))
            {
                return OutboxResult.TransientFailure;
            }
        }
    }

    /// <summary>Kayıtlı handler'ları çalıştırır, sonucu yazmaz. Handler yoksa false. (Testlerde "handler sonrası, processed_at öncesi çökme" için de kullanılır.)</summary>
    internal async Task<bool> RunHandlersAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>()
            .Where(h => string.Equals(h.MessageType, message.Type, StringComparison.Ordinal))
            .ToList();
        if (handlers.Count == 0)
        {
            return false;
        }

        foreach (var handler in handlers)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.HandlerTimeoutSeconds));
            await handler.HandleAsync(message.Envelope, timeout.Token);
        }

        return true;
    }

    private async Task<OutboxResult> RecordFailureAsync(ClaimedOutboxMessage message, string error, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNowMicroseconds();
        var attempts = message.Attempts + 1;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (attempts >= options.Value.MaxAttempts)
        {
            var dead = await ExecuteFencedAsync(db, message,
                "attempts = @attempts, last_error = @error, dead_lettered_at = @now, lock_token = NULL, locked_until = NULL, locked_by = NULL",
                cancellationToken,
                new NpgsqlParameter("attempts", NpgsqlDbType.Smallint) { Value = (short)attempts },
                new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = error },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            if (dead != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return OutboxResult.LostLease;
            }

            db.Add(AuditEntry.SystemSucceeded(now, "system:outbox", OutboxAuditActions.DeadLettered, "OutboxMessage", message.MessageId.ToString(),
                Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(new { type = message.Type, attempts, error })));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            LogDeadLettered(logger, message.MessageId, message.Type, attempts);
            return OutboxResult.DeadLettered;
        }

        var retried = await ExecuteFencedAsync(db, message,
            "attempts = @attempts, last_error = @error, available_at = @next, lock_token = NULL, locked_until = NULL, locked_by = NULL",
            cancellationToken,
            new NpgsqlParameter("attempts", NpgsqlDbType.Smallint) { Value = (short)attempts },
            new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = error },
            new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = now + Backoff(attempts) });
        await transaction.CommitAsync(CancellationToken.None);
        return retried == 1 ? OutboxResult.RetryScheduled : OutboxResult.LostLease;
    }

    private async Task TryReleaseAsync(ClaimedOutboxMessage message, string errorType)
    {
        try
        {
            await ExecuteFencedAsync(message, "available_at = @next, last_error = @error, lock_token = NULL, locked_until = NULL, locked_by = NULL",
                CancellationToken.None,
                new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = timeProvider.GetUtcNowMicroseconds() + TransientRetryDelay },
                new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = "transient:" + errorType });
        }
#pragma warning disable CA1031 // DB erişilemiyorsa lease zaman aşımı yeterlidir.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary><c>min(2^n × 5 sn, 15 dk)</c> + en fazla %20 jitter.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        var baseSeconds = Math.Min(Math.Pow(2, Math.Clamp(attempts, 1, 20)) * 5, MaxBackoff.TotalSeconds);
        var jitter = baseSeconds * 0.2 * RandomNumberGenerator.GetInt32(0, 1001) / 1000.0;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }

    private async Task<int> ExecuteFencedAsync(ClaimedOutboxMessage message, string setClause, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        return await ExecuteFencedAsync(db, message, setClause, cancellationToken, parameters);
    }

    private static async Task<int> ExecuteFencedAsync(
        LogisticsDbContext db, ClaimedOutboxMessage message, string setClause, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE platform.outbox_messages SET {setClause} WHERE id = @id AND lock_token = @token AND processed_at IS NULL AND dead_lettered_at IS NULL",
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = message.Id });
        command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Uuid) { Value = message.LockToken });
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) has no handler; not marked processed")]
    private static partial void LogNoHandler(ILogger logger, Guid messageId, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} transient failure ({ErrorType}); lease released without consuming an attempt")]
    private static partial void LogTransient(ILogger logger, Guid messageId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({Type}) handler failed ({ErrorType})")]
    private static partial void LogHandlerFailed(ILogger logger, Guid messageId, string type, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({Type}) dead-lettered after {Attempts} attempts")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string type, int attempts);
}

public static class OutboxAuditActions
{
    public const string DeadLettered = "OutboxMessageDeadLettered";
    public const string Replayed = "OutboxMessageReplayed";
}
