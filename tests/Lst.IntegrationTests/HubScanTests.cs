using System.Net;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;
using Npgsql;

namespace Lst.IntegrationTests;

/// <summary>Dilim 4: hub taramaları gerçek PostgreSQL üzerinde (kilit, tekrar, konsolidasyon, stale, terminal, rollback).</summary>
public sealed class HubScanTests(LstDatabaseFixture db) : IDisposable
{
    private static readonly string[] ScanResponseFields = ["outcome", "reasonCode", "shipment", "package", "consolidation"];
    private readonly LstApiFactory _factory = new(db);
    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddSeconds(1);

    public void Dispose() => _factory.Dispose();

    /// <summary>Her çağrıda 10 sn ileri giden olay zamanı (gerçek saatin 15 dk ötesine geçmez).</summary>
    private DateTimeOffset Next() => _clock = _clock.AddSeconds(10);

    private async Task<HttpClient> Client(string user)
    {
        var client = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User(user));
        return client;
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestApi.JsonAsync(response);
    }

    private async Task<long> CountAsync(string sql, params (string, object)[] parameters)
    {
        await using var connection = db.OpenOwnerConnection();
        return await TestApi.CountAsync(connection, sql, parameters);
    }

    [Fact]
    public async Task Full_route_with_two_packages_updates_status_and_package_locations()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson());

        var a1 = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", Next()));
        Assert.Equal("Applied", a1.GetProperty("outcome").GetString());
        Assert.Equal("AtHub", a1.GetProperty("shipment").GetProperty("status").GetString());
        Assert.Equal(1, a1.GetProperty("consolidation").GetProperty("packagesAtHub").GetInt32());

        var a2 = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[1], "Arrival", Next()));
        Assert.Equal("PackageLocationOnly", a2.GetProperty("reasonCode").GetString());
        Assert.Equal(2, a2.GetProperty("consolidation").GetProperty("packagesAtHub").GetInt32());

        var d1 = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Departure", Next()));
        Assert.Equal("InTransit", d1.GetProperty("shipment").GetProperty("status").GetString());
        var d2 = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[1], "Departure", Next()));
        Assert.Equal("PackageLocationOnly", d2.GetProperty("reasonCode").GetString());

        Assert.Equal("AtHub", (await OkAsync(await TestApi.ScanAsync(ank, "ANK-02", labels[0], "Arrival", Next()))).GetProperty("shipment").GetProperty("status").GetString());
        await OkAsync(await TestApi.ScanAsync(ank, "ANK-02", labels[1], "Arrival", Next()));
        var ofd = await OkAsync(await TestApi.ScanAsync(ank, "ANK-02", labels[0], "OutForDelivery", Next()));
        Assert.Equal("OutForDelivery", ofd.GetProperty("shipment").GetProperty("status").GetString());

        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("OutForDelivery", detail.GetProperty("status").GetString());
        var statusEvents = detail.GetProperty("timeline").EnumerateArray()
            .Where(e => e.GetProperty("statusAfter").ValueKind != JsonValueKind.Null)
            .Select(e => e.GetProperty("statusAfter").GetString()).ToList();
        Assert.Equal(["Created", "AtHub", "InTransit", "AtHub", "OutForDelivery"], statusEvents);
        var packages = detail.GetProperty("packages").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, packages[0].GetProperty("currentHubCode").ValueKind);
        Assert.Equal("OutForDelivery", packages[0].GetProperty("lastScanType").GetString());
        Assert.Equal("ANK-02", packages[1].GetProperty("currentHubCode").GetString());
        // Dağıtımdaki gönderide operasyon personeli teslimat denemesi kaydedebilir (Dilim 5); iptal/iade yok.
        Assert.Equal(["RecordDeliveryAttempt"], detail.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Rejected_consolidation_leaves_package_and_shipment_untouched()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson());
        await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", Next()));
        var (beforeDetail, beforeETag) = await TestApi.GetDetailAsync(ops, id);

        using var rejected = await TestApi.ScanAsync(ist, "IST-01", labels[0], "Departure", Next());
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var problem = await TestApi.JsonAsync(rejected);
        Assert.Equal("packages-not-consolidated", problem.GetProperty("code").GetString());
        Assert.Equal("Rejected", problem.GetProperty("outcome").GetString());
        Assert.Equal("IST-01", problem.GetProperty("package").GetProperty("currentHubCode").GetString());

        var (afterDetail, afterETag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(beforeETag, afterETag);
        Assert.Equal(beforeDetail.GetProperty("packages").GetRawText(), afterDetail.GetProperty("packages").GetRawText());
        Assert.Equal("AtHub", afterDetail.GetProperty("status").GetString());
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id AND event_type = 'DepartedHub'", ("id", id)));
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM platform.audit_entries WHERE entity_id = @id AND action = 'HubScanRejected' AND outcome = 'Rejected' AND reason_code = 'packages-not-consolidated'",
            ("id", id.ToString())));
    }

    [Fact]
    public async Task Packages_split_across_hubs_block_departure()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        using var izm = await Client("hub.izm");
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson());
        await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", Next()));

        var other = await OkAsync(await TestApi.ScanAsync(izm, "IZM-01", labels[1], "Arrival", Next()));
        Assert.Equal("MissingDepartureScan", other.GetProperty("reasonCode").GetString());
        Assert.Equal("IZM-01", other.GetProperty("shipment").GetProperty("currentHubCode").GetString());

        using var departure = await TestApi.ScanAsync(izm, "IZM-01", labels[1], "Departure", Next());
        Assert.Equal(HttpStatusCode.Conflict, departure.StatusCode);
        Assert.Equal("packages-not-consolidated", await TestApi.ProblemCodeAsync(departure));

        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        var hubs = detail.GetProperty("packages").EnumerateArray().Select(p => p.GetProperty("currentHubCode").GetString()).ToList();
        Assert.Equal(["IST-01", "IZM-01"], hubs);
    }

    [Fact]
    public async Task Technical_and_business_duplicates_have_a_single_effect()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson());
        var at = Next();
        var scanId = "HT-" + Guid.NewGuid().ToString("N")[..12];

        await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", at, scanId));
        var technical = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", at, scanId));
        Assert.Equal("Duplicate", technical.GetProperty("outcome").GetString());
        Assert.Equal("technical-duplicate", technical.GetProperty("reasonCode").GetString());

        var business = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", at.AddSeconds(45)));
        Assert.Equal("Duplicate", business.GetProperty("outcome").GetString());
        Assert.Equal("business-duplicate", business.GetProperty("reasonCode").GetString());

        // Aynı clientScanId başka koli için kullanılırsa çakışma.
        using var conflict = await TestApi.ScanAsync(ist, "IST-01", labels[1], "Arrival", Next(), scanId);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("client-scan-id-conflict", await TestApi.ProblemCodeAsync(conflict));

        // Diğer koli farklı iş olayıdır (iş anahtarı paket kimliğini içerir).
        var second = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[1], "Arrival", at.AddSeconds(5)));
        Assert.Equal("Applied", second.GetProperty("outcome").GetString());

        Assert.Equal(2, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id AND event_type = 'ArrivedAtHub'", ("id", id)));
    }

    [Fact]
    public async Task Late_older_scans_are_recorded_as_stale_without_rewinding_state()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson(packageCount: 1));
        var arrival = Next();
        await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", arrival));
        await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Departure", Next()));

        // Çevrimdışı terminalden gelen, son taramadan eski varış (iş tekrarı penceresi olan 120 sn'nin dışında).
        var stale = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", arrival.AddMinutes(-5)));
        Assert.Equal("Stale", stale.GetProperty("outcome").GetString());
        Assert.Equal("InTransit", stale.GetProperty("shipment").GetProperty("status").GetString());

        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("InTransit", detail.GetProperty("status").GetString());
        var late = Assert.Single(detail.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("outcome").GetString() == "Stale");
        Assert.True(late.GetProperty("isLate").GetBoolean());
    }

    [Fact]
    public async Task Terminal_shipment_rejects_newer_and_stales_older_scans()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        var created = DateTimeOffset.UtcNow;
        var (id, labels, etag) = await TestApi.CreateAsync(ops, new ShipmentJson(packageCount: 1));
        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = "Test iptali" }, etag))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        using var newer = await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", Next());
        Assert.Equal(HttpStatusCode.Conflict, newer.StatusCode);
        Assert.Equal("transition-not-allowed", await TestApi.ProblemCodeAsync(newer));

        var older = await OkAsync(await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", created.AddSeconds(-30)));
        Assert.Equal("Stale", older.GetProperty("outcome").GetString());
        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("Cancelled", detail.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Authorization_unknown_label_and_invalid_time()
    {
        using var ops = await Client("ops.agent");
        using var izm = await Client("hub.izm");
        using var ist = await Client("hub.ist");
        var (_, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson());

        using (var notAssigned = await TestApi.ScanAsync(izm, "IST-01", labels[0], "Arrival", Next()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAssigned.StatusCode);
        }

        using (var opsScan = await TestApi.ScanAsync(ops, "IST-01", labels[0], "Arrival", Next()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, opsScan.StatusCode);
        }

        using (var unknown = await TestApi.ScanAsync(ist, "IST-01", "LS0000000000-01", "Arrival", Next()))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var future = await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", DateTimeOffset.UtcNow.AddMinutes(30)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
            Assert.Equal("invalid-occurred-at", await TestApi.ProblemCodeAsync(future));
        }

        using var invalid = await ist.PostAsync(new Uri("/api/hubs/IST-01/scans", UriKind.Relative),
            new StringContent("""{"clientScanId":"x","labelCode":"bad","scanType":"Teleport","occurredAt":"2026-10-10T10:00:00"}""", System.Text.Encoding.UTF8, "application/json"), TestApi.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await TestApi.JsonAsync(invalid)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("labelCode", out _));
        Assert.True(errors.TryGetProperty("scanType", out _));
        Assert.True(errors.TryGetProperty("occurredAt", out _));
    }

    [Fact]
    public async Task Scan_response_of_out_of_scope_shipment_reveals_no_personal_data()
    {
        using var ops = await Client("ops.agent");
        using var izm = await Client("hub.izm");
        var json = new ShipmentJson(origin: "IST-01", destination: "ANK-02", packageCount: 1);
        var (id, labels, _) = await TestApi.CreateAsync(ops, json);

        using (var before = await izm.GetAsync(new Uri($"/api/shipments/{id}", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        using var scan = await TestApi.ScanAsync(izm, "IZM-01", labels[0], "Arrival", Next());
        var body = await OkAsync(scan);
        Assert.Equal(ScanResponseFields.Order(StringComparer.Ordinal), body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var raw = body.GetRawText();
        foreach (var pii in json.PiiValues)
        {
            Assert.DoesNotContain(pii, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Concurrent_arrivals_of_two_packages_produce_one_status_change()
    {
        using var ops = await Client("ops.agent");
        using var ist1 = await Client("hub.ist");
        using var ist2 = await Client("hub.ist");
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson());
        var at = Next();

        using var barrier = new Barrier(2);
        var tasks = new[] { (ist1, labels[0]), (ist2, labels[1]) }.Select(x => Task.Run(async () =>
        {
            barrier.SignalAndWait(TestApi.Ct);
            return await TestApi.ScanAsync(x.Item1, "IST-01", x.Item2, "Arrival", at);
        })).ToArray();
        var responses = await Task.WhenAll(tasks);
        foreach (var r in responses)
        {
            Assert.Equal("Applied", (await OkAsync(r)).GetProperty("outcome").GetString());
            r.Dispose();
        }

        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id AND event_type = 'ArrivedAtHub' AND status_after = 'AtHub'", ("id", id)));
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id AND reason_code = 'PackageLocationOnly'", ("id", id)));
        Assert.Equal(2, await CountAsync(
            "SELECT count(*) FROM shipments.packages p JOIN operations.hubs h ON h.id = p.current_hub_id WHERE p.shipment_id = @id AND h.code = 'IST-01'", ("id", id)));
    }

    [Fact]
    public async Task Failure_after_mutation_rolls_back_package_and_shipment_changes()
    {
        var userId = await db.UserIdAsync("hub.ist2");
        await using (var owner = db.OpenOwnerConnection())
        {
            await using var create = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION platform.test_fail_scan_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.actor_id = '{userId}' THEN RAISE EXCEPTION 'forced scan audit failure (test)'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_test_fail_scan_audit BEFORE INSERT ON platform.audit_entries
                    FOR EACH ROW EXECUTE FUNCTION platform.test_fail_scan_audit();
                """, owner);
            await create.ExecuteNonQueryAsync(TestApi.Ct);
        }

        try
        {
            using var ops = await Client("ops.agent");
            using var ist2 = await Client("hub.ist2");
            var (id, labels, etag) = await TestApi.CreateAsync(ops, new ShipmentJson());
            using var response = await TestApi.ScanAsync(ist2, "IST-01", labels[0], "Arrival", Next());
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var (detail, after) = await TestApi.GetDetailAsync(ops, id);
            Assert.Equal(etag, after);
            Assert.Equal("Created", detail.GetProperty("status").GetString());
            Assert.All(detail.GetProperty("packages").EnumerateArray(), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("lastScanType").ValueKind));
            Assert.Equal(0, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id AND event_type <> 'Created'", ("id", id)));
        }
        finally
        {
            await using var owner = db.OpenOwnerConnection();
            await using var drop = new NpgsqlCommand(
                "DROP TRIGGER IF EXISTS trg_test_fail_scan_audit ON platform.audit_entries; DROP FUNCTION IF EXISTS platform.test_fail_scan_audit();", owner);
            await drop.ExecuteNonQueryAsync(TestApi.Ct);
        }
    }
}
