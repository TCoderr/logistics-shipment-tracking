using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lst.Modules.Shipments.Domain;

namespace Lst.Modules.Shipments.Application;

/// <summary>
/// "lst.create-shipment.v1" kanonik istek gösterimi (RFC 8785 uygulaması DEĞİLDİR; bu endpoint'in tipli DTO'suna özgüdür).
/// Kurallar (docs/05 §3, Karar değişikliği Dilim 1):
/// - Hash, sıkı JSON okuması ve şekil doğrulaması başarılı olduktan sonra <see cref="ValidCreateShipment"/> üzerinden hesaplanır.
/// - Alan sırası sabittir (aşağıdaki kayıtların bildirim sırası); gelen JSON'daki alan sırası ve boşluk etkisizdir.
/// - İsteğe bağlı alanlarda eksik, null, "" ve yalnız boşluk aynıdır (null). countryCode verilmezse "TR".
/// - Zorunlu string'ler olduğu gibi (kırpılmadan) karşılaştırılır.
/// - Paket dizisinin sırası anlamlıdır (paket sıra numarasını belirler).
/// - declaredValue.amount invariant "0.00" metni olarak yazılır: 1850, 1850.0, 1850.00 aynı hash'i verir.
/// Biçim değişirse <see cref="Version"/> artırılır; eski sürümle saklanmış anahtarlar karşılaştırılamaz ve 422 döner.
/// </summary>
public static class CreateShipmentRequestHash
{
    public const short Version = 1;
    public const string Endpoint = "POST /api/shipments";
    private const string Domain = "lst.create-shipment.v1";

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
    };

    public static byte[] Compute(ValidCreateShipment request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = ToCanonicalBytes(request);
        var header = Encoding.UTF8.GetBytes($"{Domain}\n{Endpoint}\n");
        var input = new byte[header.Length + canonical.Length];
        header.CopyTo(input, 0);
        canonical.CopyTo(input, header.Length);
        return SHA256.HashData(input);
    }

    internal static byte[] ToCanonicalBytes(ValidCreateShipment r) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new CanonicalV1(
                r.ServiceLevel.ToString(),
                r.ExternalReference,
                r.OriginHubCode,
                r.DestinationHubCode,
                Party(r.Sender),
                Party(r.Recipient),
                r.Packages.Select(p => new CanonicalPackage(p.WeightGrams, p.LengthMm, p.WidthMm, p.HeightMm, p.ContentDescription)).ToList(),
                r.DeclaredValue is null
                    ? null
                    : new CanonicalMoney(r.DeclaredValue.Amount.ToString("0.00", CultureInfo.InvariantCulture), r.DeclaredValue.Currency)),
            CanonicalOptions);

    private static CanonicalParty Party(PartySnapshot p) =>
        new(p.FullName, p.CompanyName, p.PhoneE164, p.Email,
            new CanonicalAddress(p.AddressLine1, p.AddressLine2, p.District, p.City, p.PostalCode, p.CountryCode));

    private sealed record CanonicalV1(
        string ServiceLevel,
        string? ExternalReference,
        string OriginHubCode,
        string DestinationHubCode,
        CanonicalParty Sender,
        CanonicalParty Recipient,
        IReadOnlyList<CanonicalPackage> Packages,
        CanonicalMoney? DeclaredValue);

    private sealed record CanonicalParty(string FullName, string? CompanyName, string Phone, string? Email, CanonicalAddress Address);

    private sealed record CanonicalAddress(string Line1, string? Line2, string? District, string City, string? PostalCode, string CountryCode);

    private sealed record CanonicalPackage(int WeightGrams, int LengthMm, int WidthMm, int HeightMm, string? ContentDescription);

    private sealed record CanonicalMoney(string Amount, string Currency);
}
