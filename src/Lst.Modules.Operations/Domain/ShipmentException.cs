namespace Lst.Modules.Operations.Domain;

public enum ExceptionType
{
    DeliveryFailed,
    MaxAttemptsReached,
    DeliveryOverdue,
    NoMovement,
    PackageSplit,
    CarrierEventPendingReview,
    CarrierEventConflict,
}

public enum ExceptionSeverity
{
    Low,
    Medium,
    High,
}

public enum ExceptionStatus
{
    Open,
    Assigned,
    Resolved,
}

public enum ResolutionCode
{
    ReattemptApproved,
    ReturnToSenderInitiated,
    CarrierConfirmed,
    EventAppliedManually,
    EventDiscarded,
    PackagesConsolidated,
    NoActionNeeded,
    AutoResolved,
}

/// <summary>
/// İnsan müdahalesi gerektiren durum (docs/02 §4.1). Gönderi + tip başına tek açık kayıt (partial unique index); tekrarlar
/// <see cref="OccurrenceCount"/>'u artırır. Durumlar: Open → Assigned → Resolved (terminal).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Domain sözlüğündeki terim (docs/02 §1); .NET Exception türevi değildir.")]
public sealed class ShipmentException
{
    private ShipmentException()
    {
    }

    public Guid Id { get; private set; }

    public Guid ShipmentId { get; private set; }

    public ExceptionType Type { get; private set; }

    public ExceptionSeverity Severity { get; private set; }

    public ExceptionStatus Status { get; private set; }

    public Guid? HubId { get; private set; }

    public int OccurrenceCount { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset LastOccurredAt { get; private set; }

    public string OpenedBySource { get; private set; } = string.Empty;

    public DateTimeOffset DueAt { get; private set; }

    public Guid? AssignedToUserId { get; private set; }

    public DateTimeOffset? AssignedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public Guid? ResolvedByUserId { get; private set; }

    public ResolutionCode? ResolutionCode { get; private set; }

    public string? ResolutionNote { get; private set; }

    /// <summary>PostgreSQL xmin; istisna ETag'i.</summary>
    public uint Version { get; private set; }

    public bool IsOpen => Status != ExceptionStatus.Resolved;

    /// <summary>Vade: High 4 saat, Medium 24 saat, Low 72 saat (duvar saati; iş saati hesabı v2).</summary>
    public static TimeSpan DueWindow(ExceptionSeverity severity) => severity switch
    {
        ExceptionSeverity.High => TimeSpan.FromHours(4),
        ExceptionSeverity.Medium => TimeSpan.FromHours(24),
        _ => TimeSpan.FromHours(72),
    };

    public static ShipmentException Open(Guid shipmentId, ExceptionType type, ExceptionSeverity severity, Guid? hubId, string source, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            ShipmentId = shipmentId,
            Type = type,
            Severity = severity,
            Status = ExceptionStatus.Open,
            HubId = hubId,
            OccurrenceCount = 1,
            OpenedAt = now,
            LastOccurredAt = now,
            OpenedBySource = source,
            DueAt = now + DueWindow(severity),
        };

    public void RecordOccurrence(DateTimeOffset now)
    {
        EnsureOpen();
        OccurrenceCount++;
        LastOccurredAt = now;
    }

    public void Assign(Guid userId, DateTimeOffset now)
    {
        EnsureOpen();
        Status = ExceptionStatus.Assigned;
        AssignedToUserId = userId;
        AssignedAt = now;
    }

    public void Resolve(ResolutionCode code, string note, Guid userId, DateTimeOffset now)
    {
        EnsureOpen();
        if (!ExceptionResolutionPolicy.IsAllowed(Type, code))
        {
            throw new InvalidOperationException($"{code} bu istisna tipi için geçerli değil.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        Status = ExceptionStatus.Resolved;
        ResolutionCode = code;
        ResolutionNote = note;
        ResolvedByUserId = userId;
        ResolvedAt = now;
    }

    /// <summary>Önem yalnız yükselir (SLA: gecikme gün geçtikçe High); vade yeni öneme göre yeniden hesaplanır, tekrar sayısı değişmez.</summary>
    public bool Escalate(ExceptionSeverity severity, DateTimeOffset now)
    {
        EnsureOpen();
        if (severity <= Severity)
        {
            return false;
        }

        Severity = severity;
        DueAt = Min(DueAt, now + DueWindow(severity));
        return true;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>Gönderi terminale ulaşınca (teslim, iade tamamlandı, iptal) operasyonel istisnalar kendiliğinden kapanır.</summary>
    public void AutoResolve(DateTimeOffset now)
    {
        EnsureOpen();
        Status = ExceptionStatus.Resolved;
        ResolutionCode = Domain.ResolutionCode.AutoResolved;
        ResolvedAt = now;
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("Çözülmüş istisna değiştirilemez.");
        }
    }
}

public static class ExceptionResolutionPolicy
{
    private static readonly Dictionary<ExceptionType, ResolutionCode[]> Allowed = new()
    {
        [ExceptionType.DeliveryFailed] = [ResolutionCode.ReturnToSenderInitiated, ResolutionCode.NoActionNeeded],
        [ExceptionType.MaxAttemptsReached] = [ResolutionCode.ReattemptApproved, ResolutionCode.ReturnToSenderInitiated],
        [ExceptionType.DeliveryOverdue] = [ResolutionCode.NoActionNeeded, ResolutionCode.ReturnToSenderInitiated],
        [ExceptionType.NoMovement] = [ResolutionCode.NoActionNeeded, ResolutionCode.ReturnToSenderInitiated],
        [ExceptionType.PackageSplit] = [ResolutionCode.PackagesConsolidated, ResolutionCode.NoActionNeeded],
        [ExceptionType.CarrierEventPendingReview] = [ResolutionCode.CarrierConfirmed, ResolutionCode.EventDiscarded],
        [ExceptionType.CarrierEventConflict] = [ResolutionCode.CarrierConfirmed, ResolutionCode.EventDiscarded],
    };

    public static IReadOnlyList<ResolutionCode> AllowedFor(ExceptionType type) => Allowed[type];

    public static bool IsAllowed(ExceptionType type, ResolutionCode code) => Allowed[type].Contains(code);

    /// <summary>Gönderi terminale ulaşınca otomatik kapanan tipler (carrier çakışmaları insan kararı bekler).</summary>
    public static bool AutoResolvesOnTerminal(ExceptionType type) =>
        type is not (ExceptionType.CarrierEventConflict or ExceptionType.CarrierEventPendingReview);
}
