using System.Globalization;
using System.Text.RegularExpressions;
using Lst.Modules.Shipments.Domain;

namespace Lst.Modules.Shipments.Application;

/// <summary>Şekil doğrulamasından geçmiş, varsayılanları uygulanmış istek. Hub varlığı DB transaction'ında ayrıca denetlenir.</summary>
public sealed record ValidCreateShipment(
    ServiceLevel ServiceLevel,
    string? ExternalReference,
    string OriginHubCode,
    string DestinationHubCode,
    PartySnapshot Sender,
    PartySnapshot Recipient,
    IReadOnlyList<PackageSpec> Packages,
    DeclaredValue? DeclaredValue);

/// <summary>
/// Durumsuz (DB'siz) doğrulama. Kurallar: docs/02 §8 ve Dilim 1 promptu §4.2.
/// İsteğe bağlı string alanlarda "" ve yalnız boşluk NULL kabul edilir; zorunlu alanlarda kırpma yapılmaz.
/// </summary>
public static partial class CreateShipmentValidator
{
    public const decimal MaxDeclaredValue = 9_999_999_999.99m;
    public const string DefaultCountryCode = "TR";

    public static (ValidCreateShipment? Value, Dictionary<string, string[]> Errors) Validate(CreateShipmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var serviceLevel = request.ServiceLevel switch
        {
            "Standard" => ServiceLevel.Standard,
            "Express" => ServiceLevel.Express,
            _ => (ServiceLevel?)null,
        };
        if (serviceLevel is null)
        {
            errors["serviceLevel"] = ["'Standard' veya 'Express' olmalı."];
        }

        var externalReference = Optional(request.ExternalReference);
        if (externalReference is { Length: > 64 })
        {
            errors["externalReference"] = ["En fazla 64 karakter olmalı."];
        }

        var origin = HubCode(request.OriginHubCode, "originHubCode", errors);
        var destination = HubCode(request.DestinationHubCode, "destinationHubCode", errors);
        var sender = Party(request.Sender, "sender", errors);
        var recipient = Party(request.Recipient, "recipient", errors);
        var packages = Packages(request.Packages, errors);
        var declaredValue = Declared(request.DeclaredValue, errors);

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        return (new ValidCreateShipment(
            serviceLevel!.Value, externalReference, origin!, destination!, sender!, recipient!, packages!, declaredValue), errors);
    }

    private static string? HubCode(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null || !HubCodeRegex().IsMatch(value))
        {
            errors[field] = ["Hub kodu 'IST-01' biçiminde olmalı."];
            return null;
        }

        return value;
    }

    private static PartySnapshot? Party(PartyRequest? party, string prefix, Dictionary<string, string[]> errors)
    {
        if (party is null)
        {
            errors[prefix] = ["Zorunludur."];
            return null;
        }

        var start = errors.Count;
        var fullName = Required(party.FullName, 150, $"{prefix}.fullName", errors);
        var companyName = OptionalMax(party.CompanyName, 150, $"{prefix}.companyName", errors);

        var phone = party.Phone;
        if (phone is null || !PhoneRegex().IsMatch(phone))
        {
            errors[$"{prefix}.phone"] = ["Telefon E.164 biçiminde olmalı (ör. +905321234567)."];
        }

        var email = OptionalMax(party.Email, 254, $"{prefix}.email", errors);
        if (email is not null && !email.Contains('@', StringComparison.Ordinal))
        {
            errors[$"{prefix}.email"] = ["Geçerli bir e-posta adresi olmalı."];
        }

        var address = party.Address;
        if (address is null)
        {
            errors[$"{prefix}.address"] = ["Zorunludur."];
            return null;
        }

        var line1 = Required(address.Line1, 200, $"{prefix}.address.line1", errors);
        var line2 = OptionalMax(address.Line2, 200, $"{prefix}.address.line2", errors);
        var district = OptionalMax(address.District, 100, $"{prefix}.address.district", errors);
        var city = Required(address.City, 100, $"{prefix}.address.city", errors);
        var countryCode = Optional(address.CountryCode) ?? DefaultCountryCode;
        if (!CountryRegex().IsMatch(countryCode))
        {
            errors[$"{prefix}.address.countryCode"] = ["ISO 3166-1 alpha-2 olmalı (ör. TR)."];
        }

        var postalCode = Optional(address.PostalCode);
        if (countryCode == DefaultCountryCode)
        {
            if (postalCode is null || !TrPostalCodeRegex().IsMatch(postalCode))
            {
                errors[$"{prefix}.address.postalCode"] = ["Türkiye posta kodu 5 haneli olmalı."];
            }
        }
        else if (postalCode is { Length: > 10 })
        {
            errors[$"{prefix}.address.postalCode"] = ["En fazla 10 karakter olmalı."];
        }

        if (errors.Count > start)
        {
            return null;
        }

        return new PartySnapshot(fullName!, companyName, phone!, email, line1!, line2, district, city!, postalCode, countryCode);
    }

    private static List<PackageSpec>? Packages(IReadOnlyList<PackageRequest?>? packages, Dictionary<string, string[]> errors)
    {
        if (packages is null || packages.Count is < 1 or > Shipment.MaxPackages)
        {
            errors["packages"] = ["Gönderi 1 ile 20 arasında paket içermeli."];
            return null;
        }

        var start = errors.Count;
        var result = new List<PackageSpec>(packages.Count);
        for (var i = 0; i < packages.Count; i++)
        {
            var p = packages[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"packages[{i}]");
            if (p is null)
            {
                errors[prefix] = ["Zorunludur."];
                continue;
            }

            var weight = Range(p.WeightGrams, 1, 70_000, $"{prefix}.weightGrams", errors);
            var length = Range(p.LengthMm, 1, 3_000, $"{prefix}.lengthMm", errors);
            var width = Range(p.WidthMm, 1, 3_000, $"{prefix}.widthMm", errors);
            var height = Range(p.HeightMm, 1, 3_000, $"{prefix}.heightMm", errors);
            var content = OptionalMax(p.ContentDescription, 200, $"{prefix}.contentDescription", errors);
            result.Add(new PackageSpec(weight, length, width, height, content));
        }

        return errors.Count > start ? null : result;
    }

    private static DeclaredValue? Declared(DeclaredValueRequest? value, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        var ok = true;
        if (value.Amount is not { } amount || amount < 0 || amount > MaxDeclaredValue)
        {
            errors["declaredValue.amount"] = ["0 ile 9999999999.99 arasında olmalı."];
            ok = false;
        }
        else if (decimal.Round(amount, 2) != amount)
        {
            errors["declaredValue.amount"] = ["En fazla 2 ondalık hane içermeli."];
            ok = false;
        }

        if (value.Currency is null || !CurrencyRegex().IsMatch(value.Currency))
        {
            errors["declaredValue.currency"] = ["ISO 4217 kodu olmalı (ör. TRY)."];
            ok = false;
        }

        // decimal.Round ölçeği 2'ye sabitler: 1850, 1850.0 ve 1850.000 aynı değer ve aynı kanonik gösterim olur.
        return ok ? new DeclaredValue(decimal.Round(value.Amount!.Value, 2), value.Currency!) : null;
    }

    private static string? Required(string? value, int max, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["Zorunludur."];
            return null;
        }

        if (value.Length > max)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"En fazla {max} karakter olmalı.")];
            return null;
        }

        return value;
    }

    private static string? OptionalMax(string? value, int max, string field, Dictionary<string, string[]> errors)
    {
        var normalized = Optional(value);
        if (normalized is { } v && v.Length > max)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"En fazla {max} karakter olmalı.")];
        }

        return normalized;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int Range(int? value, int min, int max, string field, Dictionary<string, string[]> errors)
    {
        if (value is not { } v || v < min || v > max)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"{min} ile {max} arasında olmalı.")];
            return 0;
        }

        return v;
    }

    [GeneratedRegex("^[A-Z]{3}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex HubCodeRegex();

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CountryRegex();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyRegex();

    [GeneratedRegex("^[0-9]{5}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrPostalCodeRegex();
}
