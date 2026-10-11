namespace Lst.Modules.Shipments.Application;

public sealed record CancelShipmentRequest
{
    public string? Reason { get; init; }
}

/// <summary>Operatör komutlarının ortak yanıtı: güncel durum özeti; yeni ETag header'da.</summary>
public sealed record ShipmentCommandResponse(Guid Id, string TrackingNumber, string Status, DateTime StatusChangedAt);

public abstract record OperatorCommandOutcome
{
    private OperatorCommandOutcome()
    {
    }

    public sealed record Succeeded(ShipmentCommandResponse Response, string ETag) : OperatorCommandOutcome;

    public sealed record NotFound : OperatorCommandOutcome;

    /// <summary>If-Match güncel ETag ile eşleşmedi; istemci güncel durumu yeniden okumalı.</summary>
    public sealed record PreconditionFailed(string CurrentETag) : OperatorCommandOutcome;

    public sealed record NotAllowed(string Code, string CurrentStatus, bool ReturnToSenderAllowed) : OperatorCommandOutcome;

    public sealed record Busy : OperatorCommandOutcome;
}
