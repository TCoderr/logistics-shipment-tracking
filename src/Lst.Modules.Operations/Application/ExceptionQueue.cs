using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lst.Modules.Identity;
using Lst.Modules.Operations.Domain;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Operations.Application;

public sealed record ExceptionQueueQuery
{
    public string[]? Status { get; init; }

    public string[]? Type { get; init; }

    public string? HubCode { get; init; }

    public string? ShipmentId { get; init; }

    public string? AssignedTo { get; init; }

    public string? Overdue { get; init; }

    public string? Sort { get; init; }

    public string? Limit { get; init; }

    public string? Cursor { get; init; }
}

public sealed record AssigneeView(Guid Id, string DisplayName);

public sealed record ExceptionShipmentView(Guid Id, string TrackingNumber, string Status);

public sealed record ExceptionItem(
    Guid Id,
    string Type,
    string Severity,
    string Status,
    ExceptionShipmentView Shipment,
    string? HubCode,
    int OccurrenceCount,
    DateTime OpenedAt,
    DateTime DueAt,
    bool IsOverdue,
    AssigneeView? AssignedTo,
    DateTime? ResolvedAt,
    string? ResolutionCode,
    IReadOnlyList<string> AllowedResolutions,
    [property: System.Text.Json.Serialization.JsonPropertyName("etag")] string ETag);

public sealed record ExceptionQueueResponse(IReadOnlyList<ExceptionItem> Items, string? NextCursor, int Limit);

/// <summary>Doğrulanmış kuyruk isteği. Varsayılan: açık (Open+Assigned), vadeye göre artan; "vadesi geçmiş önce" doğal sonuçtur.</summary>
public sealed record ExceptionQueueRequest(
    IReadOnlyList<ExceptionStatus> Statuses,
    IReadOnlyList<ExceptionType> Types,
    string? HubCode,
    Guid? ShipmentId,
    string? AssignedTo,
    bool? Overdue,
    bool SortByDue,
    int Limit,
    string? Cursor)
{
    public static bool TryCreate(ExceptionQueueQuery query, out ExceptionQueueRequest? request, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(query);
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        request = null;

        var statuses = Parse<ExceptionStatus>(query.Status, "status", errors);
        var types = Parse<ExceptionType>(query.Type, "type", errors);
        Guid? shipmentId = null;
        if (query.ShipmentId is not null)
        {
            if (Guid.TryParseExact(query.ShipmentId, "D", out var parsedShipmentId) && parsedShipmentId != Guid.Empty)
            {
                shipmentId = parsedShipmentId;
            }
            else
            {
                errors["shipmentId"] = ["Gönderi kimliği (UUID) olmalı."];
            }
        }

        if (query.AssignedTo is not null && query.AssignedTo is not ("me" or "unassigned") && !Guid.TryParse(query.AssignedTo, out _))
        {
            errors["assignedTo"] = ["'me', 'unassigned' veya kullanıcı kimliği olmalı."];
        }

        bool? overdue = query.Overdue switch { null => null, "true" => true, "false" => false, _ => null };
        if (query.Overdue is not null && overdue is null)
        {
            errors["overdue"] = ["'true' veya 'false' olmalı."];
        }

        var sortByDue = query.Sort is null or "dueAt";
        if (query.Sort is not null and not ("dueAt" or "-openedAt"))
        {
            errors["sort"] = ["'dueAt' veya '-openedAt' olmalı."];
        }

        var limit = 50;
        if (query.Limit is not null && (!int.TryParse(query.Limit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200))
        {
            errors["limit"] = ["1 ile 200 arasında bir tam sayı olmalı."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        request = new ExceptionQueueRequest(
            statuses.Count > 0 ? statuses : [ExceptionStatus.Open, ExceptionStatus.Assigned],
            types, query.HubCode, shipmentId, query.AssignedTo, overdue, sortByDue, limit, string.IsNullOrEmpty(query.Cursor) ? null : query.Cursor);
        return true;
    }

    public string Fingerprint(Guid userId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            string.Join(',', Statuses.Order()), string.Join(',', Types.Order()), HubCode, ShipmentId?.ToString("N"), AssignedTo, Overdue, SortByDue, userId.ToString("N"))))
            .AsSpan(0, 16));

    private static List<T> Parse<T>(string[]? values, string field, Dictionary<string, string[]> errors)
        where T : struct, Enum
    {
        var result = new List<T>();
        foreach (var raw in values ?? [])
        {
            if (Enum.TryParse<T>(raw, ignoreCase: false, out var value) && raw == value.ToString())
            {
                result.Add(value);
            }
            else
            {
                errors[field] = ["Geçerli değerler: " + string.Join(", ", Enum.GetNames<T>()) + "."];
            }
        }

        return result;
    }
}

public abstract record ExceptionQueueOutcome
{
    private ExceptionQueueOutcome()
    {
    }

    public sealed record Page(ExceptionQueueResponse Response) : ExceptionQueueOutcome;

    public sealed record InvalidCursor : ExceptionQueueOutcome;
}

/// <summary>İstisna kuyruğu: filtreler SQL'de, keyset (dueAt,id) artan veya (openedAt,id) azalan; Data Protection korumalı cursor.</summary>
public sealed class ExceptionQueueHandler(LogisticsDbContext db, TimeProvider timeProvider, IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Lst.Operations.ExceptionCursor.v1");

    public async Task<ExceptionQueueOutcome> HandleAsync(ExceptionQueueRequest request, ActorAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);
        var now = timeProvider.GetUtcNow();
        var fingerprint = request.Fingerprint(access.UserId);
        Cursor? cursor = null;
        if (request.Cursor is not null)
        {
            cursor = Read(request.Cursor);
            if (cursor is null || cursor.Filter != fingerprint)
            {
                return new ExceptionQueueOutcome.InvalidCursor();
            }
        }

        var hubs = await db.Set<Hub>().AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, cancellationToken);
        var statuses = request.Statuses.ToArray();
        var query = db.Set<ShipmentException>().AsNoTracking().Where(x => statuses.Contains(x.Status));
        if (request.Types.Count > 0)
        {
            var types = request.Types.ToArray();
            query = query.Where(x => types.Contains(x.Type));
        }

        if (request.HubCode is not null)
        {
            var hubId = hubs.FirstOrDefault(h => h.Value == request.HubCode).Key;
            query = query.Where(x => x.HubId == hubId);
        }

        if (request.ShipmentId is { } shipmentId)
        {
            query = query.Where(x => x.ShipmentId == shipmentId);
        }

        switch (request.AssignedTo)
        {
            case "me":
                query = query.Where(x => x.AssignedToUserId == access.UserId);
                break;
            case "unassigned":
                query = query.Where(x => x.AssignedToUserId == null);
                break;
            case { } id:
                var assignee = Guid.Parse(id);
                query = query.Where(x => x.AssignedToUserId == assignee);
                break;
        }

        if (request.Overdue is { } overdue)
        {
            query = overdue ? query.Where(x => x.DueAt < now) : query.Where(x => x.DueAt >= now);
        }

        if (cursor is not null)
        {
            var key = new DateTimeOffset(cursor.Key, TimeSpan.Zero);
            query = request.SortByDue
                ? query.Where(x => EF.Functions.GreaterThan(ValueTuple.Create(x.DueAt, x.Id), ValueTuple.Create(key, cursor.Id)))
                : query.Where(x => EF.Functions.LessThan(ValueTuple.Create(x.OpenedAt, x.Id), ValueTuple.Create(key, cursor.Id)));
        }

        query = request.SortByDue
            ? query.OrderBy(x => x.DueAt).ThenBy(x => x.Id)
            : query.OrderByDescending(x => x.OpenedAt).ThenByDescending(x => x.Id);

        var rows = await query.Take(request.Limit + 1)
            .Select(x => new
            {
                Exception = x,
                Shipment = db.Set<Shipment>().Where(s => s.Id == x.ShipmentId).Select(s => new { s.TrackingNumber, s.Status }).First(),
                Assignee = db.Users.Where(u => u.Id == x.AssignedToUserId).Select(u => new { u.Id, u.DisplayName }).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var page = rows.Take(request.Limit).ToList();
        string? next = null;
        if (rows.Count > request.Limit)
        {
            var last = page[^1].Exception;
            next = _protector.Protect(JsonSerializer.Serialize(new Cursor(
                (request.SortByDue ? last.DueAt : last.OpenedAt).UtcTicks, last.Id, fingerprint)));
        }

        var items = page.Select(r => ToItem(r.Exception, r.Shipment.TrackingNumber, r.Shipment.Status,
            r.Assignee is null ? null : new AssigneeView(r.Assignee.Id, r.Assignee.DisplayName), hubs, now)).ToList();
        return new ExceptionQueueOutcome.Page(new ExceptionQueueResponse(items, next, request.Limit));
    }

    internal static ExceptionItem ToItem(
        ShipmentException x, string trackingNumber, ShipmentStatus shipmentStatus, AssigneeView? assignee, IReadOnlyDictionary<Guid, string> hubs, DateTimeOffset now) =>
        new(
            x.Id,
            x.Type.ToString(),
            x.Severity.ToString(),
            x.Status.ToString(),
            new ExceptionShipmentView(x.ShipmentId, trackingNumber, shipmentStatus.ToString()),
            x.HubId is { } h ? hubs[h] : null,
            x.OccurrenceCount,
            x.OpenedAt.UtcDateTime,
            x.DueAt.UtcDateTime,
            x.IsOpen && x.DueAt < now,
            assignee,
            x.ResolvedAt?.UtcDateTime,
            x.ResolutionCode?.ToString(),
            x.IsOpen ? ExceptionResolutionPolicy.AllowedFor(x.Type).Select(c => c.ToString()).ToList() : [],
            ExceptionETag.From(x.Version));

    private Cursor? Read(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<Cursor>(_protector.Unprotect(value));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal sealed record Cursor(long Key, Guid Id, string Filter);
}

public static class ExceptionETag
{
    public static string From(uint version) => $"\"{version}\"";
}
