namespace Lst.Platform.Idempotency;

/// <summary>
/// platform.idempotency_records. Yanıt kolonları claim süresince NULL'dır; aynı transaction içinde doldurulur.
/// Ertelenmiş (DEFERRABLE INITIALLY DEFERRED) constraint trigger'ı tamamlanmamış bir kaydın commit edilmesini engeller.
/// Kayıtlar yalnız <see cref="IdempotencyStore"/> üzerinden ham SQL ile yazılır; entity eşlemesi migration ve okuma içindir.
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
    }

    public Guid Id { get; private set; }

    public string ActorScope { get; private set; } = string.Empty;

    public string Endpoint { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    public byte[] RequestHash { get; private set; } = [];

    public short RequestHashVersion { get; private set; }

    public short? ResponseStatusCode { get; private set; }

    public string? ResponseContentType { get; private set; }

    public byte[]? ResponseBody { get; private set; }

    public string? ResponseLocation { get; private set; }

    public string? ResponseEtag { get; private set; }

    public Guid? ResourceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }
}
