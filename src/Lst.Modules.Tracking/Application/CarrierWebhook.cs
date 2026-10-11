using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lst.Modules.Shipments.Domain;

namespace Lst.Modules.Tracking.Application;

/// <summary>"Carriers" konfigürasyonu. Secret'lar secret store/user-secrets/secret dosyalarından gelir; repoda yoktur.</summary>
public sealed class CarrierOptions
{
    public const string SectionName = "Carriers";

    /// <summary>Carrier kodu → en fazla iki aktif secret (rotasyon). Secret en az 32 karakter.</summary>
    public Dictionary<string, CarrierSecretOptions> Secrets { get; } = new(StringComparer.Ordinal);

    /// <summary>Carrier başına pencere içinde kabul edilen istek sayısı (varsayılan 50 istek/sn).</summary>
    public int RateLimitPermitLimit { get; set; } = 50;

    public int RateLimitWindowSeconds { get; set; } = 1;

    public int SignatureToleranceSeconds { get; set; } = 300;
}

public sealed class CarrierSecretOptions
{
    public IList<string> Values { get; } = [];
}

public sealed record CarrierWebhookPayload
{
    public string? EventId { get; init; }

    public string? CarrierCode { get; init; }

    public string? TrackingNumber { get; init; }

    public string? EventType { get; init; }

    public string? OccurredAt { get; init; }

    public long? Sequence { get; init; }

    public LocationPayload? Location { get; init; }

    public AttemptPayload? Attempt { get; init; }
}

public sealed record LocationPayload
{
    public string? FacilityCode { get; init; }

    public string? City { get; init; }
}

public sealed record AttemptPayload
{
    public int? Number { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Normalize, doğrulanmış carrier olayı. Kanonik hash bu alanlardan (sabit sıra) üretilir.</summary>
public sealed record NormalizedCarrierEvent(
    string EventId,
    string TrackingNumber,
    TrackingEventType EventType,
    string CarrierEventType,
    DateTimeOffset OccurredAt,
    long? Sequence,
    string? FacilityCode,
    string? City,
    short? AttemptNumber,
    DeliveryFailureReason? FailureReason)
{
    public string ToCanonicalJson() => JsonSerializer.Serialize(new
    {
        eventId = EventId,
        trackingNumber = TrackingNumber,
        eventType = CarrierEventType,
        occurredAtUtcTicks = OccurredAt.UtcTicks,
        offsetMinutes = (int)OccurredAt.Offset.TotalMinutes,
        sequence = Sequence,
        facilityCode = FacilityCode,
        city = City,
        attemptNumber = AttemptNumber,
        failureReason = FailureReason?.ToString(),
    });

    public byte[] CanonicalSha256() => SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson()));
}

public static partial class CarrierWebhookParser
{
    public const int MaxBodyBytes = 64 * 1024;
    public const string SignatureHeader = "X-Lst-Signature";

    /// <summary>
    /// Webhook JSON okuma: büyük/küçük harf duyarlı, yinelenen alan reddedilir, string sayı yok, derinlik 16.
    /// Bilinmeyen alanlar reddedilmez ama saklanmaz/işlenmez (docs/05 §5.1 allowlist).
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16,
    };

    private static readonly Dictionary<string, TrackingEventType> EventTypes = new(StringComparer.Ordinal)
    {
        ["PICKED_UP"] = TrackingEventType.HandedToCarrier,
        ["IN_TRANSIT"] = TrackingEventType.CarrierInTransit,
        ["ARRIVED_AT_FACILITY"] = TrackingEventType.ArrivedAtHub,
        ["DEPARTED_FACILITY"] = TrackingEventType.DepartedHub,
        ["OUT_FOR_DELIVERY"] = TrackingEventType.OutForDelivery,
        ["DELIVERED"] = TrackingEventType.Delivered,
        ["DELIVERY_FAILED"] = TrackingEventType.DeliveryFailed,
        ["RETURN_INITIATED"] = TrackingEventType.ReturnInitiated,
        ["RETURNED_TO_SENDER"] = TrackingEventType.ReturnedToSender,
    };

    private static readonly Dictionary<string, DeliveryFailureReason> Reasons = new(StringComparer.Ordinal)
    {
        ["RECIPIENT_ABSENT"] = DeliveryFailureReason.RecipientAbsent,
        ["ADDRESS_NOT_FOUND"] = DeliveryFailureReason.AddressNotFound,
        ["REFUSED"] = DeliveryFailureReason.RecipientRefused,
        ["NO_ACCESS"] = DeliveryFailureReason.AccessRestricted,
        ["DAMAGED"] = DeliveryFailureReason.PackageDamaged,
    };

    public static IReadOnlyCollection<string> SupportedEventTypes => EventTypes.Keys;

    public static TrackingEventType? MapEventType(string carrierEventType) =>
        EventTypes.TryGetValue(carrierEventType, out var mapped) ? mapped : null;

    /// <summary>
    /// X-Lst-Signature: t=&lt;unix sn&gt;,v1=&lt;hex&gt;[,v1=&lt;hex&gt;]. HMAC-SHA256(secret, "{t}." + ham gövde) sabit zamanlı karşılaştırılır.
    /// </summary>
    public static bool VerifySignature(string? header, ReadOnlySpan<byte> rawBody, IEnumerable<string> secrets, DateTimeOffset now, int toleranceSeconds, out DateTimeOffset signedAt)
    {
        signedAt = default;
        if (string.IsNullOrWhiteSpace(header) || header.Length > 1024)
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var key = part[..separator];
            var value = part[(separator + 1)..];
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (key == "v1" && value.Length == 64 && HexRegex().IsMatch(value))
            {
                signatures.Add(Convert.FromHexString(value));
            }
        }

        if (timestamp is null || signatures.Count == 0)
        {
            return false;
        }

        signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (Math.Abs((now - signedAt).TotalSeconds) > toleranceSeconds)
        {
            return false;
        }

        var prefix = Encoding.ASCII.GetBytes(timestamp.Value.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(message, 0);
        rawBody.CopyTo(message.AsSpan(prefix.Length));
        var verified = false;
        foreach (var secret in secrets)
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
            foreach (var signature in signatures)
            {
                // Kısa devre yapılmaz: tüm adaylar sabit zamanlı karşılaştırılır.
                verified |= CryptographicOperations.FixedTimeEquals(expected, signature);
            }
        }

        return verified;
    }

    public static string Sign(string secret, long unixSeconds, ReadOnlySpan<byte> rawBody)
    {
        var prefix = Encoding.ASCII.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(message, 0);
        rawBody.CopyTo(message.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    public static (NormalizedCarrierEvent? Value, Dictionary<string, string[]> Errors) Normalize(CarrierWebhookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (payload.EventId is null || !EventIdRegex().IsMatch(payload.EventId))
        {
            errors["eventId"] = ["1–100 karakter; harf, rakam ve '-', '_', '.', ':'."];
        }

        if (payload.TrackingNumber is null || !TrackingNumbers.IsValid(payload.TrackingNumber))
        {
            errors["trackingNumber"] = ["Geçerli LS tracking numarası olmalı."];
        }

        TrackingEventType eventType = default;
        if (payload.EventType is null || !EventTypes.TryGetValue(payload.EventType, out eventType))
        {
            errors["eventType"] = ["Desteklenen olay tipleri: " + string.Join(", ", EventTypes.Keys) + "."];
        }

        DateTimeOffset occurredAt = default;
        if (payload.OccurredAt is null || !OffsetRegex().IsMatch(payload.OccurredAt)
            || !DateTimeOffset.TryParse(payload.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out occurredAt))
        {
            errors["occurredAt"] = ["Ofsetli ISO-8601 zaman olmalı."];
        }

        if (payload.Sequence is < 0)
        {
            errors["sequence"] = ["Negatif olamaz."];
        }

        var facility = payload.Location?.FacilityCode;
        if (facility is not null && !FacilityRegex().IsMatch(facility))
        {
            errors["location.facilityCode"] = ["1–20 karakter; büyük harf, rakam, '-'."];
        }

        var city = string.IsNullOrWhiteSpace(payload.Location?.City) ? null : payload.Location!.City;
        if (city is { Length: > 100 })
        {
            errors["location.city"] = ["En fazla 100 karakter."];
        }

        short? attemptNumber = null;
        DeliveryFailureReason? reason = null;
        if (eventType == TrackingEventType.DeliveryFailed && errors.Count == 0)
        {
            if (payload.Attempt?.Number is not { } number || number is < 1 or > 20)
            {
                errors["attempt.number"] = ["DELIVERY_FAILED için 1–20 arası zorunlu."];
            }
            else
            {
                attemptNumber = checked((short)number);
            }

            if (payload.Attempt?.Reason is not { } rawReason || rawReason.Length > 40)
            {
                errors["attempt.reason"] = ["DELIVERY_FAILED için zorunlu."];
            }
            else
            {
                reason = Reasons.TryGetValue(rawReason, out var mapped) ? mapped : DeliveryFailureReason.Other;
            }
        }

        return errors.Count > 0
            ? (null, errors)
            : (new NormalizedCarrierEvent(payload.EventId!, payload.TrackingNumber!, eventType, payload.EventType!, occurredAt,
                payload.Sequence, facility, city, attemptNumber, reason), errors);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventIdRegex();

    [GeneratedRegex("^[A-Z0-9-]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex FacilityRegex();

    [GeneratedRegex(@"(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();
}
