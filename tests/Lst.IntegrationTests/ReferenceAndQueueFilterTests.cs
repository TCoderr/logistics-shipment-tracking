using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;

namespace Lst.IntegrationTests;

/// <summary>
/// Dilim 9 için eklenen arayüz destek uçları: hub/carrier referans verisi, istisna kuyruğunda gönderi filtresi ve
/// demo simülatör keşif ucu (yalnız Development + bayrak).
/// </summary>
public sealed class ReferenceAndQueueFilterTests(LstDatabaseFixture db) : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddSeconds(1);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private DateTimeOffset Next() => _clock = _clock.AddSeconds(10);

    private LstApiFactory Factory(string environment = "Testing", Dictionary<string, string?>? settings = null)
    {
        var factory = new LstApiFactory(db, environment: environment, settings: settings);
        _disposables.Add(factory);
        return factory;
    }

    private async Task<HttpClient> Login(LstApiFactory factory, string user)
    {
        var client = factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User(user));
        return client;
    }

    private static async Task Ok(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(TestApi.Ct)}");
    }

    [Fact]
    public async Task Hub_reference_lists_all_hubs_for_internal_users_without_personal_data()
    {
        var factory = Factory();
        using var anonymous = factory.CreateHttpsClient();
        using (var denied = await anonymous.GetAsync("/api/reference/hubs", TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        foreach (var user in new[] { "ops.agent", "hub.izm" })
        {
            var client = await Login(factory, user);
            using var response = await client.GetAsync("/api/reference/hubs", TestApi.Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var items = (await TestApi.JsonAsync(response)).GetProperty("items").EnumerateArray().ToArray();

            var codes = items.Select(i => i.GetProperty("code").GetString()).ToArray();
            Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
            Assert.Contains("IST-01", codes);
            Assert.Contains("ANK-02", codes);
            Assert.Contains("IZM-01", codes);
            var inactive = items.Single(i => i.GetProperty("code").GetString() == "BRS-01");
            Assert.False(inactive.GetProperty("isActive").GetBoolean());
            Assert.All(items, item => Assert.Equal(
                ["city", "code", "isActive", "name"],
                item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        }
    }

    [Fact]
    public async Task Carrier_reference_is_limited_to_operations_staff()
    {
        var factory = Factory();
        var hub = await Login(factory, "hub.ist");
        using (var forbidden = await hub.GetAsync("/api/reference/carriers", TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var ops = await Login(factory, "ops.agent");
        using var response = await ops.GetAsync("/api/reference/carriers", TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var simex = Assert.Single((await TestApi.JsonAsync(response)).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("code").GetString() == "SIMEX");
        Assert.True(simex.GetProperty("isActive").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(simex.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Exception_queue_filters_by_shipment_and_binds_the_cursor_to_that_filter()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var ist = await Login(factory, "hub.ist");

        // İki ayrı gönderide konsolidasyon reddi → her birinde bir PackageSplit istisnası.
        var shipments = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson(packageCount: 2));
            await Ok(TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", Next()));
            using var rejected = await TestApi.ScanAsync(ist, "IST-01", labels[0], "Departure", Next());
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            shipments.Add(id);
        }

        using var filtered = await ops.GetAsync($"/api/exceptions?shipmentId={shipments[0]}", TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var item = Assert.Single((await TestApi.JsonAsync(filtered)).GetProperty("items").EnumerateArray());
        Assert.Equal(shipments[0], item.GetProperty("shipment").GetProperty("id").GetGuid());
        Assert.Equal("PackageSplit", item.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("etag").GetString()));

        using var unknown = await ops.GetAsync($"/api/exceptions?shipmentId={Guid.CreateVersion7()}", TestApi.Ct);
        Assert.Empty((await TestApi.JsonAsync(unknown)).GetProperty("items").EnumerateArray());

        foreach (var invalid in new[] { "abc", "", Guid.Empty.ToString() })
        {
            using var bad = await ops.GetAsync($"/api/exceptions?shipmentId={invalid}", TestApi.Ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.True((await TestApi.JsonAsync(bad)).GetProperty("errors").TryGetProperty("shipmentId", out _));
        }

        // Filtresiz sorgunun cursor'ı gönderi filtreli sorguda kullanılamaz (filtre parmak izine dahildir).
        using var firstPage = await ops.GetAsync("/api/exceptions?limit=1", TestApi.Ct);
        var cursor = (await TestApi.JsonAsync(firstPage)).GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        using var mismatch = await ops.GetAsync($"/api/exceptions?limit=1&shipmentId={shipments[0]}&cursor={Uri.EscapeDataString(cursor)}", TestApi.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal("invalid-cursor", await TestApi.ProblemCodeAsync(mismatch));
    }

    [Fact]
    public async Task Simulator_discovery_exists_only_in_development_with_flag_and_only_for_ops_manager()
    {
        var testing = Factory();
        var managerInTesting = await Login(testing, "ops.manager");
        using (var missing = await managerInTesting.GetAsync("/api/dev/carrier-simulator", TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        var developmentWithoutFlag = Factory(environment: "Development");
        var managerWithoutFlag = await Login(developmentWithoutFlag, "ops.manager");
        using (var missing = await managerWithoutFlag.GetAsync("/api/dev/carrier-simulator", TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        var development = Factory(environment: "Development", settings: new() { ["Simulation:Enabled"] = "true" });
        var agent = await Login(development, "ops.agent");
        using (var forbidden = await agent.GetAsync("/api/dev/carrier-simulator", TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var manager = await Login(development, "ops.manager");
        using var response = await manager.GetAsync("/api/dev/carrier-simulator", TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestApi.JsonAsync(response);
        Assert.Equal("SIMEX", body.GetProperty("carrierCode").GetString());
        var scenarios = body.GetProperty("scenarios").EnumerateArray().Select(s => s.GetString()).ToArray();
        Assert.Contains("HappyPath", scenarios);
        Assert.Contains("OutOfOrderAfterDelivery", scenarios);

        // Keşif ucunun bildirdiği her senaryo, çalıştırma ucunun doğrulamasından geçer (geçersiz tracking no ile yalnız o alan hata verir).
        foreach (var scenario in scenarios)
        {
            using var run = await manager.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = "x", scenario }, TestApi.Ct);
            Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
            var errors = (await TestApi.JsonAsync(run)).GetProperty("errors");
            Assert.True(errors.TryGetProperty("trackingNumber", out _));
            Assert.False(errors.TryGetProperty("scenario", out JsonElement _), scenario);
        }
    }
}
