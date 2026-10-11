using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Lst.IntegrationTests.Infrastructure;

public sealed record Session(Guid UserId, string CsrfToken);

public static class TestApi
{
    public const string CsrfHeader = "X-CSRF-TOKEN";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string NewKey() => "it-" + Guid.NewGuid().ToString("N");

    public static async Task<string> GetAnonymousCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/auth/csrf", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("csrfToken").GetString()!;
    }

    public static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string? csrfToken, string userName, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { userName, password }),
        };
        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfHeader, csrfToken);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Anonim CSRF → login → /me (kimlikli yeni CSRF). Sonraki isteklerde kimlikli token varsayılan header olur.</summary>
    public static async Task<Session> LoginAsync(HttpClient client, TestUser user)
    {
        client.DefaultRequestHeaders.Remove(CsrfHeader);
        var anonymous = await GetAnonymousCsrfAsync(client);
        using (var login = await PostLoginAsync(client, anonymous, user.UserName, user.Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        using var me = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = body.GetProperty("csrfToken").GetString()!;
        client.DefaultRequestHeaders.Add(CsrfHeader, token);
        return new Session(body.GetProperty("id").GetGuid(), token);
    }

    public static async Task<HttpResponseMessage> ScanAsync(
        HttpClient client, string hubCode, string labelCode, string scanType, DateTimeOffset occurredAt, string? clientScanId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hubs/{hubCode}/scans")
        {
            Content = JsonContent.Create(new
            {
                clientScanId = clientScanId ?? "scan-" + Guid.NewGuid().ToString("N"),
                labelCode,
                scanType,
                occurredAt = occurredAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
            }),
        };
        return await client.SendAsync(request, Ct);
    }

    public static async Task<HttpResponseMessage> PostCommandAsync(HttpClient client, string path, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    public static async Task<(JsonElement Body, string ETag)> GetDetailAsync(HttpClient client, Guid shipmentId)
    {
        using var response = await client.GetAsync(new Uri($"/api/shipments/{shipmentId}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response), response.Headers.ETag!.ToString());
    }

    /// <summary>Gönderi oluşturur; (id, paket etiketleri, ETag) döner.</summary>
    public static async Task<(Guid Id, string[] Labels, string ETag)> CreateAsync(HttpClient client, ShipmentJson json)
    {
        using var response = await CreateShipmentAsync(client, NewKey(), json.ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        return (
            body.GetProperty("id").GetGuid(),
            body.GetProperty("packages").EnumerateArray().Select(p => p.GetProperty("labelCode").GetString()!).ToArray(),
            response.Headers.ETag!.ToString());
    }

    public static async Task<HttpResponseMessage> CreateShipmentAsync(HttpClient client, string? idempotencyKey, string json, string? csrfOverride = null, bool omitCsrf = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shipments")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (!omitCsrf && csrfOverride is null)
        {
            return await client.SendAsync(request, Ct);
        }

        // Varsayılan CSRF header'ı bu istek için kaldırılır (yalnız sıralı testlerde kullanılır) ve sonra geri konur.
        var saved = client.DefaultRequestHeaders.TryGetValues(CsrfHeader, out var values) ? values.ToList() : [];
        client.DefaultRequestHeaders.Remove(CsrfHeader);
        try
        {
            if (csrfOverride is not null)
            {
                request.Headers.Add(CsrfHeader, csrfOverride);
            }

            return await client.SendAsync(request, Ct);
        }
        finally
        {
            if (saved.Count > 0)
            {
                client.DefaultRequestHeaders.Add(CsrfHeader, saved);
            }
        }
    }

    /// <summary>
    /// Gövde byte dizisi olarak okunur: HttpContent tamponlanmış okuma stream'ini önbelleğe alır ve ReadFromJsonAsync onu
    /// kapatır; byte okuması ise aynı yanıtın birden çok kez (ör. önce problem kodu, sonra errors) okunmasına izin verir.
    /// </summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsByteArrayAsync(Ct));

    public static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await JsonAsync(response)).GetProperty("code").GetString()!;
    }

    public static async Task<long> CountAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(await ScalarAsync(connection, sql, parameters), CultureInfo.InvariantCulture);

    public static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
#pragma warning disable CA2100 // Test SQL'i sabittir; değerler parametreyle verilir.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(Ct);
    }
}

/// <summary>Benzersiz, sentetik gönderi istekleri. Her test kendi harici referansını ve PII değerlerini üretir.</summary>
public sealed class ShipmentJson
{
    private readonly JsonObject _node;

    public ShipmentJson(string? origin = "IST-01", string? destination = "ANK-02", int packageCount = 2)
    {
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        ExternalReference = "IT-" + suffix;
        RecipientName = "Alici Test " + suffix;
        SenderName = "Gonderen Test " + suffix;
        RecipientPhone = "+90532" + RandomNumberGenerator.GetInt32(1_000_000, 9_999_999).ToString(CultureInfo.InvariantCulture);
        RecipientAddressLine = "Test Sokak " + suffix + " No: 3";
        _node = new JsonObject
        {
            ["serviceLevel"] = "Standard",
            ["externalReference"] = ExternalReference,
            ["originHubCode"] = origin,
            ["destinationHubCode"] = destination,
            ["sender"] = new JsonObject
            {
                ["fullName"] = SenderName,
                ["companyName"] = "Test Tekstil Ltd.",
                ["phone"] = "+902124445566",
                ["email"] = "lojistik@test.example",
                ["address"] = Address("Gonderen Cd. " + suffix, "Bağcılar", "İstanbul", "34200"),
            },
            ["recipient"] = new JsonObject
            {
                ["fullName"] = RecipientName,
                ["companyName"] = null,
                ["phone"] = RecipientPhone,
                ["email"] = null,
                ["address"] = Address(RecipientAddressLine, "Çankaya", "Ankara", "06420"),
            },
            ["packages"] = new JsonArray(Enumerable.Range(0, packageCount).Select(i => (JsonNode)new JsonObject
            {
                ["weightGrams"] = 2400 - i,
                ["lengthMm"] = 400,
                ["widthMm"] = 300,
                ["heightMm"] = 200,
                ["contentDescription"] = "Tekstil",
            }).ToArray()),
            ["declaredValue"] = new JsonObject { ["amount"] = 1850.00m, ["currency"] = "TRY" },
        };
    }

    public string ExternalReference { get; }

    public string RecipientName { get; }

    public string SenderName { get; }

    public string RecipientPhone { get; }

    public string RecipientAddressLine { get; }

    public JsonObject Node => _node;

    public IEnumerable<string> PiiValues => [RecipientName, SenderName, RecipientPhone, RecipientAddressLine];

    public override string ToString() => _node.ToJsonString();

    public string ToReorderedJson() => Reorder(_node).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public ShipmentJson With(Action<JsonObject> change)
    {
        change(_node);
        return this;
    }

    private static JsonObject Address(string line1, string district, string city, string postalCode) => new()
    {
        ["line1"] = line1,
        ["line2"] = null,
        ["district"] = district,
        ["city"] = city,
        ["postalCode"] = postalCode,
        ["countryCode"] = "TR",
    };

    private static JsonObject Reorder(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var (key, value) in source.Reverse())
        {
            result[key] = value switch
            {
                JsonObject o => Reorder(o),
                JsonArray a => new JsonArray(a.Select(i => i is JsonObject io ? (JsonNode)Reorder(io) : i?.DeepClone()).ToArray()),
                _ => value?.DeepClone(),
            };
        }

        return result;
    }
}
