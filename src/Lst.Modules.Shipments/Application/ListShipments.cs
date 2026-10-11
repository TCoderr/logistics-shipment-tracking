using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Shipments.Application;

/// <summary>Query string bağlama modeli; değerler <see cref="ShipmentListRequest.TryCreate"/> ile doğrulanır.</summary>
public sealed record ShipmentListQuery
{
    public string? Q { get; init; }

    public string[]? Status { get; init; }

    public string? HubCode { get; init; }

    public string? ServiceLevel { get; init; }

    public string? CarrierCode { get; init; }

    public string? Delayed { get; init; }

    public string? HasOpenException { get; init; }

    public string? CreatedFrom { get; init; }

    public string? CreatedTo { get; init; }

    public string? Sort { get; init; }

    public string? Limit { get; init; }

    public string? Cursor { get; init; }
}

public enum ShipmentSortField
{
    CreatedAt,
    PromisedDeliveryDate,
    StatusChangedAt,
}

public enum ShipmentSearchKind
{
    None,
    TrackingNumber,
    LabelCode,
    ExternalReference,
    RecipientPhoneLast4,
}

/// <summary>Doğrulanmış liste isteği. Tarih aralığı yarı açık: createdFrom dahil, createdTo hariç.</summary>
public sealed record ShipmentListRequest(
    ShipmentSearchKind SearchKind,
    string? SearchValue,
    IReadOnlyList<ShipmentStatus> Statuses,
    string? HubCode,
    ServiceLevel? ServiceLevel,
    string? CarrierCode,
    bool? Delayed,
    bool? HasOpenException,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    ShipmentSortField SortField,
    bool Descending,
    int Limit,
    string? Cursor)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private static readonly Dictionary<string, (ShipmentSortField Field, bool Descending)> Sorts = new(StringComparer.Ordinal)
    {
        ["-createdAt"] = (ShipmentSortField.CreatedAt, true),
        ["createdAt"] = (ShipmentSortField.CreatedAt, false),
        ["promisedDeliveryDate"] = (ShipmentSortField.PromisedDeliveryDate, false),
        ["-promisedDeliveryDate"] = (ShipmentSortField.PromisedDeliveryDate, true),
        ["statusChangedAt"] = (ShipmentSortField.StatusChangedAt, false),
        ["-statusChangedAt"] = (ShipmentSortField.StatusChangedAt, true),
    };

    public string SortToken => (Descending ? "-" : string.Empty) + SortField switch
    {
        ShipmentSortField.CreatedAt => "createdAt",
        ShipmentSortField.PromisedDeliveryDate => "promisedDeliveryDate",
        _ => "statusChangedAt",
    };

    public bool HasNonSearchFilter =>
        Statuses.Count > 0 || HubCode is not null || ServiceLevel is not null || CarrierCode is not null || Delayed is not null || HasOpenException is not null
        || CreatedFrom is not null || CreatedTo is not null;

    public static bool TryCreate(ShipmentListQuery query, out ShipmentListRequest? request, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(query);
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        request = null;

        var statuses = new List<ShipmentStatus>();
        foreach (var raw in query.Status ?? [])
        {
            if (Enum.TryParse<ShipmentStatus>(raw, ignoreCase: false, out var status) && Enum.IsDefined(status) && raw == status.ToString())
            {
                statuses.Add(status);
            }
            else
            {
                errors["status"] = ["Geçerli durum adları: " + string.Join(", ", Enum.GetNames<ShipmentStatus>()) + "."];
            }
        }

        if (query.HubCode is not null && !Patterns.HubCode().IsMatch(query.HubCode))
        {
            errors["hubCode"] = ["Hub kodu 'IST-01' biçiminde olmalı."];
        }

        ServiceLevel? serviceLevel = query.ServiceLevel switch
        {
            null => null,
            "Standard" => Domain.ServiceLevel.Standard,
            "Express" => Domain.ServiceLevel.Express,
            _ => null,
        };
        if (query.ServiceLevel is not null && serviceLevel is null)
        {
            errors["serviceLevel"] = ["'Standard' veya 'Express' olmalı."];
        }

        if (query.CarrierCode is not null && !Patterns.CarrierCode().IsMatch(query.CarrierCode))
        {
            errors["carrierCode"] = ["Carrier kodu 2–16 büyük harf/rakam olmalı."];
        }

        bool? delayed = query.Delayed switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => null,
        };
        if (query.Delayed is not null && delayed is null)
        {
            errors["delayed"] = ["'true' veya 'false' olmalı."];
        }

        bool? hasOpenException = query.HasOpenException switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => null,
        };
        if (query.HasOpenException is not null && hasOpenException is null)
        {
            errors["hasOpenException"] = ["'true' veya 'false' olmalı."];
        }

        var from = ParseInstant(query.CreatedFrom, "createdFrom", errors);
        var to = ParseInstant(query.CreatedTo, "createdTo", errors);
        if (from is not null && to is not null && from >= to)
        {
            errors["createdTo"] = ["createdTo, createdFrom'dan sonra olmalı (aralık yarı açık: [createdFrom, createdTo))."];
        }

        var (sortField, descending) = (ShipmentSortField.CreatedAt, true);
        if (query.Sort is not null && !Sorts.TryGetValue(query.Sort, out var sort))
        {
            errors["sort"] = ["Geçerli sıralamalar: " + string.Join(", ", Sorts.Keys) + "."];
        }
        else if (query.Sort is not null)
        {
            (sortField, descending) = Sorts[query.Sort];
        }

        var limit = DefaultLimit;
        if (query.Limit is not null
            && (!int.TryParse(query.Limit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaxLimit))
        {
            errors["limit"] = [string.Create(CultureInfo.InvariantCulture, $"1 ile {MaxLimit} arasında bir tam sayı olmalı.")];
        }

        if (query.Cursor is { Length: 0 or > 4096 })
        {
            errors["cursor"] = ["Cursor geçersiz."];
        }

        var (kind, value) = ClassifySearch(query.Q, errors);

        var candidate = new ShipmentListRequest(kind, value, statuses, query.HubCode, serviceLevel, query.CarrierCode, delayed, hasOpenException, from, to,
            sortField, descending, limit, string.IsNullOrEmpty(query.Cursor) ? null : query.Cursor);

        if (kind == ShipmentSearchKind.RecipientPhoneLast4 && !candidate.HasNonSearchFilter)
        {
            errors["q"] = ["Telefonun son 4 hanesiyle arama en az bir başka filtreyle (durum, hub, servis, gecikme, tarih) birlikte yapılmalı."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        request = candidate;
        return true;
    }

    /// <summary>Cursor'ı filtreler, sıralama ve kullanıcıya bağlayan kanonik özet.</summary>
    public string FilterFingerprint(Guid userId)
    {
        var canonical = string.Join('|',
            SearchKind, SearchValue ?? string.Empty,
            string.Join(',', Statuses.Select(s => s.ToString()).Order(StringComparer.Ordinal)),
            HubCode ?? string.Empty, ServiceLevel?.ToString() ?? string.Empty, CarrierCode ?? string.Empty, Delayed?.ToString() ?? string.Empty,
            HasOpenException?.ToString() ?? string.Empty,
            CreatedFrom?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            CreatedTo?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            SortToken, userId.ToString("N"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)).AsSpan(0, 16));
    }

    private static (ShipmentSearchKind, string?) ClassifySearch(string? q, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(q))
        {
            return (ShipmentSearchKind.None, null);
        }

        if (Patterns.TrackingNumber().IsMatch(q))
        {
            return (ShipmentSearchKind.TrackingNumber, q);
        }

        if (Patterns.LabelCode().IsMatch(q))
        {
            return (ShipmentSearchKind.LabelCode, q);
        }

        if (Patterns.Last4().IsMatch(q))
        {
            return (ShipmentSearchKind.RecipientPhoneLast4, q);
        }

        if (q.Length > 64 || string.IsNullOrWhiteSpace(q))
        {
            errors["q"] = ["Arama değeri tracking no, koli etiketi, harici referans (≤ 64) veya telefonun son 4 hanesi olmalı."];
            return (ShipmentSearchKind.None, null);
        }

        return (ShipmentSearchKind.ExternalReference, q);
    }

    private static DateTimeOffset? ParseInstant(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (!Patterns.OffsetSuffix().IsMatch(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            errors[field] = ["Ofsetli ISO-8601 zaman olmalı (ör. 2026-10-01T00:00:00+03:00 veya ...Z)."];
            return null;
        }

        return parsed.ToUniversalTime();
    }
}

internal static partial class Patterns
{
    [GeneratedRegex("^[A-Z]{3}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    public static partial Regex HubCode();

    [GeneratedRegex("^LS[0-9]{10}$", RegexOptions.CultureInvariant)]
    public static partial Regex TrackingNumber();

    [GeneratedRegex("^[A-Z0-9]{2,16}$", RegexOptions.CultureInvariant)]
    public static partial Regex CarrierCode();

    [GeneratedRegex("^LS[0-9]{10}-[0-9]{2}$", RegexOptions.CultureInvariant)]
    public static partial Regex LabelCode();

    [GeneratedRegex("^[0-9]{4}$", RegexOptions.CultureInvariant)]
    public static partial Regex Last4();

    [GeneratedRegex(@"(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    public static partial Regex OffsetSuffix();
}

public sealed record ShipmentListItem(
    Guid Id,
    string TrackingNumber,
    string Status,
    string ServiceLevel,
    DateOnly PromisedDeliveryDate,
    bool IsDelayed,
    string OriginHubCode,
    string DestinationHubCode,
    string? CurrentHubCode,
    string? CarrierCode,
    string RecipientCity,
    int PackageCount,
    int OpenExceptionCount,
    DateTime StatusChangedAt,
    DateTime CreatedAt);

public sealed record ShipmentListResponse(IReadOnlyList<ShipmentListItem> Items, string? NextCursor, int Limit);

public abstract record ShipmentListOutcome
{
    private ShipmentListOutcome()
    {
    }

    public sealed record Page(ShipmentListResponse Response) : ShipmentListOutcome;

    public sealed record InvalidCursor : ShipmentListOutcome;

    public sealed record CursorMismatch : ShipmentListOutcome;
}

/// <summary>
/// Yetkili gönderi listesi. Hub kapsamı ve tüm filtreler SQL'de uygulanır; sayfalama keyset (sıralama alanı, id) ile yapılır.
/// Konum değerleri tick hassasiyetinde saklanır (timestamptz mikro saniye kaybı yok).
/// </summary>
public sealed class ListShipmentsHandler(
    LogisticsDbContext db,
    PromisedDeliveryCalculator calendar,
    TimeProvider timeProvider,
    IDataProtectionProvider dataProtection)
{
    private const int CursorVersion = 1;
    private readonly IDataProtector _cursorProtector = dataProtection.CreateProtector("Lst.Shipments.ListCursor.v1");

    public async Task<ShipmentListOutcome> HandleAsync(ShipmentListRequest request, ActorAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        var fingerprint = request.FilterFingerprint(access.UserId);
        CursorPayload? position = null;
        if (request.Cursor is not null)
        {
            position = TryReadCursor(request.Cursor);
            if (position is null)
            {
                return new ShipmentListOutcome.InvalidCursor();
            }

            if (position.Filter != fingerprint)
            {
                return new ShipmentListOutcome.CursorMismatch();
            }
        }

        var hubs = await db.Set<Hub>().AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, cancellationToken);
        var carriers = await db.Set<CarrierRow>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);
        var today = calendar.TodayLocal(timeProvider.GetUtcNow());

        var query = db.Set<Shipment>().AsNoTracking();

        if (!access.IsOperations)
        {
            // Kapsam SQL'de: kapsam dışı satır ne sayfaya ne cursor'a girer.
            var scope = access.IsHubOperator ? access.Hubs.Keys.ToArray() : [];
            query = query.Where(s => scope.Contains(s.OriginHubId) || scope.Contains(s.DestinationHubId)
                || (s.CurrentHubId != null && scope.Contains(s.CurrentHubId.Value)));
        }

        query = ApplyFilters(query, request, hubs, today);
        if (request.CarrierCode is not null)
        {
            var carrierId = carriers.FirstOrDefault(x => x.Value == request.CarrierCode).Key;
            query = query.Where(s => s.CarrierId == carrierId);
        }
        if (request.HasOpenException is { } hasOpen)
        {
            var open = db.Set<OpenShipmentExceptionRow>();
            query = hasOpen
                ? query.Where(s => open.Any(x => x.ShipmentId == s.Id))
                : query.Where(s => !open.Any(x => x.ShipmentId == s.Id));
        }
        query = ApplyKeyset(query, request, position);
        query = ApplyOrder(query, request);

        var rows = await query
            .Take(request.Limit + 1)
            .Select(s => new
            {
                s.Id,
                s.TrackingNumber,
                s.Status,
                s.ServiceLevel,
                s.PromisedDeliveryDate,
                s.OriginHubId,
                s.DestinationHubId,
                s.CurrentHubId,
                s.CarrierId,
                s.PackageCount,
                s.StatusChangedAt,
                s.CreatedAt,
                RecipientCity = s.Parties.Where(p => p.Role == PartyRole.Recipient).Select(p => p.City).FirstOrDefault(),
                OpenExceptionCount = db.Set<OpenShipmentExceptionRow>().Count(x => x.ShipmentId == s.Id),
            })
            .ToListAsync(cancellationToken);

        var page = rows.Take(request.Limit).ToList();
        string? nextCursor = null;
        if (rows.Count > request.Limit)
        {
            var last = page[^1];
            var key = request.SortField switch
            {
                ShipmentSortField.CreatedAt => last.CreatedAt.UtcTicks,
                ShipmentSortField.StatusChangedAt => last.StatusChangedAt.UtcTicks,
                _ => last.PromisedDeliveryDate.DayNumber,
            };
            nextCursor = WriteCursor(new CursorPayload(CursorVersion, request.SortToken, key, last.Id, fingerprint));
        }

        var items = page.Select(r => new ShipmentListItem(
                r.Id,
                r.TrackingNumber,
                r.Status.ToString(),
                r.ServiceLevel.ToString(),
                r.PromisedDeliveryDate,
                r.Status.IsDelayed(r.PromisedDeliveryDate, today),
                hubs[r.OriginHubId],
                hubs[r.DestinationHubId],
                r.CurrentHubId is { } c ? hubs[c] : null,
                r.CarrierId is { } carrierId ? carriers[carrierId] : null,
                r.RecipientCity ?? string.Empty,
                r.PackageCount,
                r.OpenExceptionCount,
                r.StatusChangedAt.UtcDateTime,
                r.CreatedAt.UtcDateTime))
            .ToList();

        return new ShipmentListOutcome.Page(new ShipmentListResponse(items, nextCursor, request.Limit));
    }

    private static IQueryable<Shipment> ApplyFilters(
        IQueryable<Shipment> query, ShipmentListRequest request, Dictionary<Guid, string> hubs, DateOnly today)
    {
        switch (request.SearchKind)
        {
            case ShipmentSearchKind.TrackingNumber:
                query = query.Where(s => s.TrackingNumber == request.SearchValue);
                break;
            case ShipmentSearchKind.LabelCode:
                query = query.Where(s => s.Packages.Any(p => p.LabelCode == request.SearchValue));
                break;
            case ShipmentSearchKind.ExternalReference:
                query = query.Where(s => s.ExternalReference == request.SearchValue);
                break;
            case ShipmentSearchKind.RecipientPhoneLast4:
                query = query.Where(s => s.Parties.Any(p => p.Role == PartyRole.Recipient && p.PhoneLast4 == request.SearchValue));
                break;
        }

        if (request.Statuses.Count > 0)
        {
            var statuses = request.Statuses.ToArray();
            query = query.Where(s => statuses.Contains(s.Status));
        }

        if (request.HubCode is not null)
        {
            // Bilinmeyen hub kodu boş sonuç verir (Guid.Empty hiçbir satırla eşleşmez).
            var hubId = hubs.FirstOrDefault(h => h.Value == request.HubCode).Key;
            query = query.Where(s => s.OriginHubId == hubId || s.DestinationHubId == hubId || s.CurrentHubId == hubId);
        }

        if (request.ServiceLevel is { } level)
        {
            query = query.Where(s => s.ServiceLevel == level);
        }

        if (request.Delayed is { } delayed)
        {
            var open = new[] { ShipmentStatus.Delivered, ShipmentStatus.ReturnedToSender, ShipmentStatus.Cancelled, ShipmentStatus.ReturningToSender };
            query = delayed
                ? query.Where(s => !open.Contains(s.Status) && s.PromisedDeliveryDate < today)
                : query.Where(s => open.Contains(s.Status) || s.PromisedDeliveryDate >= today);
        }

        if (request.CreatedFrom is { } from)
        {
            query = query.Where(s => s.CreatedAt >= from);
        }

        if (request.CreatedTo is { } to)
        {
            query = query.Where(s => s.CreatedAt < to);
        }

        return query;
    }

    private static IQueryable<Shipment> ApplyKeyset(IQueryable<Shipment> query, ShipmentListRequest request, CursorPayload? position)
    {
        if (position is null)
        {
            return query;
        }

        var id = position.Id;
        switch (request.SortField)
        {
            case ShipmentSortField.CreatedAt:
            {
                var key = new DateTimeOffset(position.Key, TimeSpan.Zero);
                return request.Descending
                    ? query.Where(s => EF.Functions.LessThan(ValueTuple.Create(s.CreatedAt, s.Id), ValueTuple.Create(key, id)))
                    : query.Where(s => EF.Functions.GreaterThan(ValueTuple.Create(s.CreatedAt, s.Id), ValueTuple.Create(key, id)));
            }

            case ShipmentSortField.StatusChangedAt:
            {
                var key = new DateTimeOffset(position.Key, TimeSpan.Zero);
                return request.Descending
                    ? query.Where(s => EF.Functions.LessThan(ValueTuple.Create(s.StatusChangedAt, s.Id), ValueTuple.Create(key, id)))
                    : query.Where(s => EF.Functions.GreaterThan(ValueTuple.Create(s.StatusChangedAt, s.Id), ValueTuple.Create(key, id)));
            }

            default:
            {
                var key = DateOnly.FromDayNumber(checked((int)position.Key));
                return request.Descending
                    ? query.Where(s => EF.Functions.LessThan(ValueTuple.Create(s.PromisedDeliveryDate, s.Id), ValueTuple.Create(key, id)))
                    : query.Where(s => EF.Functions.GreaterThan(ValueTuple.Create(s.PromisedDeliveryDate, s.Id), ValueTuple.Create(key, id)));
            }
        }
    }

    private static IQueryable<Shipment> ApplyOrder(IQueryable<Shipment> query, ShipmentListRequest request) =>
        (request.SortField, request.Descending) switch
        {
            (ShipmentSortField.CreatedAt, true) => query.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id),
            (ShipmentSortField.CreatedAt, false) => query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id),
            (ShipmentSortField.StatusChangedAt, true) => query.OrderByDescending(s => s.StatusChangedAt).ThenByDescending(s => s.Id),
            (ShipmentSortField.StatusChangedAt, false) => query.OrderBy(s => s.StatusChangedAt).ThenBy(s => s.Id),
            (_, true) => query.OrderByDescending(s => s.PromisedDeliveryDate).ThenByDescending(s => s.Id),
            _ => query.OrderBy(s => s.PromisedDeliveryDate).ThenBy(s => s.Id),
        };

    private string WriteCursor(CursorPayload payload) =>
        _cursorProtector.Protect(JsonSerializer.Serialize(payload));

    private CursorPayload? TryReadCursor(string cursor)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(_cursorProtector.Unprotect(cursor));
            return payload is { V: CursorVersion } ? payload : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sürümlü cursor yükü: sıralama, konum anahtarı (UTC tick veya DayNumber), tie-breaker id, filtre özeti.</summary>
    internal sealed record CursorPayload(int V, string Sort, long Key, Guid Id, string Filter);
}
