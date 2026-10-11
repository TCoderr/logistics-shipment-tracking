using System.Net;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace Lst.IntegrationTests;

/// <summary>
/// Dilim 3 liste/arama/sayfalama. Her test sahte saatle benzersiz bir zaman penceresinde kendi veri setini oluşturur ve
/// createdFrom/createdTo ile izole olur; saat donuk olduğu için tüm kayıtlar aynı created_at (mikro saniye) değerini alır.
/// </summary>
public sealed class ShipmentListTests(LstDatabaseFixture db)
{
    private static readonly string[] AllowedItemFields =
    [
        "id", "trackingNumber", "status", "serviceLevel", "promisedDeliveryDate", "isDelayed", "originHubCode", "destinationHubCode",
        "currentHubCode", "carrierCode", "recipientCity", "packageCount", "openExceptionCount", "statusChangedAt", "createdAt",
    ];

    private sealed record Dataset(
        LstApiFactory Factory,
        FakeTimeProvider Clock,
        DateTimeOffset WindowStart,
        List<Created> Shipments) : IDisposable
    {
        public string Window =>
            $"createdFrom={Uri.EscapeDataString(WindowStart.ToString("O"))}&createdTo={Uri.EscapeDataString(WindowStart.AddHours(1).ToString("O"))}";

        public void Dispose() => Factory.Dispose();
    }

    private sealed record Created(Guid Id, string TrackingNumber, string LabelCode, string ExternalReference, string RecipientPhone, string Origin, string Service);

    private async Task<Dataset> CreateDatasetAsync(params (string Origin, string Service)[] specs)
    {
        // Diğer testlerin (gerçek saat) kayıtlarıyla çakışmayan, gelecekte benzersiz bir pencere.
        var start = DateTimeOffset.UtcNow.Date.AddDays(5 + Random.Shared.Next(0, 300)).AddMinutes(Random.Shared.Next(0, 1000));
        var windowStart = new DateTimeOffset(start, TimeSpan.Zero);
        var clock = new FakeTimeProvider(windowStart.AddMinutes(1));
        var factory = new LstApiFactory(db, clock);
        using var client = factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("ops.manager"));
        var created = new List<Created>();
        foreach (var (origin, service) in specs)
        {
            var json = new ShipmentJson(origin: origin).With(n => n["serviceLevel"] = service);
            using var response = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), json.ToString());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await TestApi.JsonAsync(response);
            created.Add(new Created(
                body.GetProperty("id").GetGuid(),
                body.GetProperty("trackingNumber").GetString()!,
                body.GetProperty("packages")[0].GetProperty("labelCode").GetString()!,
                json.ExternalReference,
                json.RecipientPhone,
                origin,
                service));
        }

        return new Dataset(factory, clock, windowStart, created);
    }

    private static async Task<(List<JsonElement> Items, int Pages)> ReadAllPagesAsync(HttpClient client, string query, int limit)
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/shipments?{query}&limit={limit}" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            using var response = await client.GetAsync(new Uri(url, UriKind.Relative), TestApi.Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var body = await TestApi.JsonAsync(response);
            var pageItems = body.GetProperty("items").EnumerateArray().ToList();
            Assert.True(pageItems.Count <= limit);
            items.AddRange(pageItems);
            cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("nextCursor").GetString();
            pages++;
            Assert.True(pages < 50, "sayfalama sonlanmadı");
        }
        while (cursor is not null);

        return (items, pages);
    }

    private static string UuidOrder(Guid id) => id.ToString("D");

    [Fact]
    public async Task Same_timestamp_rows_page_without_skip_or_duplicate_in_both_directions()
    {
        using var data = await CreateDatasetAsync(
            Enumerable.Range(0, 7).Select(_ => ("IST-01", "Standard")).ToArray());
        using var client = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("ops.agent"));

        var createdAt = new HashSet<string>();
        var (desc, pages) = await ReadAllPagesAsync(client, data.Window + "&sort=-createdAt", 2);
        Assert.Equal(4, pages);
        var descIds = desc.Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(data.Shipments.Select(s => s.Id).OrderByDescending(UuidOrder, StringComparer.Ordinal), descIds);
        foreach (var item in desc)
        {
            createdAt.Add(item.GetProperty("createdAt").GetString()!);
        }

        Assert.Single(createdAt); // hepsi aynı mikro saniye

        var (asc, _) = await ReadAllPagesAsync(client, data.Window + "&sort=createdAt", 3);
        Assert.Equal(data.Shipments.Select(s => s.Id).OrderBy(UuidOrder, StringComparer.Ordinal), asc.Select(i => i.GetProperty("id").GetGuid()));

        var (statusChanged, _) = await ReadAllPagesAsync(client, data.Window + "&sort=-statusChangedAt", 1);
        Assert.Equal(7, statusChanged.Select(i => i.GetProperty("id").GetGuid()).Distinct().Count());
    }

    [Fact]
    public async Task Promised_date_sort_orders_by_date_then_id()
    {
        using var data = await CreateDatasetAsync(
            ("IST-01", "Standard"), ("IST-01", "Express"), ("IST-01", "Standard"), ("IST-01", "Express"), ("IST-01", "Standard"));
        using var client = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("ops.agent"));

        var (items, _) = await ReadAllPagesAsync(client, data.Window + "&sort=promisedDeliveryDate", 2);
        var keys = items.Select(i => (Date: i.GetProperty("promisedDeliveryDate").GetString()!, Id: UuidOrder(i.GetProperty("id").GetGuid()))).ToList();
        Assert.Equal(5, keys.Count);
        Assert.Equal(keys.OrderBy(k => k.Date, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal), keys);
        Assert.Equal(2, keys.Select(k => k.Date).Distinct().Count());
    }

    [Fact]
    public async Task Hub_operator_sees_only_scoped_rows_and_pagination_does_not_leak()
    {
        using var data = await CreateDatasetAsync(
            ("IST-01", "Standard"), ("IST-01", "Standard"), ("IST-01", "Express"), ("IST-01", "Standard"),
            ("IZM-01", "Standard"), ("IZM-01", "Express"), ("IZM-01", "Standard"));
        using var izm = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(izm, db.User("hub.izm"));

        var (izmItems, izmPages) = await ReadAllPagesAsync(izm, data.Window, 1);
        Assert.Equal(3, izmPages);
        Assert.All(izmItems, i => Assert.Equal("IZM-01", i.GetProperty("originHubCode").GetString()));
        Assert.Equal(
            data.Shipments.Where(s => s.Origin == "IZM-01").Select(s => s.Id).Order(),
            izmItems.Select(i => i.GetProperty("id").GetGuid()).Order());

        // Kapsam dışı hub filtresi boş döner; kapsam dışı satır sayımı sızmaz (toplam sayı yok).
        var (outOfScope, _) = await ReadAllPagesAsync(izm, data.Window + "&hubCode=IST-01", 5);
        Assert.Empty(outOfScope);

        using var ist = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(ist, db.User("hub.ist"));
        var (istItems, _) = await ReadAllPagesAsync(ist, data.Window, 2);
        Assert.Equal(4, istItems.Count);

        using var ops = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(ops, db.User("ops.agent"));
        var (opsItems, _) = await ReadAllPagesAsync(ops, data.Window, 200);
        Assert.Equal(7, opsItems.Count);
    }

    [Fact]
    public async Task Filters_combine_and_list_items_expose_no_personal_data()
    {
        using var data = await CreateDatasetAsync(
            ("IST-01", "Standard"), ("IST-01", "Express"), ("IZM-01", "Express"), ("IZM-01", "Standard"));
        using var client = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("ops.agent"));

        var (expressIzm, _) = await ReadAllPagesAsync(client, data.Window + "&serviceLevel=Express&hubCode=IZM-01", 10);
        var only = Assert.Single(expressIzm);
        Assert.Equal(data.Shipments.Single(s => s.Origin == "IZM-01" && s.Service == "Express").Id, only.GetProperty("id").GetGuid());

        var (created, _) = await ReadAllPagesAsync(client, data.Window + "&status=Created&status=AtHub", 10);
        Assert.Equal(4, created.Count);
        var (delivered, _) = await ReadAllPagesAsync(client, data.Window + "&status=Delivered", 10);
        Assert.Empty(delivered);

        var (notDelayed, _) = await ReadAllPagesAsync(client, data.Window + "&delayed=false", 10);
        Assert.Equal(4, notDelayed.Count);
        Assert.All(notDelayed, i => Assert.False(i.GetProperty("isDelayed").GetBoolean()));

        foreach (var item in notDelayed)
        {
            Assert.Equal(AllowedItemFields.Order(StringComparer.Ordinal), item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("Ankara", item.GetProperty("recipientCity").GetString());
            var raw = item.GetRawText();
            foreach (var shipment in data.Shipments)
            {
                Assert.DoesNotContain(shipment.RecipientPhone, raw, StringComparison.Ordinal);
            }
        }

        // Saat söz verilen tarihlerin ötesine ilerleyince hepsi gecikmiş olur.
        data.Clock.Advance(TimeSpan.FromDays(10));
        await TestApi.LoginAsync(client, db.User("ops.agent"));
        var (delayed, _) = await ReadAllPagesAsync(client, data.Window + "&delayed=true", 10);
        Assert.Equal(4, delayed.Count);
        Assert.All(delayed, i => Assert.True(i.GetProperty("isDelayed").GetBoolean()));
    }

    [Fact]
    public async Task Search_by_tracking_number_label_reference_and_phone_last4()
    {
        using var data = await CreateDatasetAsync(("IST-01", "Standard"), ("IST-01", "Standard"));
        using var client = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("ops.agent"));
        var target = data.Shipments[1];

        foreach (var q in new[] { target.TrackingNumber, target.LabelCode, target.ExternalReference })
        {
            var (found, _) = await ReadAllPagesAsync(client, "q=" + Uri.EscapeDataString(q), 10);
            Assert.Equal(target.Id, Assert.Single(found).GetProperty("id").GetGuid());
        }

        var last4 = target.RecipientPhone[^4..];
        using (var alone = await client.GetAsync(new Uri($"/api/shipments?q={last4}", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, alone.StatusCode);
            Assert.True((await TestApi.JsonAsync(alone)).GetProperty("errors").TryGetProperty("q", out _));
        }

        var (byPhone, _) = await ReadAllPagesAsync(client, data.Window + "&q=" + last4, 10);
        Assert.Contains(byPhone, i => i.GetProperty("id").GetGuid() == target.Id);
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("limit=abc", "limit")]
    [InlineData("sort=name", "sort")]
    [InlineData("status=Lost", "status")]
    [InlineData("status=created", "status")]
    [InlineData("serviceLevel=Overnight", "serviceLevel")]
    [InlineData("delayed=yes", "delayed")]
    [InlineData("hubCode=ist", "hubCode")]
    [InlineData("createdFrom=2026-10-01T00:00:00", "createdFrom")]
    [InlineData("createdFrom=2026-10-02T00:00:00Z&createdTo=2026-10-01T00:00:00Z", "createdTo")]
    [InlineData("cursor=", "cursor")]
    public async Task Invalid_query_parameters_return_field_validation_errors(string query, string field)
    {
        using var factory = new LstApiFactory(db);
        using var client = factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("ops.agent"));
        using var response = await client.GetAsync(new Uri("/api/shipments?" + query, UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", await TestApi.ProblemCodeAsync(response));
        Assert.True((await TestApi.JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task Tampered_foreign_or_mismatched_cursor_is_rejected()
    {
        using var data = await CreateDatasetAsync(("IST-01", "Standard"), ("IST-01", "Standard"), ("IST-01", "Standard"));
        using var ops = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(ops, db.User("ops.agent"));
        using var first = await ops.GetAsync(new Uri($"/api/shipments?{data.Window}&limit=1", UriKind.Relative), TestApi.Ct);
        var cursor = (await TestApi.JsonAsync(first)).GetProperty("nextCursor").GetString()!;

        var tampered = cursor[..^3] + (cursor[^3] == 'A' ? "B" : "A") + cursor[^2..];
        using (var bad = await ops.GetAsync(new Uri($"/api/shipments?{data.Window}&limit=1&cursor={Uri.EscapeDataString(tampered)}", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("invalid-cursor", await TestApi.ProblemCodeAsync(bad));
        }

        using (var changedFilter = await ops.GetAsync(new Uri($"/api/shipments?{data.Window}&limit=1&serviceLevel=Standard&cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, changedFilter.StatusCode);
            Assert.Equal("cursor-mismatch", await TestApi.ProblemCodeAsync(changedFilter));
        }

        using var other = data.Factory.CreateHttpsClient();
        await TestApi.LoginAsync(other, db.User("ops.manager"));
        using var foreign = await other.GetAsync(new Uri($"/api/shipments?{data.Window}&limit=1&cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal("cursor-mismatch", await TestApi.ProblemCodeAsync(foreign));
    }

    [Fact]
    public async Task Unauthenticated_list_is_401()
    {
        using var factory = new LstApiFactory(db);
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync(new Uri("/api/shipments", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
