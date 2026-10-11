namespace Lst.Modules.Shipments.Application;

// İstek DTO'ları bilerek nullable'dır: eksik alanlar JSON hatası yerine alan bazlı doğrulama hatası üretir.
// Tanımsız ve yinelenen JSON alanları ShipmentsJson.StrictRequest ile reddedilir.

public sealed record CreateShipmentRequest
{
    public string? ServiceLevel { get; init; }

    public string? ExternalReference { get; init; }

    public string? OriginHubCode { get; init; }

    public string? DestinationHubCode { get; init; }

    public PartyRequest? Sender { get; init; }

    public PartyRequest? Recipient { get; init; }

    public IReadOnlyList<PackageRequest?>? Packages { get; init; }

    public DeclaredValueRequest? DeclaredValue { get; init; }
}

public sealed record PartyRequest
{
    public string? FullName { get; init; }

    public string? CompanyName { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public AddressRequest? Address { get; init; }
}

public sealed record AddressRequest
{
    public string? Line1 { get; init; }

    public string? Line2 { get; init; }

    public string? District { get; init; }

    public string? City { get; init; }

    public string? PostalCode { get; init; }

    public string? CountryCode { get; init; }
}

public sealed record PackageRequest
{
    public int? WeightGrams { get; init; }

    public int? LengthMm { get; init; }

    public int? WidthMm { get; init; }

    public int? HeightMm { get; init; }

    public string? ContentDescription { get; init; }
}

public sealed record DeclaredValueRequest
{
    public decimal? Amount { get; init; }

    public string? Currency { get; init; }
}

public sealed record CreateShipmentResponse(
    Guid Id,
    string TrackingNumber,
    string PublicTrackingCode,
    string Status,
    string ServiceLevel,
    DateOnly PromisedDeliveryDate,
    IReadOnlyList<CreatedPackageResponse> Packages,
    DateTime CreatedAt);

public sealed record CreatedPackageResponse(Guid Id, int Sequence, string LabelCode);
