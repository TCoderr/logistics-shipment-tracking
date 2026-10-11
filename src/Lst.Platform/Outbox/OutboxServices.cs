using System.Diagnostics;
using System.Text.Json;
using Lst.Platform.Auditing;
using Lst.Platform.Persistence;
using Lst.Platform.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Lst.Platform.Outbox;

/// <summary>Sürümlü outbox mesaj tipleri (docs/05 §8).</summary>
public static class OutboxMessageTypes
{
    public const string ShipmentStatusChanged = "shipment.status-changed.v1";
    public const string ExceptionOpened = "shipment.exception-opened.v1";
    public const string ExceptionAssigned = "shipment.exception-assigned.v1";
    public const string ExceptionResolved = "shipment.exception-resolved.v1";
    public const string RecipientNotification = "notification.recipient-update.v1";
}

/// <summary>
/// Canlı panel invalidation yayını (Dilim 10'da SignalR). Varsayılan uygulama hiçbir şey yayınlamaz; yine de outbox mesajı handler'ı olduğu için işlenir.
/// </summary>
public interface IRealtimeNotifier
{
    Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken);
}

internal sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RealtimeInvalidationHandler(string messageType, IRealtimeNotifier notifier) : IOutboxHandler
{
    public string MessageType { get; } = messageType;

    public Task HandleAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) => notifier.PublishAsync(envelope, cancellationToken);
}

public sealed record RecipientNotificationPayload(Guid ShipmentId, string Template);

/// <summary>
/// Simüle alıcı bildirim kanalı: gerçek SMS/e-posta gönderilmez. Teslim kaydı message_id ile tekildir (tüketici tarafı tekrar ayıklama);
/// log yalnız kimlik ve şablon adı içerir, alıcı iletişim bilgisi okunmaz ve yazılmaz.
/// </summary>
internal sealed partial class SimulatedNotificationHandler(LogisticsDbContext db, TimeProvider timeProvider, ILogger<SimulatedNotificationHandler> logger) : IOutboxHandler
{
    public string MessageType => OutboxMessageTypes.RecipientNotification;

    public async Task HandleAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = envelope.Read<RecipientNotificationPayload>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.notification_deliveries (message_id, shipment_id, template, recorded_at)
            VALUES (@message_id, @shipment_id, @template, @now)
            ON CONFLICT (message_id) DO NOTHING
            """, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.Add(new NpgsqlParameter("message_id", NpgsqlDbType.Uuid) { Value = envelope.MessageId });
        command.Parameters.Add(new NpgsqlParameter("shipment_id", NpgsqlDbType.Uuid) { Value = payload.ShipmentId });
        command.Parameters.Add(new NpgsqlParameter("template", NpgsqlDbType.Varchar) { Value = payload.Template });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = timeProvider.GetUtcNowMicroseconds() });
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (inserted == 1)
        {
            LogSimulated(logger, payload.Template, payload.ShipmentId, envelope.MessageId);
        }
        else
        {
            LogDuplicate(logger, envelope.MessageId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulated recipient notification {Template} for shipment {ShipmentId} (message {MessageId}); nothing sent")]
    private static partial void LogSimulated(ILogger logger, string template, Guid shipmentId, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate outbox delivery {MessageId} ignored by notification channel")]
    private static partial void LogDuplicate(ILogger logger, Guid messageId);
}

public sealed record OutboxMessageView(
    Guid MessageId, string Type, DateTime OccurredAt, DateTime AvailableAt, int Attempts, string? LastError, DateTime? DeadLetteredAt,
    DateTime? ProcessedAt, int ReplayCount, JsonElement Payload);

public enum OutboxReplayResult
{
    Replayed,
    NotFound,
    NotDeadLettered,
}

/// <summary>Operasyon yöneticisi araçları: dead-letter listesi ve audit'li replay.</summary>
public sealed class OutboxAdministration(LogisticsDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<OutboxMessageView>> ListAsync(bool deadLettered, int limit, CancellationToken cancellationToken)
    {
        var query = db.Set<OutboxMessage>().AsNoTracking();
        query = deadLettered
            ? query.Where(m => m.DeadLetteredAt != null).OrderByDescending(m => m.DeadLetteredAt).ThenByDescending(m => m.Id)
            : query.Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null).OrderBy(m => m.AvailableAt).ThenBy(m => m.Id);
        var rows = await query.Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return rows.Select(m => new OutboxMessageView(m.MessageId, m.Type, m.OccurredAt.UtcDateTime, m.AvailableAt.UtcDateTime, m.Attempts, m.LastError,
            m.DeadLetteredAt?.UtcDateTime, m.ProcessedAt?.UtcDateTime, m.ReplayCount, JsonSerializer.Deserialize<JsonElement>(m.Payload))).ToList();
    }

    public async Task<OutboxReplayResult> ReplayAsync(Guid messageId, Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNowMicroseconds();
        db.Database.AutoSavepointsEnabled = false;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var message = await db.Set<OutboxMessage>()
            .FromSqlInterpolated($"SELECT * FROM platform.outbox_messages WHERE message_id = {messageId} FOR UPDATE")
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (message is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OutboxReplayResult.NotFound;
        }

        if (message.DeadLetteredAt is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OutboxReplayResult.NotDeadLettered;
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE platform.outbox_messages
               SET dead_lettered_at = NULL, attempts = 0, available_at = {now}, last_error = NULL,
                   lock_token = NULL, locked_until = NULL, locked_by = NULL, replay_count = replay_count + 1
             WHERE id = {message.Id}
            """, cancellationToken);
        db.Add(AuditEntry.UserSucceeded(now, userId, OutboxAuditActions.Replayed, "OutboxMessage", messageId.ToString(), Activity.Current?.TraceId.ToString(),
            JsonSerializer.Serialize(new { type = message.Type, previousAttempts = (int)message.Attempts, lastError = message.LastError })));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return OutboxReplayResult.Replayed;
    }
}

internal sealed partial class OutboxWorker(
    OutboxDispatcher dispatcher,
    IConfiguration configuration,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Workers:Enabled", true))
        {
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var processed = await dispatcher.RunOnceAsync(stoppingToken);
                failures = 0;
                delay = processed > 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(options.Value.PollMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Worker döngüsü hiçbir hatada durmaz.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures++;
                LogPollFailed(logger, ex.GetType().Name, TransientFailures.IsTransient(ex));
                delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 6))));
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox poll failed ({ErrorType}, transient={Transient}); backing off")]
    private static partial void LogPollFailed(ILogger logger, string errorType, bool transient);
}

public static class OutboxRegistration
{
    public static IServiceCollection AddLstOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(o => o.BatchSize is > 0 and <= 500 && o.LeaseSeconds is >= 5 and <= 600 && o.MaxAttempts is > 0 and <= 50
                && o.PollMilliseconds >= 50 && o.HandlerTimeoutSeconds is > 0 and < 60 && o.HandlerTimeoutSeconds < o.LeaseSeconds,
                "Workers:Outbox ayarları geçersiz (handler zaman aşımı lease süresinden kısa olmalı).")
            .ValidateOnStart();
        services.AddSingleton<OutboxDispatcher>();
        services.AddScoped<OutboxAdministration>();
        services.AddHostedService<OutboxWorker>();
        services.TryAddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();
        services.AddScoped<IOutboxHandler, SimulatedNotificationHandler>();
        foreach (var type in new[]
                 {
                     OutboxMessageTypes.ShipmentStatusChanged, OutboxMessageTypes.ExceptionOpened,
                     OutboxMessageTypes.ExceptionAssigned, OutboxMessageTypes.ExceptionResolved,
                 })
        {
            services.AddScoped<IOutboxHandler>(sp => new RealtimeInvalidationHandler(type, sp.GetRequiredService<IRealtimeNotifier>()));
        }

        return services;
    }
}
