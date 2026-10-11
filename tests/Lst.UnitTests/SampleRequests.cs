using System.Text.Json;
using Lst.Modules.Shipments.Application;

namespace Lst.UnitTests;

internal static class SampleRequests
{
    public const string Valid = """
        {
          "serviceLevel": "Standard",
          "externalReference": "ORD-2026-100245",
          "originHubCode": "IST-01",
          "destinationHubCode": "ANK-02",
          "sender": {
            "fullName": "Deniz Kaya",
            "companyName": "Kaya Tekstil Ltd.",
            "phone": "+902124445566",
            "email": "lojistik@kayatekstil.example",
            "address": { "line1": "Atatürk Cd. No: 12", "line2": null, "district": "Bağcılar", "city": "İstanbul", "postalCode": "34200", "countryCode": "TR" }
          },
          "recipient": {
            "fullName": "Ece Yılmaz",
            "companyName": null,
            "phone": "+905321234567",
            "email": null,
            "address": { "line1": "Kızılay Mh. 5. Sk. No: 3 D: 7", "line2": null, "district": "Çankaya", "city": "Ankara", "postalCode": "06420", "countryCode": "TR" }
          },
          "packages": [
            { "weightGrams": 2400, "lengthMm": 400, "widthMm": 300, "heightMm": 200, "contentDescription": "Tekstil" },
            { "weightGrams": 900, "lengthMm": 300, "widthMm": 200, "heightMm": 100, "contentDescription": null }
          ],
          "declaredValue": { "amount": 1850.00, "currency": "TRY" }
        }
        """;

    public static CreateShipmentRequest Parse(string json) =>
        JsonSerializer.Deserialize<CreateShipmentRequest>(json, ShipmentsJson.StrictRequest)!;

    public static ValidCreateShipment ParseValid(string json)
    {
        var (valid, errors) = CreateShipmentValidator.Validate(Parse(json));
        Assert.True(valid is not null, "Beklenmeyen doğrulama hatası: " + string.Join(", ", errors.Keys));
        return valid!;
    }

    public static byte[] Hash(string json) => CreateShipmentRequestHash.Compute(ParseValid(json));
}
