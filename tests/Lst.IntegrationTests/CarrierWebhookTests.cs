using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Tracking.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Lst.IntegrationTests;

/// <summary>Dilim 6: carrier webhook, kalıcı inbox, lease/fencing, poison/DLQ, DB kesintisi ve simülatör (gerçek PostgreSQL).</summary>
public sealed class CarrierWebhookTests(LstDatabaseFixture db) : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly List<IDisposable> _disposables = [];
    private DateTimeOffset _clock = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private LstApiFactory Factory(
        TimeProvider? time = null, Action<IServiceCollection>? services = null, string environment = "Testing", Dictionary<string, string?>? settings = null)
    {
        var factory = new LstApiFactory(db, time, services, environment, settings);
        _disposables.Add(factory);
        return factory;
    }

    private DateTimeOffset Next() => _clock = _clock.AddSeconds(10);

    private async Task<HttpClient> Login(LstApiFactory factory, string user)
    {
        var client = factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User(user));
        return client;
    }

    private static Dictionary<string, object?> Event(string trackingNumber, string type, DateTimeOffset occurredAt, string? eventId = null, long? sequence = null,
        string facility = "IST-SX1", string city = "İstanbul", object? attempt = null) => new()
    {
        ["eventId"] = eventId ?? "it-" + Guid.NewGuid().ToString("N"),
        ["carrierCode"] = TestCarrier.Code,
        ["trackingNumber"] = trackingNumber,
        ["eventType"] = type,
        ["occurredAt"] = occurredAt.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        ["sequence"] = sequence,
        ["location"] = new { facilityCode = facility, city },
        ["attempt"] = attempt,
    };

    private static Task<HttpResponseMessage> PostWebhookAsync(
        HttpClient client, byte[] body, DateTimeOffset signedAt, string? secret = null, string route = TestCarrier.Code, string contentType = "application/json")
    {
        var t = signedAt.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/carrier-webhooks/{route}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Add(CarrierWebhookParser.SignatureHeader, $"t={t},v1={CarrierWebhookParser.Sign(secret ?? TestCarrier.Secret, t, body)}");
        return client.SendAsync(request, TestApi.Ct);
    }

    private static Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, object payload, DateTimeOffset signedAt, string? secret = null, string route = TestCarrier.Code) =>
        PostWebhookAsync(client, JsonSerializer.SerializeToUtf8Bytes(payload), signedAt, secret, route);

    private static async Task<Guid> AcceptedAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        var body = await TestApi.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body}");
        Assert.Equal("Received", body.GetProperty("status").GetString());
        return body.GetProperty("receiptId").GetGuid();
    }

    private static async Task AssignSimexAsync(HttpClient ops, Guid shipmentId)
    {
        var (_, etag) = await TestApi.GetDetailAsync(ops, shipmentId);
        using var response = await TestApi.PostCommandAsync(ops, $"/api/shipments/{shipmentId}/carrier", new { carrierCode = TestCarrier.Code }, etag);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(TestApi.Ct)}");
    }

    private static async Task<(Guid Id, string TrackingNumber)> CarrierShipmentAsync(HttpClient ops, bool assign = true)
    {
        var (id, _, _) = await TestApi.CreateAsync(ops, new ShipmentJson());
        if (assign)
        {
            await AssignSimexAsync(ops, id);
        }

        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        return (id, detail.GetProperty("trackingNumber").GetString()!);
    }

    private static async Task<List<ProcessResult>> ProcessAsync(LstApiFactory factory, params Guid[] receiptIds)
    {
        var processor = factory.Services.GetRequiredService<CarrierInboxProcessor>();
        var results = new List<ProcessResult>();
        foreach (var claimed in await processor.ClaimAsync(50, receiptIds, TestApi.Ct))
        {
            results.Add(await processor.ProcessAsync(claimed, TestApi.Ct));
        }

        // Teşhis: beklenmeyen kalıcı hatada makbuzdaki hata tipi assertion mesajında görünür.
        Assert.True(!results.Contains(ProcessResult.RetryScheduled) || factory.Services.GetService<PoisonMarker>() is not null,
            "Processing failed: " + string.Join(";", factory.Logs.Entries.Where(e => e.Contains("processing failed", StringComparison.Ordinal))));

        return results;
    }

    private async Task<(string Status, string? Outcome, string? Reason, short Attempts, Guid? ShipmentId, string? LastError)> ReceiptAsync(Guid receiptId)
    {
        await using var connection = db.OpenOwnerConnection();
        await using var command = new NpgsqlCommand(
            "SELECT processing_status, outcome, outcome_reason, attempts, shipment_id, last_error FROM tracking.carrier_event_receipts WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", receiptId);
        await using var reader = await command.ExecuteReaderAsync(TestApi.Ct);
        Assert.True(await reader.ReadAsync(TestApi.Ct));
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt16(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private async Task<long> CountAsync(string sql, params (string, object)[] parameters)
    {
        await using var connection = db.OpenOwnerConnection();
        return await TestApi.CountAsync(connection, sql, parameters);
    }

    private static async Task<JsonElement[]> ReceiptsAsync(HttpClient ops, Guid shipmentId)
    {
        using var response = await ops.GetAsync(new Uri($"/api/shipments/{shipmentId}/carrier-receipts", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await TestApi.JsonAsync(response)).EnumerateArray()];
    }

    [Fact]
    public async Task Signed_event_is_acked_only_after_persistence_and_applied_asynchronously()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var (id, tn) = await CarrierShipmentAsync(ops);
        var carrier = factory.CreateHttpsClient();

        var receiptId = await AcceptedAsync(PostWebhookAsync(carrier, Event(tn, "PICKED_UP", Next(), sequence: 1), DateTimeOffset.UtcNow));

        // Ack kalıcılıktan sonra: makbuz satırı ve ham byte'lar var; iş sonucu henüz yok ve "Applied" olarak raporlanmaz.
        var pending = await ReceiptAsync(receiptId);
        Assert.Equal(("Received", (string?)null, (short)0), (pending.Status, pending.Outcome, pending.Attempts));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tracking.carrier_event_receipts WHERE id = @id AND raw_body IS NOT NULL AND octet_length(raw_sha256) = 32", ("id", receiptId)));
        var listed = Assert.Single(await ReceiptsAsync(ops, id));
        Assert.Equal("Received", listed.GetProperty("processingStatus").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("outcome").ValueKind);
        var (before, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("Created", before.GetProperty("status").GetString());

        Assert.Equal([ProcessResult.Processed], await ProcessAsync(factory, receiptId));
        var processed = await ReceiptAsync(receiptId);
        Assert.Equal(("Processed", "Applied", id), (processed.Status, processed.Outcome, processed.ShipmentId!.Value));
        var (after, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("InTransit", after.GetProperty("status").GetString());
        Assert.Equal("SIMEX", after.GetProperty("carrier").GetProperty("code").GetString());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("carrier").GetProperty("handedOverAt").ValueKind);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE carrier_event_receipt_id = @id", ("id", receiptId)));
        // Carrier'a teslim edildikten sonra iptal artık mümkün değildir.
        Assert.DoesNotContain("Cancel", after.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));

        // İşlenmiş makbuz yeniden claim edilmez.
        Assert.Empty(await ProcessAsync(factory, receiptId));
    }

    [Fact]
    public async Task Same_event_id_and_content_is_a_technical_duplicate_even_if_bytes_differ()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var (_, tn) = await CarrierShipmentAsync(ops);
        var carrier = factory.CreateHttpsClient();
        var payload = Event(tn, "PICKED_UP", Next(), sequence: 1);
        var receiptId = await AcceptedAsync(PostWebhookAsync(carrier, payload, DateTimeOffset.UtcNow));

        using (var again = await PostWebhookAsync(carrier, payload, DateTimeOffset.UtcNow))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var body = await TestApi.JsonAsync(again);
            Assert.Equal(("Duplicate", receiptId), (body.GetProperty("status").GetString(), body.GetProperty("receiptId").GetGuid()));
        }

        // Aynı içerik, farklı biçim (girintili JSON + bilinmeyen alan): ham byte farklı, kanonik içerik aynı.
        var reformatted = new Dictionary<string, object?>(payload) { ["driverNote"] = "kapıda" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reformatted, Indented);
        using (var formatted = await PostWebhookAsync(carrier, bytes, DateTimeOffset.UtcNow))
        {
            Assert.Equal(HttpStatusCode.OK, formatted.StatusCode);
        }

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tracking.carrier_event_receipts WHERE external_event_id = @e", ("e", (string)payload["eventId"]!)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tracking.carrier_event_conflicts WHERE receipt_id = @id", ("id", receiptId)));
        // Bilinmeyen alan saklanmaz.
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tracking.carrier_event_receipts WHERE id = @id AND payload::text LIKE '%driverNote%'", ("id", receiptId)));
    }

    [Fact]
    public async Task Same_event_id_with_different_content_keeps_original_and_records_conflict_evidence()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var (id, tn) = await CarrierShipmentAsync(ops);
        var carrier = factory.CreateHttpsClient();
        var original = Event(tn, "PICKED_UP", Next(), sequence: 1);
        var receiptId = await AcceptedAsync(PostWebhookAsync(carrier, original, DateTimeOffset.UtcNow));
        await using var owner = db.OpenOwnerConnection();
        var originalHash = (byte[])(await TestApi.ScalarAsync(owner, "SELECT payload_sha256 || raw_sha256 FROM tracking.carrier_event_receipts WHERE id = @id", ("id", receiptId)))!;

        var changed = new Dictionary<string, object?>(original) { ["location"] = new { facilityCode = "BRS-SX9", city = "Bursa" } };
        using (var conflict = await PostWebhookAsync(carrier, changed, DateTimeOffset.UtcNow))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("carrier-event-id-conflict", await TestApi.ProblemCodeAsync(conflict));
            Assert.Equal(receiptId, (await TestApi.JsonAsync(conflict)).GetProperty("receiptId").GetGuid());
        }

        // İlk makbuz değişmedi; ikinci gövde ayrı kanıt satırında ham byte'larıyla duruyor.
        Assert.Equal(originalHash, (byte[])(await TestApi.ScalarAsync(owner, "SELECT payload_sha256 || raw_sha256 FROM tracking.carrier_event_receipts WHERE id = @id", ("id", receiptId)))!);
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM tracking.carrier_event_conflicts WHERE receipt_id = @id AND raw_body IS NOT NULL AND payload_sha256 <> (SELECT payload_sha256 FROM tracking.carrier_event_receipts WHERE id = @id)",
            ("id", receiptId)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM platform.audit_entries WHERE action = 'CarrierEventConflictDetected' AND entity_id = @id", ("id", receiptId.ToString())));

        // Operatöre görünür: gönderide açık çakışma istisnası, makbuz listesinde çakışma sayısı, dikkat listesinde kayıt.
        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Contains(detail.GetProperty("openExceptions").EnumerateArray(), e => e.GetProperty("type").GetString() == "CarrierEventConflict");
        Assert.Equal(1, Assert.Single(await ReceiptsAsync(ops, id)).GetProperty("conflictCount").GetInt32());
        using var attention = await ops.GetAsync(new Uri("/api/carrier-receipts?status=Conflicted", UriKind.Relative), TestApi.Ct);
        Assert.Contains((await TestApi.JsonAsync(attention)).EnumerateArray(), r => r.GetProperty("id").GetGuid() == receiptId);

        // Orijinal olay yine normal işlenir.
        Assert.Equal([ProcessResult.Processed], await ProcessAsync(factory, receiptId));
        Assert.Equal("Applied", (await ReceiptAsync(receiptId)).Outcome);
    }

    [Fact]
    public async Task Parallel_redelivery_of_one_event_creates_exactly_one_receipt()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var (_, tn) = await CarrierShipmentAsync(ops);
        var payload = JsonSerializer.SerializeToUtf8Bytes(Event(tn, "PICKED_UP", Next()));
        var clients = Enumerable.Range(0, 8).Select(_ => factory.CreateHttpsClient()).ToArray();

        var responses = await Task.WhenAll(clients.Select(c => PostWebhookAsync(c, payload, DateTimeOffset.UtcNow)));
        var statuses = responses.Select(r => r.StatusCode).ToArray();
        var receiptIds = new HashSet<Guid>();
        foreach (var response in responses)
        {
            receiptIds.Add((await TestApi.JsonAsync(response)).GetProperty("receiptId").GetGuid());
            response.Dispose();
        }

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal(7, statuses.Count(s => s == HttpStatusCode.OK));
        var receiptId = Assert.Single(receiptIds);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM tracking.carrier_event_receipts WHERE id = @id", ("id", receiptId)));
    }

    [Fact]
    public async Task Failure_after_apply_but_before_commit_leaves_no_partial_state_and_retries_once()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var failing = Factory(time, services =>
        {
            services.AddScoped<ShipmentCarrierEvents>();
            services.Replace(ServiceDescriptor.Scoped<IShipmentCarrierEvents, FailAfterApplyCarrierEvents>());
            services.AddSingleton<PoisonMarker>();
        });
        var ops = await Login(failing, "ops.agent");
        var (id, tn) = await CarrierShipmentAsync(ops);
        var receiptId = await AcceptedAsync(PostWebhookAsync(failing.CreateHttpsClient(), Event(tn, "PICKED_UP", time.GetUtcNow().AddSeconds(5)), time.GetUtcNow()));

        Assert.Equal([ProcessResult.RetryScheduled], await ProcessAsync(failing, receiptId));
        // Uygulama (olay, durum, audit) receipt güncellemesiyle aynı transaction'daydı: hiçbiri kalmadı.
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE carrier_event_receipt_id = @id", ("id", receiptId)));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM platform.audit_entries WHERE action = 'CarrierEventRecorded' AND entity_id = @id", ("id", id.ToString())));
        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("Created", detail.GetProperty("status").GetString());

        time.Advance(TimeSpan.FromSeconds(25));
        var healthy = Factory(time);
        Assert.Equal([ProcessResult.Processed], await ProcessAsync(healthy, receiptId));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE carrier_event_receipt_id = @id", ("id", receiptId)));
        var final = await ReceiptAsync(receiptId);
        Assert.Equal(("Processed", "Applied", (short)1), (final.Status, final.Outcome, final.Attempts));
    }

    [Fact]
    public async Task Signature_timestamp_and_provider_binding_failures_are_401_without_storing_anything()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var (_, tn) = await CarrierShipmentAsync(ops);
        var carrier = factory.CreateHttpsClient();
        var eventId = "it-sig-" + Guid.NewGuid().ToString("N");
        var payload = Event(tn, "PICKED_UP", Next(), eventId);

        var attempts = new List<Task<HttpResponseMessage>>
        {
            PostWebhookAsync(carrier, payload, DateTimeOffset.UtcNow, secret: "wrong-secret-wrong-secret-wrong-secret!!"),
            PostWebhookAsync(carrier, payload, DateTimeOffset.UtcNow.AddMinutes(-6)),
            PostWebhookAsync(carrier, payload, DateTimeOffset.UtcNow.AddMinutes(6)),
            PostWebhookAsync(carrier, payload, DateTimeOffset.UtcNow, route: "NOPE"),
            PostWebhookAsync(carrier, new Dictionary<string, object?>(payload) { ["carrierCode"] = "OTHER" }, DateTimeOffset.UtcNow),
        };
        var unsigned = new HttpRequestMessage(HttpMethod.Post, "/api/carrier-webhooks/SIMEX") { Content = System.Net.Http.Json.JsonContent.Create(payload) };
        attempts.Add(carrier.SendAsync(unsigned, TestApi.Ct));

        foreach (var attempt in attempts)
        {
            using var response = await attempt;
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var problem = await TestApi.JsonAsync(response);
            Assert.Equal("invalid-signature", problem.GetProperty("code").GetString());
            Assert.False(problem.TryGetProperty("detail", out _));
        }

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM tracking.carrier_event_receipts WHERE external_event_id = @e", ("e", eventId)));
    }

    [Fact]
    public async Task Size_type_depth_duplicate_field_and_schema_limits_are_enforced_without_echo()
    {
        var factory = Factory();
        var carrier = factory.CreateHttpsClient();
        var now = DateTimeOffset.UtcNow;

        using (var tooLarge = await PostWebhookAsync(carrier, new byte[(64 * 1024) + 1], now))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        }

        using (var wrongType = await PostWebhookAsync(carrier, "{}"u8.ToArray(), now, contentType: "text/plain"))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        }

        var deep = Encoding.UTF8.GetBytes("{\"eventId\":\"x\",\"extra\":" + new string('[', 40) + new string(']', 40) + "}");
        using (var tooDeep = await PostWebhookAsync(carrier, deep, now))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
        }

        using (var duplicate = await PostWebhookAsync(carrier, "{\"eventId\":\"a\",\"eventId\":\"b\"}"u8.ToArray(), now))
        {
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        }

        var secretValue = "PII-ECHO-" + Guid.NewGuid().ToString("N");
        var invalid = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId = "it-" + Guid.NewGuid().ToString("N"),
            carrierCode = "SIMEX",
            trackingNumber = secretValue,
            eventType = "TELEPORTED",
            occurredAt = "2026-10-10 12:00",
        });
        using var schema = await PostWebhookAsync(carrier, invalid, now);
        Assert.Equal(HttpStatusCode.BadRequest, schema.StatusCode);
        var text = await schema.Content.ReadAsStringAsync(TestApi.Ct);
        Assert.DoesNotContain(secretValue, text, StringComparison.Ordinal);
        var errors = (await TestApi.JsonAsync(schema)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("trackingNumber", out _));
        Assert.True(errors.TryGetProperty("eventType", out _));
        Assert.True(errors.TryGetProperty("occurredAt", out _));
    }

    [Fact]
    public async Task Per_carrier_rate_limit_returns_429_with_retry_after()
    {
        var factory = Factory(settings: new() { ["Carriers:RateLimitPermitLimit"] = "3", ["Carriers:RateLimitWindowSeconds"] = "120" });
        var carrier = factory.CreateHttpsClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            // Geçersiz imzalı istekler de limite sayılır (imza doğrulamasından önce uygulanır).
            using var response = await PostWebhookAsync(carrier, "{}"u8.ToArray(), DateTimeOffset.UtcNow, secret: "x-not-the-secret-x-not-the-secret-x-not");
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.Equal("rate-limited", await TestApi.ProblemCodeAsync(response));
                Assert.True(response.Headers.RetryAfter is not null);
            }
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);

        // Bölüm carrier başınadır: başka bir carrier kodu kendi limitine sahiptir.
        using var other = await PostWebhookAsync(carrier, "{}"u8.ToArray(), DateTimeOffset.UtcNow, route: "OTHERX");
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task Business_outcomes_stale_review_rejected_and_duplicate_are_recorded_on_the_receipt()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var carrier = factory.CreateHttpsClient();
        var (id, tn) = await CarrierShipmentAsync(ops);

        var pickedUp = await AcceptedAsync(PostWebhookAsync(carrier, Event(tn, "PICKED_UP", Next()), DateTimeOffset.UtcNow));
        var outForDelivery = await AcceptedAsync(PostWebhookAsync(carrier, Event(tn, "OUT_FOR_DELIVERY", Next(), facility: "ANK-SX3", city: "Ankara"), DateTimeOffset.UtcNow));
        var businessDuplicate = await AcceptedAsync(PostWebhookAsync(carrier, Event(tn, "OUT_FOR_DELIVERY", _clock.AddSeconds(30), facility: "ANK-SX3", city: "Ankara"), DateTimeOffset.UtcNow));
        var delivered = await AcceptedAsync(PostWebhookAsync(carrier, Event(tn, "DELIVERED", _clock.AddMinutes(3), facility: "ANK-SX3", city: "Ankara"), DateTimeOffset.UtcNow));
        var outOfOrder = await AcceptedAsync(PostWebhookAsync(carrier, Event(tn, "IN_TRANSIT", _clock.AddSeconds(5)), DateTimeOffset.UtcNow));
        var contradicts = await AcceptedAsync(PostWebhookAsync(carrier,
            Event(tn, "DELIVERY_FAILED", _clock.AddMinutes(5), facility: "ANK-SX3", city: "Ankara", attempt: new { number = 1, reason = "RECIPIENT_ABSENT" }), DateTimeOffset.UtcNow));

        foreach (var receipt in new[] { pickedUp, outForDelivery, businessDuplicate, delivered, outOfOrder, contradicts })
        {
            Assert.Equal([ProcessResult.Processed], await ProcessAsync(factory, receipt));
        }

        Assert.Equal("Applied", (await ReceiptAsync(pickedUp)).Outcome);
        Assert.Equal("Applied", (await ReceiptAsync(outForDelivery)).Outcome);
        Assert.Equal(("Duplicate", "business-duplicate"), ((await ReceiptAsync(businessDuplicate)).Outcome, (await ReceiptAsync(businessDuplicate)).Reason));
        Assert.Equal("Applied", (await ReceiptAsync(delivered)).Outcome);
        Assert.Equal("Stale", (await ReceiptAsync(outOfOrder)).Outcome);
        Assert.Equal(("PendingReview", "contradicts-terminal"), ((await ReceiptAsync(contradicts)).Outcome, (await ReceiptAsync(contradicts)).Reason));

        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("Delivered", detail.GetProperty("status").GetString());
        Assert.Contains(detail.GetProperty("openExceptions").EnumerateArray(), e => e.GetProperty("type").GetString() == "CarrierEventPendingReview");
        using var review = await ops.GetAsync(new Uri("/api/carrier-receipts?status=PendingReview", UriKind.Relative), TestApi.Ct);
        Assert.Contains((await TestApi.JsonAsync(review)).EnumerateArray(), r => r.GetProperty("id").GetGuid() == contradicts);

        // Atanmamış carrier ve bilinmeyen gönderi: Rejected, makbuz gönderiye bağlanmaz.
        var (unassignedId, unassignedTn) = await CarrierShipmentAsync(ops, assign: false);
        var notAssigned = await AcceptedAsync(PostWebhookAsync(carrier, Event(unassignedTn, "PICKED_UP", Next()), DateTimeOffset.UtcNow));
        var unknown = await AcceptedAsync(PostWebhookAsync(carrier, Event(Lst.Modules.Shipments.Domain.TrackingNumbers.FromPayload(999_999_998), "PICKED_UP", Next()), DateTimeOffset.UtcNow));
        await ProcessAsync(factory, notAssigned, unknown);
        var rejected = await ReceiptAsync(notAssigned);
        Assert.Equal(("Rejected", "carrier-not-assigned", (Guid?)null), (rejected.Outcome, rejected.Reason, rejected.ShipmentId));
        Assert.Equal(("Rejected", "unknown-shipment"), ((await ReceiptAsync(unknown)).Outcome, (await ReceiptAsync(unknown)).Reason));
        var (unassigned, _) = await TestApi.GetDetailAsync(ops, unassignedId);
        Assert.Equal("Created", unassigned.GetProperty("status").GetString());
        Assert.Empty(await ReceiptsAsync(ops, unassignedId));
    }

    [Fact]
    public async Task Expired_lease_owner_cannot_commit_after_takeover()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = Factory(time);
        var ops = await Login(factory, "ops.agent");
        var (_, tn) = await CarrierShipmentAsync(ops);
        var receiptId = await AcceptedAsync(PostWebhookAsync(factory.CreateHttpsClient(), Event(tn, "PICKED_UP", time.GetUtcNow().AddSeconds(5)), time.GetUtcNow()));
        var processor = factory.Services.GetRequiredService<CarrierInboxProcessor>();

        // Aynı anda iki claim: yalnız biri kazanır.
        var claims = await Task.WhenAll(processor.ClaimAsync(5, [receiptId], TestApi.Ct), processor.ClaimAsync(5, [receiptId], TestApi.Ct));
        var stale = Assert.Single(claims.SelectMany(c => c));

        time.Advance(TimeSpan.FromSeconds(31));
        var current = Assert.Single(await processor.ClaimAsync(5, [receiptId], TestApi.Ct));
        Assert.NotEqual(stale.LockToken, current.LockToken);

        Assert.Equal(ProcessResult.LostLease, await processor.ProcessAsync(stale, TestApi.Ct));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE carrier_event_receipt_id = @id", ("id", receiptId)));
        Assert.Equal(ProcessResult.Processed, await processor.ProcessAsync(current, TestApi.Ct));
        Assert.Equal(ProcessResult.LostLease, await processor.ProcessAsync(stale, TestApi.Ct));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE carrier_event_receipt_id = @id", ("id", receiptId)));
        Assert.Equal("Applied", (await ReceiptAsync(receiptId)).Outcome);
    }

    [Fact]
    public async Task Poison_event_backs_off_then_dead_letters_with_audit_and_operator_visibility()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = Factory(time, services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IShipmentCarrierEvents, PoisonCarrierEvents>());
                services.AddSingleton<PoisonMarker>();
            },
            settings: new() { ["Workers:CarrierInbox:MaxAttempts"] = "2" });
        var ops = await Login(factory, "ops.agent");
        var (id, tn) = await CarrierShipmentAsync(ops);
        var receiptId = await AcceptedAsync(PostWebhookAsync(factory.CreateHttpsClient(), Event(tn, "PICKED_UP", time.GetUtcNow().AddSeconds(5)), time.GetUtcNow()));

        Assert.Equal([ProcessResult.RetryScheduled], await ProcessAsync(factory, receiptId));
        var first = await ReceiptAsync(receiptId);
        Assert.Equal(("Received", (short)1), (first.Status, first.Attempts));
        Assert.Equal(typeof(InvalidOperationException).FullName, first.LastError);
        // Backoff: hemen yeniden claim edilmez.
        Assert.Empty(await ProcessAsync(factory, receiptId));

        time.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal([ProcessResult.DeadLettered], await ProcessAsync(factory, receiptId));
        var dead = await ReceiptAsync(receiptId);
        Assert.Equal(("DeadLettered", (short)2, (string?)null), (dead.Status, dead.Attempts, dead.Outcome));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM platform.audit_entries WHERE action = 'CarrierEventDeadLettered' AND entity_id = @id", ("id", receiptId.ToString())));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE carrier_event_receipt_id = @id", ("id", receiptId)));

        time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(await ProcessAsync(factory, receiptId));
        using var attention = await ops.GetAsync(new Uri("/api/carrier-receipts?status=DeadLettered", UriKind.Relative), TestApi.Ct);
        Assert.Contains((await TestApi.JsonAsync(attention)).EnumerateArray(), r => r.GetProperty("id").GetGuid() == receiptId);
        Assert.Equal("DeadLettered", Assert.Single(await ReceiptsAsync(ops, id)).GetProperty("processingStatus").GetString());
    }

    [Fact]
    public async Task Database_outage_neither_consumes_attempts_nor_dead_letters_and_recovers()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var outage = Factory(time, services => services.Replace(ServiceDescriptor.Scoped<IShipmentCarrierEvents, UnreachableDatabaseCarrierEvents>()),
            settings: new() { ["Workers:CarrierInbox:MaxAttempts"] = "2" });
        var ops = await Login(outage, "ops.agent");
        var (id, tn) = await CarrierShipmentAsync(ops);
        var receiptId = await AcceptedAsync(PostWebhookAsync(outage.CreateHttpsClient(), Event(tn, "PICKED_UP", time.GetUtcNow().AddSeconds(5)), time.GetUtcNow()));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal([ProcessResult.TransientFailure], await ProcessAsync(outage, receiptId));
            var state = await ReceiptAsync(receiptId);
            Assert.Equal(("Received", (short)0), (state.Status, state.Attempts));
            Assert.StartsWith("transient:", state.LastError, StringComparison.Ordinal);
            time.Advance(TimeSpan.FromSeconds(6));
        }

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM platform.audit_entries WHERE action = 'CarrierEventDeadLettered' AND entity_id = @id", ("id", receiptId.ToString())));

        var healthy = Factory(time);
        Assert.Equal([ProcessResult.Processed], await ProcessAsync(healthy, receiptId));
        Assert.Equal("Applied", (await ReceiptAsync(receiptId)).Outcome);
        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("InTransit", detail.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Webhook_returns_503_without_ack_when_storage_is_unreachable()
    {
        var factory = Factory(settings: new() { ["ConnectionStrings:Lst"] = "Host=127.0.0.1;Port=1;Database=lst_none;Username=none;Password=none;Timeout=2;Gss Encryption Mode=Disable" });
        using var response = await PostWebhookAsync(factory.CreateHttpsClient(),
            Event(Lst.Modules.Shipments.Domain.TrackingNumbers.FromPayload(123_456_789), "PICKED_UP", Next()), DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("service-unavailable", await TestApi.ProblemCodeAsync(response));
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Carrier_assignment_and_receipt_endpoints_enforce_roles_and_preconditions()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var hub = await Login(factory, "hub.ist");
        var (id, _, etag) = await TestApi.CreateAsync(ops, new ShipmentJson());

        using (var noIfMatch = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/carrier", new { carrierCode = "SIMEX" }, null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, noIfMatch.StatusCode);
        }

        using (var unknown = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/carrier", new { carrierCode = "NOPE" }, etag))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            Assert.Equal("unknown-carrier", await TestApi.ProblemCodeAsync(unknown));
        }

        using (var hubAssign = await TestApi.PostCommandAsync(hub, $"/api/shipments/{id}/carrier", new { carrierCode = "SIMEX" }, etag))
        {
            Assert.Equal(HttpStatusCode.Forbidden, hubAssign.StatusCode);
        }

        using (var hubRead = await hub.GetAsync(new Uri($"/api/shipments/{id}/carrier-receipts", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, hubRead.StatusCode);
        }

        using (var anonymous = await factory.CreateHttpsClient().GetAsync(new Uri("/api/carrier-receipts", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var badFilter = await ops.GetAsync(new Uri("/api/carrier-receipts?status=Everything", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
        }

        await AssignSimexAsync(ops, id);
        var (detail, current) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("SIMEX", detail.GetProperty("carrier").GetProperty("code").GetString());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM platform.audit_entries WHERE action = 'CarrierAssigned' AND entity_id = @id", ("id", id.ToString())));

        using var stale = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/carrier", new { carrierCode = "SIMEX" }, etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.NotEqual(etag, current);
    }

    [Fact]
    public async Task Runtime_role_cannot_delete_receipts_or_modify_carriers()
    {
        await using var runtime = db.OpenRuntimeConnection();
        foreach (var sql in new[]
        {
            "DELETE FROM tracking.carrier_event_receipts WHERE false",
            "DELETE FROM tracking.carrier_event_conflicts WHERE false",
            "UPDATE tracking.carriers SET is_active = is_active WHERE false",
            "INSERT INTO tracking.carriers (id, code, name, is_active, sends_sequence) VALUES (gen_random_uuid(), 'EVIL', 'x', true, false)",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => TestApi.ScalarAsync(runtime, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        Assert.True(await TestApi.CountAsync(runtime, "SELECT count(*) FROM tracking.carriers WHERE code = 'SIMEX'") == 1);
    }

    [Fact]
    public async Task Simulator_route_exists_only_in_development_with_flag_and_production_refuses_to_start()
    {
        var testing = Factory();
        var manager = await Login(testing, "ops.manager");
        using (var missing = await manager.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = "LS0000000000", scenario = "HappyPath" }, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        var developmentWithoutFlag = Factory(environment: "Development");
        var manager2 = await Login(developmentWithoutFlag, "ops.manager");
        using (var missing = await manager2.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = "LS0000000000", scenario = "HappyPath" }, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // Development dışı her ortamda bayrak açıksa uygulama başlamaz.
        foreach (var environment in new[] { "Production", "Testing" })
        {
            var refused = Factory(environment: environment, settings: new() { ["Simulation:Enabled"] = "true" });
            var error = Assert.ThrowsAny<Exception>(() => refused.CreateHttpsClient());
            Assert.Contains("Simulation:Enabled yalnız Development", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Simulator_sends_signed_events_through_the_real_pipeline_for_ops_manager_only()
    {
        LstApiFactory factory = null!;
        factory = Factory(environment: "Development", settings: new() { ["Simulation:Enabled"] = "true" },
            services: services => services.Replace(ServiceDescriptor.Singleton<ICarrierWebhookTransport>(_ => new TestServerTransport(() => factory.Server))));
        var ops = await Login(factory, "ops.agent");
        var manager = await Login(factory, "ops.manager");
        var (id, tn) = await CarrierShipmentAsync(ops);

        using (var forbidden = await ops.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = tn, scenario = "HappyPath" }, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var invalid = await manager.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = tn, scenario = "Teleport" }, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using var run = await manager.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = tn, scenario = "HappyPath" }, TestApi.Ct);
        var body = await TestApi.JsonAsync(run);
        Assert.True(run.StatusCode == HttpStatusCode.OK, body.ToString());
        var deliveries = body.GetProperty("deliveries").EnumerateArray().ToArray();
        Assert.Equal(5, deliveries.Length);
        Assert.All(deliveries, d => Assert.Equal(202, d.GetProperty("httpStatus").GetInt32()));

        using var badSignature = await manager.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = tn, scenario = "InvalidSignature" }, TestApi.Ct);
        Assert.Equal(401, Assert.Single((await TestApi.JsonAsync(badSignature)).GetProperty("deliveries").EnumerateArray()).GetProperty("httpStatus").GetInt32());

        using var sameId = await manager.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = tn, scenario = "SameIdDifferentPayload" }, TestApi.Ct);
        Assert.Equal([202, 409], (await TestApi.JsonAsync(sameId)).GetProperty("deliveries").EnumerateArray().Select(d => d.GetProperty("httpStatus").GetInt32()));

        var receipts = await ReceiptsAsync(ops, id);
        Assert.Equal(6, receipts.Length);
        Assert.All(receipts, r => Assert.Equal("Received", r.GetProperty("processingStatus").GetString()));
    }

    /// <summary>
    /// Regresyon (Dilim 10 E2E'de bulundu): partideki makbuzlar alınış sırasıyla işlenir. Claim sorgusunun RETURNING sırası garanti
    /// etmediği için sıralı gelen bir akış işçi tarafından karıştırılıyor ve geçerli olaylar Stale sonuçlanıyordu.
    /// </summary>
    [Fact]
    public async Task Batch_claim_processes_receipts_in_arrival_order_so_an_in_order_feed_is_fully_applied()
    {
        LstApiFactory factory = null!;
        factory = Factory(environment: "Development", settings: new() { ["Simulation:Enabled"] = "true" },
            services: services => services.Replace(ServiceDescriptor.Singleton<ICarrierWebhookTransport>(_ => new TestServerTransport(() => factory.Server))));
        var ops = await Login(factory, "ops.agent");
        var manager = await Login(factory, "ops.manager");
        var processor = factory.Services.GetRequiredService<CarrierInboxProcessor>();

        // Birkaç gönderide tekrarlanır: sıra tesadüfen doğru çıkmasın.
        for (var round = 0; round < 4; round++)
        {
            var (id, tn) = await CarrierShipmentAsync(ops);
            using (var run = await manager.PostAsJsonAsync("/api/dev/carrier-simulator/runs", new { trackingNumber = tn, scenario = "HappyPath" }, TestApi.Ct))
            {
                Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            }

            var arrivalOrder = (await ReceiptsAsync(ops, id))
                .OrderBy(r => r.GetProperty("receivedAt").GetDateTimeOffset()).ThenBy(r => r.GetProperty("id").GetGuid())
                .Select(r => r.GetProperty("id").GetGuid()).ToArray();
            Assert.Equal(5, arrivalOrder.Length);

            // İstenen kimlikler ters sırada verilse de claim alınış sırasını korur.
            var claimed = await processor.ClaimAsync(50, [.. arrivalOrder.Reverse()], TestApi.Ct);
            Assert.Equal(arrivalOrder, claimed.Select(c => c.Id));
            foreach (var receipt in claimed)
            {
                Assert.Equal(ProcessResult.Processed, await processor.ProcessAsync(receipt, TestApi.Ct));
            }

            var processed = await ReceiptsAsync(ops, id);
            Assert.All(processed, r => Assert.Equal("Applied", r.GetProperty("outcome").GetString()));
            var (detail, _) = await TestApi.GetDetailAsync(ops, id);
            Assert.Equal("Delivered", detail.GetProperty("status").GetString());
            Assert.DoesNotContain(detail.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("outcome").GetString() != "Applied");
        }
    }

    private sealed class PoisonMarker;

    /// <summary>Gerçek uygulamayı yapar, ardından commit'ten önce hata verir (işçi çökmesi benzetimi).</summary>
    private sealed class FailAfterApplyCarrierEvents(ShipmentCarrierEvents inner) : IShipmentCarrierEvents
    {
        public async Task<CarrierApplyResult> ApplyAsync(Lst.Modules.Shipments.Domain.CarrierEventInput input, CancellationToken cancellationToken)
        {
            await inner.ApplyAsync(input, cancellationToken);
            throw new InvalidOperationException("Simulated crash before commit.");
        }

        public Task<Guid?> ReportConflictAsync(Guid receiptId, Guid carrierId, string trackingNumber, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ReportConflictAsync(receiptId, carrierId, trackingNumber, now, cancellationToken);
    }

    private sealed class PoisonCarrierEvents : IShipmentCarrierEvents
    {
        public Task<CarrierApplyResult> ApplyAsync(Lst.Modules.Shipments.Domain.CarrierEventInput input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated poison message.");

        public Task<Guid?> ReportConflictAsync(Guid receiptId, Guid carrierId, string trackingNumber, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);
    }

    /// <summary>Gerçek bir Npgsql bağlantı hatası üretir (kapalı port): DB kesintisinin işleyiciye yansıması.</summary>
    private sealed class UnreachableDatabaseCarrierEvents : IShipmentCarrierEvents
    {
        public async Task<CarrierApplyResult> ApplyAsync(Lst.Modules.Shipments.Domain.CarrierEventInput input, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2;Gss Encryption Mode=Disable");
            await connection.OpenAsync(cancellationToken);
            throw new InvalidOperationException("Unreachable database was reachable.");
        }

        public Task<Guid?> ReportConflictAsync(Guid receiptId, Guid carrierId, string trackingNumber, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);
    }

    private sealed class TestServerTransport(Func<TestServer> server) : ICarrierWebhookTransport
    {
        public async Task<(int Status, string Body)> SendAsync(string carrierCode, byte[] body, string signatureHeader, CancellationToken cancellationToken)
        {
            using var client = server().CreateClient();
            client.BaseAddress = new Uri("https://localhost/");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/carrier-webhooks/{carrierCode}") { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add(CarrierWebhookParser.SignatureHeader, signatureHeader);
            using var response = await client.SendAsync(request, cancellationToken);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}
