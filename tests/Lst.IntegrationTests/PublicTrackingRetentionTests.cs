using System.Net;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Lst.IntegrationTests;

/// <summary>Dilim 8: public takip (allowlist, aynı 404, başlıklar, log redaksiyonu), IP rate limit + güvenilir proxy, kod yenileme, saklama işi.</summary>
public sealed class PublicTrackingRetentionTests(LstDatabaseFixture db) : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private LstApiFactory Factory(TimeProvider? time = null, Action<IServiceCollection>? services = null, Dictionary<string, string?>? settings = null)
    {
        var factory = new LstApiFactory(db, time, services, settings: settings);
        _disposables.Add(factory);
        return factory;
    }

    private async Task<HttpClient> Login(LstApiFactory factory, string user)
    {
        var client = factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User(user));
        return client;
    }

    private static Task<HttpResponseMessage> PublicAsync(HttpClient client, string code, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/public/tracking/{code}");
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return client.SendAsync(request, TestApi.Ct);
    }

    private static void AssertPublicHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store");
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")), StringComparison.Ordinal);
    }

    private static async Task<string> PublicCodeAsync(HttpClient ops, Guid id) =>
        (await TestApi.GetDetailAsync(ops, id)).Body.GetProperty("publicTrackingCode").GetString()!;

    [Fact]
    public async Task Public_view_returns_only_allowlisted_fields_and_applied_events()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var hub = await Login(factory, "hub.ist");
        var json = new ShipmentJson(packageCount: 2);
        var (id, labels, _) = await TestApi.CreateAsync(ops, json);
        var start = DateTimeOffset.UtcNow;
        foreach (var label in labels)
        {
            using var arrival = await TestApi.ScanAsync(hub, "IST-01", label, "Arrival", start.AddSeconds(5));
            Assert.Equal(HttpStatusCode.OK, arrival.StatusCode);
        }

        await CarrierHelper.AssignSimexAsync(ops, id);
        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        var trackingNumber = detail.GetProperty("trackingNumber").GetString()!;
        var anonymous = factory.CreateHttpsClient();
        var pickup = await CarrierHelper.SendAsync(anonymous, trackingNumber, "PICKED_UP", start.AddMinutes(1), DateTimeOffset.UtcNow);
        Assert.Equal("Applied", await CarrierHelper.ProcessAsync(factory, db, pickup));
        // Mevcut durumdan eski carrier olayı Stale olur ve public zaman çizelgesinde yer almaz.
        var stale = await CarrierHelper.SendAsync(anonymous, trackingNumber, "IN_TRANSIT", start.AddSeconds(30), DateTimeOffset.UtcNow);
        Assert.Equal("Stale", await CarrierHelper.ProcessAsync(factory, db, stale));

        var code = await PublicCodeAsync(ops, id);
        using var response = await PublicAsync(anonymous, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPublicHeaders(response);
        var text = await response.Content.ReadAsStringAsync(TestApi.Ct);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(
            ["destinationCity", "isDelayed", "lastUpdatedAt", "packageCount", "promisedDeliveryDate", "status", "statusLabel", "timeline", "trackingNumberMasked"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(body.GetProperty("timeline").EnumerateArray(), item =>
            Assert.Equal(["city", "label", "occurredAt"], item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        Assert.Equal(("InTransit", "Yolda", 2, "Ankara"),
            (body.GetProperty("status").GetString(), body.GetProperty("statusLabel").GetString(), body.GetProperty("packageCount").GetInt32(),
             body.GetProperty("destinationCity").GetString()));
        Assert.Equal(trackingNumber[..4] + "******" + trackingNumber[^2..], body.GetProperty("trackingNumberMasked").GetString());
        var timeline = body.GetProperty("timeline").EnumerateArray().Select(t => (t.GetProperty("label").GetString(), t.GetProperty("city").GetString())).ToList();
        Assert.Equal(
            [("Gönderi kaydı oluşturuldu", null), ("Aktarma merkezine ulaştı", "İstanbul"), ("Taşıyıcı firmaya teslim edildi", "Ankara")],
            timeline);

        foreach (var secret in json.PiiValues.Concat([json.ExternalReference, "Çankaya", "06420", "34200", "IST-01", "İstanbul Avrupa Aktarma", "ANK-SX3",
                     trackingNumber, id.ToString(), "SIMEX", "Tekstil"]))
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        // Public kod log'a girmez (istek satırı dahil).
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_and_unknown_codes_get_byte_identical_404s_with_public_headers()
    {
        var factory = Factory();
        var client = factory.CreateHttpsClient();
        var bodies = new List<string>();
        foreach (var code in new[] { "not-a-code", new string('Z', 26), "LS4829105735", "0123456789ABCDEFGHJKMNPQRS", new string('I', 26) })
        {
            using var response = await PublicAsync(client, code);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            AssertPublicHeaders(response);
            bodies.Add(await response.Content.ReadAsStringAsync(TestApi.Ct));
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        Assert.Equal("not-found", JsonSerializer.Deserialize<JsonElement>(bodies[0]).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Twenty_first_request_per_ip_is_429_and_spoofed_forwarded_for_is_ignored()
    {
        var factory = Factory();
        var client = factory.CreateHttpsClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 21; i++)
        {
            // Güvenilir proxy tanımlı değil: her istekte farklı X-Forwarded-For olsa da tek istemci sayılır.
            using var response = await PublicAsync(client, new string('Z', 26), $"203.0.113.{i + 1}");
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                AssertPublicHeaders(response);
                Assert.NotNull(response.Headers.RetryAfter);
                Assert.Equal("rate-limited", await TestApi.ProblemCodeAsync(response));
            }
        }

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.NotFound, 20), HttpStatusCode.TooManyRequests], statuses);

        // İç API'ler public limitinden etkilenmez.
        using var csrf = await client.GetAsync(new Uri("/api/auth/csrf", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
    }

    [Fact]
    public async Task Forwarded_for_is_honored_only_from_a_configured_trusted_proxy()
    {
        var factory = Factory(
            services: s => s.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter("10.0.0.5")),
            settings: new() { ["ReverseProxy:TrustedProxies:0"] = "10.0.0.5", ["RateLimiting:Public:PerIpPerMinute"] = "3" });
        var client = factory.CreateHttpsClient();
        var first = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await PublicAsync(client, new string('Z', 26), "203.0.113.7");
            first.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests], first);
        using (var otherClient = await PublicAsync(client, new string('Z', 26), "203.0.113.8"))
        {
            Assert.Equal(HttpStatusCode.NotFound, otherClient.StatusCode);
        }

        // Aynı proxy ama güven listesi yoksa başlık yok sayılır: tüm istemciler proxy IP'si olarak tek bölümdedir.
        var untrusted = Factory(
            services: s => s.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter("10.0.0.5")),
            settings: new() { ["RateLimiting:Public:PerIpPerMinute"] = "3" });
        var untrustedClient = untrusted.CreateHttpsClient();
        var second = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await PublicAsync(untrustedClient, new string('Z', 26), $"203.0.113.{20 + i}");
            second.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, second[^1]);
    }

    [Fact]
    public async Task Ops_manager_rotates_the_code_old_code_stops_working_and_audit_has_no_code()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var manager = await Login(factory, "ops.manager");
        var hub = await Login(factory, "hub.ist");
        var (id, _, etag) = await TestApi.CreateAsync(ops, new ShipmentJson());
        var oldCode = await PublicCodeAsync(ops, id);
        var anonymous = factory.CreateHttpsClient();
        using (var before = await PublicAsync(anonymous, oldCode))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        var path = $"/api/shipments/{id}/public-tracking-code/rotate";
        using (var agent = await TestApi.PostCommandAsync(ops, path, new { }, etag))
        {
            Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
        }

        using (var missing = await TestApi.PostCommandAsync(manager, path, new { }, null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        }

        using (var stale = await TestApi.PostCommandAsync(manager, path, new { }, "\"1\""))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        }

        string newCode;
        using (var rotate = await TestApi.PostCommandAsync(manager, path, new { }, etag))
        {
            Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
            newCode = (await TestApi.JsonAsync(rotate)).GetProperty("publicTrackingCode").GetString()!;
            Assert.NotEqual(etag, rotate.Headers.ETag?.Tag);
        }

        Assert.NotEqual(oldCode, newCode);
        using (var old = await PublicAsync(anonymous, oldCode))
        {
            Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        }

        using (var current = await PublicAsync(anonymous, newCode))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        Assert.Equal(newCode, await PublicCodeAsync(ops, id));
        Assert.Equal(JsonValueKind.Null, (await TestApi.GetDetailAsync(hub, id)).Body.GetProperty("publicTrackingCode").ValueKind);
        await using var owner = db.OpenOwnerConnection();
        var changes = (string?)await TestApi.ScalarAsync(owner,
            "SELECT string_agg(changes::text, ',') FROM platform.audit_entries WHERE action = 'PublicTrackingCodeRotated' AND entity_id = @id", ("id", id.ToString()));
        Assert.NotNull(changes);
        Assert.DoesNotContain(oldCode, changes, StringComparison.Ordinal);
        Assert.DoesNotContain(newCode, changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retention_masks_old_terminal_pii_purges_raw_payloads_keeps_tombstones_and_is_idempotent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 5, 7, 0, 0, TimeSpan.Zero));
        var factory = Factory(time);
        var ops = await Login(factory, "ops.agent");
        var oldTerminalJson = new ShipmentJson();
        var (oldTerminal, _, oldEtag) = await TestApi.CreateAsync(ops, oldTerminalJson);
        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{oldTerminal}/cancel", new { reason = "Alıcı Ayşe Yılmaz istemedi" }, oldEtag))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var oldOpenJson = new ShipmentJson();
        var (oldOpen, _, _) = await TestApi.CreateAsync(ops, oldOpenJson);
        await CarrierHelper.AssignSimexAsync(ops, oldOpen);
        var oldOpenTn = (await TestApi.GetDetailAsync(ops, oldOpen)).Body.GetProperty("trackingNumber").GetString()!;
        var receipt = await CarrierHelper.SendAsync(factory.CreateHttpsClient(), oldOpenTn, "PICKED_UP", time.GetUtcNow().AddMinutes(1), time.GetUtcNow().AddMinutes(2));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("Applied", await CarrierHelper.ProcessAsync(factory, db, receipt));

        time.SetUtcNow(DateTimeOffset.UtcNow);
        ops = await Login(factory, "ops.agent");
        var recentJson = new ShipmentJson();
        var (recentTerminal, _, recentEtag) = await TestApi.CreateAsync(ops, recentJson);
        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{recentTerminal}/cancel", new { reason = "Yeni iptal" }, recentEtag))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var manager = await Login(factory, "ops.manager");
        using (var agentRun = await ops.PostAsync(new Uri("/api/operations/retention/run", UriKind.Relative), null, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, agentRun.StatusCode);
        }

        JsonElement first;
        using (var run = await manager.PostAsync(new Uri("/api/operations/retention/run", UriKind.Relative), null, TestApi.Ct))
        {
            first = await TestApi.JsonAsync(run);
            Assert.True(run.StatusCode == HttpStatusCode.OK, first + string.Join(" | ", factory.Logs.Entries.Where(e => e.StartsWith("Error", StringComparison.Ordinal)).Select(e => e[..Math.Min(e.Length, 1500)])));
        }

        Assert.True(first.GetProperty("affected").GetProperty("shipment-pii").GetInt32() >= 1);
        Assert.True(first.GetProperty("affected").GetProperty("carrier-raw-payload").GetInt32() >= 1);

        await using var owner = db.OpenOwnerConnection();
        // Eski terminal gönderi maskelendi; şehir/ilçe/posta kodu kaldı.
        Assert.Equal(2, await TestApi.CountAsync(owner,
            """
            SELECT count(*) FROM shipments.shipment_parties
             WHERE shipment_id = @id AND full_name = '[redacted]' AND company_name IS NULL AND phone_e164 IS NULL AND phone_last4 IS NULL
               AND email IS NULL AND address_line1 IS NULL AND address_line2 IS NULL AND city IS NOT NULL AND postal_code IS NOT NULL
            """, ("id", oldTerminal)));
        Assert.Equal(1, await TestApi.CountAsync(owner,
            "SELECT count(*) FROM shipments.shipments WHERE id = @id AND pii_redacted_at IS NOT NULL AND cancellation_reason = '[redacted]'", ("id", oldTerminal)));
        var (masked, _) = await TestApi.GetDetailAsync(manager, oldTerminal);
        Assert.True(masked.GetProperty("piiRedacted").GetBoolean());
        Assert.Equal("[redacted]", masked.GetProperty("recipient").GetProperty("fullName").GetString());
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("recipient").GetProperty("phone").ValueKind);
        var maskedText = masked.GetRawText();
        foreach (var pii in oldTerminalJson.PiiValues.Append("Ayşe Yılmaz"))
        {
            Assert.DoesNotContain(pii, maskedText, StringComparison.Ordinal);
        }

        // Terminal olmayan eski gönderi ve yeni terminal gönderi değişmedi.
        foreach (var (id, json) in new[] { (oldOpen, oldOpenJson), (recentTerminal, recentJson) })
        {
            Assert.Equal(0, await TestApi.CountAsync(owner, "SELECT count(*) FROM shipments.shipments WHERE id = @id AND pii_redacted_at IS NOT NULL", ("id", id)));
            Assert.Equal(json.RecipientName, (await TestApi.GetDetailAsync(manager, id)).Body.GetProperty("recipient").GetProperty("fullName").GetString());
        }

        // Ham payload temizlendi ama makbuz (tombstone) ve FK'li olay duruyor; hash'ler kaldı.
        Assert.Equal(1, await TestApi.CountAsync(owner,
            """
            SELECT count(*) FROM tracking.carrier_event_receipts
             WHERE id = @id AND raw_body IS NULL AND payload IS NULL AND payload_purged_at IS NOT NULL
               AND octet_length(raw_sha256) = 32 AND octet_length(payload_sha256) = 32 AND outcome = 'Applied'
            """, ("id", receipt)));
        Assert.Equal(1, await TestApi.CountAsync(owner, "SELECT count(*) FROM shipments.tracking_events WHERE carrier_event_receipt_id = @id", ("id", receipt)));
        // Süresi dolmuş idempotency kaydı silindi.
        Assert.Equal(0, await TestApi.CountAsync(owner, "SELECT count(*) FROM platform.idempotency_records WHERE resource_id = @id", ("id", oldTerminal)));
        Assert.Equal(1, await TestApi.CountAsync(owner, "SELECT count(*) FROM platform.idempotency_records WHERE resource_id = @id", ("id", recentTerminal)));

        var purgedAt = await TestApi.ScalarAsync(owner, "SELECT payload_purged_at FROM tracking.carrier_event_receipts WHERE id = @id", ("id", receipt));
        var redactedAt = await TestApi.ScalarAsync(owner, "SELECT pii_redacted_at FROM shipments.shipments WHERE id = @id", ("id", oldTerminal));

        // İkinci çalıştırma bu verilerde değişiklik yapmaz.
        using (var again = await manager.PostAsync(new Uri("/api/operations/retention/run", UriKind.Relative), null, TestApi.Ct))
        {
            var second = await TestApi.JsonAsync(again);
            Assert.Equal(0, second.GetProperty("affected").GetProperty("shipment-pii").GetInt32());
        }

        Assert.Equal(purgedAt, await TestApi.ScalarAsync(owner, "SELECT payload_purged_at FROM tracking.carrier_event_receipts WHERE id = @id", ("id", receipt)));
        Assert.Equal(redactedAt, await TestApi.ScalarAsync(owner, "SELECT pii_redacted_at FROM shipments.shipments WHERE id = @id", ("id", oldTerminal)));

        Assert.Equal(2, await TestApi.CountAsync(owner,
            "SELECT count(*) FROM platform.audit_entries WHERE action = 'RetentionRun' AND actor_id = @actor",
            ("actor", (await db.UserIdAsync("ops.manager")).ToString())));

        // Maskelenmiş gönderinin public görünümü şehir düzeyinde çalışmaya devam eder.
        using var publicView = await PublicAsync(factory.CreateHttpsClient(), (string)(await TestApi.ScalarAsync(owner,
            "SELECT public_tracking_code FROM shipments.shipments WHERE id = @id", ("id", oldTerminal)))!);
        Assert.Equal(HttpStatusCode.OK, publicView.StatusCode);
        Assert.Equal("Ankara", (await TestApi.JsonAsync(publicView)).GetProperty("destinationCity").GetString());
    }

    private sealed class FixedRemoteIpStartupFilter(string ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
