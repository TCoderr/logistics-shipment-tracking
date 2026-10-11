namespace Lst.Platform.Operations;

/// <summary>Hub referans verisi (operations.hubs). Operations modülü dilim 5'te bu tipi devralır.</summary>
public sealed class Hub
{
    private Hub()
    {
    }

    public Hub(Guid id, string code, string name, string city, string? district, bool isActive, DateTimeOffset createdAt)
    {
        Id = id;
        Code = code;
        Name = name;
        City = city;
        District = district;
        IsActive = isActive;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string City { get; private set; } = string.Empty;

    public string? District { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
