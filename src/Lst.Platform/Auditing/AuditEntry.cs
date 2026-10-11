namespace Lst.Platform.Auditing;

/// <summary>
/// Append-only audit kaydı (platform.audit_entries). <see cref="Changes"/> yalnız allowlist alanları içerir; PII yazılmaz.
/// Çalışma zamanı DB rolünün bu tabloda yalnız INSERT/SELECT yetkisi vardır (db/runtime-role-grants.sql).
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry()
    {
    }

    public long Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string ActorType { get; private set; } = string.Empty;

    public string ActorId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public string Outcome { get; private set; } = string.Empty;

    public string? ReasonCode { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>JSON nesnesi (jsonb). Çağıran yalnız PII olmayan alanları verir.</summary>
    public string Changes { get; private set; } = "{}";

    public static AuditEntry UserSucceeded(
        DateTimeOffset occurredAt,
        Guid userId,
        string action,
        string entityType,
        string entityId,
        string? correlationId,
        string changesJson) =>
        Create(occurredAt, AuditActorTypes.User, userId.ToString(), action, entityType, entityId,
            AuditOutcomes.Succeeded, reasonCode: null, correlationId, changesJson);

    /// <summary>Sistem (worker, ingestion) aktörlü kayıt; <paramref name="systemActor"/> worker/kaynak adıdır.</summary>
    public static AuditEntry SystemSucceeded(
        DateTimeOffset occurredAt,
        string systemActor,
        string action,
        string entityType,
        string entityId,
        string? correlationId,
        string changesJson) =>
        Create(occurredAt, AuditActorTypes.System, systemActor, action, entityType, entityId,
            AuditOutcomes.Succeeded, reasonCode: null, correlationId, changesJson);

    public static AuditEntry UserRejected(
        DateTimeOffset occurredAt,
        Guid userId,
        string action,
        string entityType,
        string entityId,
        string reasonCode,
        string? correlationId,
        string changesJson) =>
        Create(occurredAt, AuditActorTypes.User, userId.ToString(), action, entityType, entityId,
            AuditOutcomes.Rejected, reasonCode, correlationId, changesJson);

    internal static AuditEntry Create(
        DateTimeOffset occurredAt,
        string actorType,
        string actorId,
        string action,
        string entityType,
        string entityId,
        string outcome,
        string? reasonCode,
        string? correlationId,
        string changesJson) =>
        new()
        {
            OccurredAt = occurredAt,
            ActorType = actorType,
            ActorId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Outcome = outcome,
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
            Changes = changesJson,
        };
}

public static class AuditEntryFactory
{
    /// <summary>
    /// Kimliği doğrulanmamış aktörün reddedilen işlemi (ör. LoginFailed). Kullanıcı adı, parola veya istek gövdesi yazılmaz;
    /// <paramref name="entityId"/> yalnız var olan kullanıcının id'si veya "unknown" olabilir.
    /// </summary>
    public static AuditEntry AnonymousRejected(
        DateTimeOffset occurredAt,
        string action,
        string entityType,
        string entityId,
        string reasonCode,
        string? correlationId) =>
        AuditEntry.Create(
            occurredAt,
            AuditActorTypes.Anonymous,
            "anonymous",
            action,
            entityType,
            entityId,
            AuditOutcomes.Rejected,
            reasonCode,
            correlationId,
            "{}");
}

public static class AuditActorTypes
{
    public const string User = "User";
    public const string Carrier = "Carrier";
    public const string System = "System";
    public const string Anonymous = "Anonymous";
}

public static class AuditOutcomes
{
    public const string Succeeded = "Succeeded";
    public const string Rejected = "Rejected";
}
