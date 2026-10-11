using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;
using Lst.Modules.Operations.Application;
using Lst.Platform.Outbox;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Lst.IntegrationTests;

/// <summary>Dilim 7: transactional outbox, dağıtıcı (lease/fencing, poison, replay), SLA izleyici, KPI özeti ve audit okuma.</summary>
public sealed class OutboxSlaSummaryTests(LstDatabaseFixture db) : IDisposable
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

    private async Task<long> CountAsync(string sql, params (string, object)[] parameters)
    {
        await using var connection = db.OpenOwnerConnection();
        return await TestApi.CountAsync(connection, sql, parameters);
    }

    private async Task<List<(Guid MessageId, string Type, JsonElement Payload)>> OutboxForAsync(Guid entityId)
    {
        await using var connection = db.OpenOwnerConnection();
        await using var command = new NpgsqlCommand(
            """
            SELECT message_id, type, payload::text FROM platform.outbox_messages
             WHERE payload->>'shipmentId' = @id OR payload->>'exceptionId' = @id ORDER BY id
            """, connection);
        command.Parameters.AddWithValue("id", entityId.ToString());
        var rows = new List<(Guid, string, JsonElement)>();
        await using var reader = await command.ExecuteReaderAsync(TestApi.Ct);
        while (await reader.ReadAsync(TestApi.Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), JsonSerializer.Deserialize<JsonElement>(reader.GetString(2))));
        }

        return rows;
    }

    private static async Task<Guid> EnqueueAsync<T>(LstApiFactory factory, T payload, DateTimeOffset now)
        where T : IOutboxPayload
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        var message = OutboxMessage.Create(payload, now);
        context.Add(message);
        await context.SaveChangesAsync(TestApi.Ct);
        return message.MessageId;
    }

    private async Task<(DateTimeOffset? ProcessedAt, DateTimeOffset? DeadLetteredAt, short Attempts, string? LastError)> MessageAsync(Guid messageId)
    {
        await using var connection = db.OpenOwnerConnection();
        await using var command = new NpgsqlCommand(
            "SELECT processed_at, dead_lettered_at, attempts, last_error FROM platform.outbox_messages WHERE message_id = @id", connection);
        command.Parameters.AddWithValue("id", messageId);
        await using var reader = await command.ExecuteReaderAsync(TestApi.Ct);
        Assert.True(await reader.ReadAsync(TestApi.Ct));
        return (reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    [Fact]
    public async Task Status_changes_write_pii_free_outbox_messages_in_the_same_transaction()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var json = new ShipmentJson();
        var (id, _, etag) = await TestApi.CreateAsync(ops, json);
        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = "Müşteri vazgeçti" }, etag))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var messages = await OutboxForAsync(id);
        var statusChanges = messages.Where(m => m.Type == OutboxMessageTypes.ShipmentStatusChanged).Select(m => m.Payload).ToList();
        Assert.Equal(2, statusChanges.Count);
        Assert.Equal(JsonValueKind.Null, statusChanges[0].GetProperty("statusBefore").ValueKind);
        Assert.Equal("Created", statusChanges[0].GetProperty("statusAfter").GetString());
        Assert.Equal(("Created", "Cancelled"), (statusChanges[1].GetProperty("statusBefore").GetString(), statusChanges[1].GetProperty("statusAfter").GetString()));
        Assert.Contains(db.Hubs["IST-01"], statusChanges[1].GetProperty("hubIds").EnumerateArray().Select(h => h.GetGuid()));
        var raw = string.Join("\n", messages.Select(m => m.Payload.GetRawText()));
        foreach (var pii in json.PiiValues.Append("Müşteri vazgeçti"))
        {
            Assert.DoesNotContain(pii, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Failed_outbox_insert_rolls_back_the_status_change_and_timeline_entry()
    {
        var factory = Factory(services: s => s.AddSingleton<IInterceptor, BrokenOutboxInterceptor>());
        var ops = await Login(factory, "ops.agent");
        var healthy = await Login(Factory(), "ops.agent");
        var (id, _, etag) = await TestApi.CreateAsync(healthy, new ShipmentJson());
        var eventsBefore = await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id", ("id", id));

        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = "Test" }, etag))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, cancel.StatusCode);
        }

        var (detail, after) = await TestApi.GetDetailAsync(healthy, id);
        Assert.Equal("Created", detail.GetProperty("status").GetString());
        Assert.Equal(etag, after);
        Assert.Equal(eventsBefore, await CountAsync("SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id", ("id", id)));
        Assert.Single(await OutboxForAsync(id), m => m.Type == OutboxMessageTypes.ShipmentStatusChanged);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM platform.audit_entries WHERE action = 'ShipmentCancelled' AND entity_id = @id", ("id", id.ToString())));
    }

    [Fact]
    public async Task Crash_after_handler_before_processed_is_redelivered_and_deduplicated_by_the_consumer()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = Factory(time);
        var ops = await Login(factory, "ops.agent");
        var (shipmentId, _, _) = await TestApi.CreateAsync(ops, new ShipmentJson());
        var messageId = await EnqueueAsync(factory, new Lst.Modules.Shipments.Infrastructure.RecipientNotificationOutboxPayload(shipmentId, "OutForDelivery"), time.GetUtcNow());
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();

        var first = Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));
        Assert.True(await dispatcher.RunHandlersAsync(first, TestApi.Ct));
        // Süreç burada "çöktü": processed_at yazılmadı. Lease dolmadan başka işçi alamaz.
        Assert.Empty(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));
        time.Advance(TimeSpan.FromSeconds(61));
        var second = Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));
        Assert.NotEqual(first.LockToken, second.LockToken);
        Assert.Equal(OutboxResult.Processed, await dispatcher.ProcessAsync(second, TestApi.Ct));
        // Eski işçi sonucu yazamaz (fencing).
        Assert.Equal(OutboxResult.LostLease, await dispatcher.ProcessAsync(first, TestApi.Ct));

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM platform.notification_deliveries WHERE message_id = @id", ("id", messageId)));
        Assert.NotNull((await MessageAsync(messageId)).ProcessedAt);
        Assert.Contains(factory.Logs.Entries, e => e.Contains("Duplicate outbox delivery", StringComparison.Ordinal) && e.Contains(messageId.ToString(), StringComparison.Ordinal));
        Assert.Empty(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));
    }

    [Fact]
    public async Task Message_without_handler_is_never_marked_processed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = Factory(time, settings: new() { ["Workers:Outbox:MaxAttempts"] = "2" });
        var messageId = await EnqueueAsync(factory, new UnhandledPayload(Guid.NewGuid()), time.GetUtcNow());
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();

        Assert.Equal(OutboxResult.RetryScheduled, await dispatcher.ProcessAsync(Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct)), TestApi.Ct));
        var pending = await MessageAsync(messageId);
        Assert.Equal(((DateTimeOffset?)null, (short)1, OutboxDispatcher.NoHandlerError), (pending.ProcessedAt, pending.Attempts, pending.LastError));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(OutboxResult.DeadLettered, await dispatcher.ProcessAsync(Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct)), TestApi.Ct));
        var dead = await MessageAsync(messageId);
        Assert.Null(dead.ProcessedAt);
        Assert.NotNull(dead.DeadLetteredAt);
    }

    [Fact]
    public async Task Poison_message_dead_letters_after_eight_attempts_and_only_ops_manager_can_replay_it_with_audit()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = Factory(time, services => services.AddScoped<IOutboxHandler, PoisonHandler>());
        var messageId = await EnqueueAsync(factory, new PoisonPayload(Guid.NewGuid()), time.GetUtcNow());
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();

        var results = new List<OutboxResult>();
        for (var i = 0; i < 8; i++)
        {
            var claimed = Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));
            results.Add(await dispatcher.ProcessAsync(claimed, TestApi.Ct));
            // Backoff dolmadan yeniden claim edilmez.
            Assert.Empty(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));
            time.Advance(TimeSpan.FromMinutes(20));
        }

        Assert.Equal([.. Enumerable.Repeat(OutboxResult.RetryScheduled, 7), OutboxResult.DeadLettered], results);
        var dead = await MessageAsync(messageId);
        Assert.Equal(((short)8, typeof(InvalidOperationException).FullName), (dead.Attempts, dead.LastError));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM platform.audit_entries WHERE action = 'OutboxMessageDeadLettered' AND entity_id = @id", ("id", messageId.ToString())));

        var manager = await Login(factory, "ops.manager");
        var agent = await Login(factory, "ops.agent");
        using (var list = await manager.GetAsync(new Uri("/api/operations/outbox?status=DeadLettered&limit=200", UriKind.Relative), TestApi.Ct))
        {
            Assert.Contains((await TestApi.JsonAsync(list)).EnumerateArray(), m => m.GetProperty("messageId").GetGuid() == messageId);
        }

        using (var forbidden = await agent.PostAsync(new Uri($"/api/operations/outbox/{messageId}/replay", UriKind.Relative), null, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var replay = await manager.PostAsync(new Uri($"/api/operations/outbox/{messageId}/replay", UriKind.Relative), null, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        }

        var replayed = await MessageAsync(messageId);
        Assert.Equal(((DateTimeOffset?)null, (short)0), (replayed.DeadLetteredAt, replayed.Attempts));
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM platform.audit_entries WHERE action = 'OutboxMessageReplayed' AND entity_id = @id AND actor_id = @actor",
            ("id", messageId.ToString()), ("actor", (await db.UserIdAsync("ops.manager")).ToString())));
        Assert.Single(await dispatcher.ClaimAsync(10, [messageId], TestApi.Ct));

        using (var notDead = await manager.PostAsync(new Uri($"/api/operations/outbox/{messageId}/replay", UriKind.Relative), null, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, notDead.StatusCode);
        }

        using var unknown = await manager.PostAsync(new Uri($"/api/operations/outbox/{Guid.NewGuid()}/replay", UriKind.Relative), null, TestApi.Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Sla_monitor_opens_escalates_and_resolves_idempotently_with_outbox_messages()
    {
        // Salı 10:00 (yerel) oluşturma → Standard söz: Perşembe.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero));
        var factory = Factory(time);
        var (id, labels, _) = await TestApi.CreateAsync(await Login(factory, "ops.agent"), new ShipmentJson(packageCount: 1));
        var monitor = factory.Services.GetRequiredService<SlaMonitor>();

        time.SetUtcNow(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero)); // Cuma: 1 gün gecikme, 72 iş saati hareketsiz
        Assert.Equal(new SlaRunResult(1, 2, 0, 0), await monitor.RunOnceAsync([id], TestApi.Ct));
        Assert.Equal(new SlaRunResult(1, 0, 0, 0), await monitor.RunOnceAsync([id], TestApi.Ct));
        var ops = await Login(factory, "ops.agent");
        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        var open = detail.GetProperty("openExceptions").EnumerateArray().ToDictionary(e => e.GetProperty("type").GetString()!, e => e.GetProperty("severity").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["DeliveryOverdue"] = "Medium", ["NoMovement"] = "Medium" }, open);

        time.SetUtcNow(new DateTimeOffset(2026, 9, 5, 7, 0, 0, TimeSpan.Zero)); // Cumartesi: 2 gün gecikme → High
        Assert.Equal(new SlaRunResult(1, 0, 1, 0), await monitor.RunOnceAsync([id], TestApi.Ct));
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM operations.shipment_exceptions WHERE shipment_id = @id AND type = 'DeliveryOverdue' AND severity = 'High' AND occurrence_count = 1", ("id", id)));

        // Hareket (varış taraması) NoMovement'ı kapatır; gecikme sürer.
        var hub = await Login(factory, "hub.ist");
        using (var scan = await TestApi.ScanAsync(hub, "IST-01", labels[0], "Arrival", time.GetUtcNow().AddMinutes(-1)))
        {
            Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        }

        Assert.Equal(new SlaRunResult(1, 0, 0, 1), await monitor.RunOnceAsync([id], TestApi.Ct));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM operations.shipment_exceptions WHERE shipment_id = @id AND type = 'NoMovement' AND status <> 'Resolved'", ("id", id)));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM operations.shipment_exceptions WHERE shipment_id = @id AND type = 'DeliveryOverdue' AND status <> 'Resolved'", ("id", id)));

        // İptal (terminal) gecikmeyi otomatik kapatır; sonraki tur değişiklik yapmaz.
        ops = await Login(factory, "ops.agent");
        var (_, etag) = await TestApi.GetDetailAsync(ops, id);
        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = "Test" }, etag))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var final = await monitor.RunOnceAsync([id], TestApi.Ct);
        Assert.True(final is null || final.Opened + final.Escalated + final.Resolved == 0);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM operations.shipment_exceptions WHERE shipment_id = @id AND status <> 'Resolved'", ("id", id)));

        var exceptionIds = new List<Guid>();
        await using (var connection = db.OpenOwnerConnection())
        await using (var command = new NpgsqlCommand("SELECT id FROM operations.shipment_exceptions WHERE shipment_id = @id", connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(TestApi.Ct);
            while (await reader.ReadAsync(TestApi.Ct))
            {
                exceptionIds.Add(reader.GetGuid(0));
            }
        }

        Assert.Equal(2, exceptionIds.Count);
        foreach (var exceptionId in exceptionIds)
        {
            var types = (await OutboxForAsync(exceptionId)).Select(m => m.Type).ToList();
            Assert.Equal([OutboxMessageTypes.ExceptionOpened, OutboxMessageTypes.ExceptionResolved], types);
        }

        Assert.Equal(2, await CountAsync(
            "SELECT count(*) FROM platform.audit_entries WHERE actor_id = 'system:sla-monitor' AND action = 'ExceptionOpened' AND entity_id = ANY(@ids)",
            ("ids", exceptionIds.Select(e => e.ToString()).ToArray())));
    }

    [Fact]
    public async Task Two_concurrent_sla_monitors_open_exactly_one_exception_per_type()
    {
        var start = new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var factory = Factory(time);
        var ops = await Login(factory, "ops.agent");
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await TestApi.CreateAsync(ops, new ShipmentJson(packageCount: 1))).Id);
        }

        var later = new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero);
        time.SetUtcNow(later);
        var other = Factory(new FakeTimeProvider(later));
        var monitors = new[] { factory.Services.GetRequiredService<SlaMonitor>(), other.Services.GetRequiredService<SlaMonitor>() };
        var results = await Task.WhenAll(monitors.Select(m => m.RunOnceAsync(ids, TestApi.Ct)));
        Assert.Contains(results, r => r is not null);

        // İkinci tur da (kilit serbest) yeni kayıt açmaz.
        await Task.WhenAll(monitors.Select(m => m.RunOnceAsync(ids, TestApi.Ct)));
        foreach (var id in ids)
        {
            Assert.Equal(1, await CountAsync("SELECT count(*) FROM operations.shipment_exceptions WHERE shipment_id = @id AND type = 'DeliveryOverdue'", ("id", id)));
            Assert.Equal(1, await CountAsync("SELECT count(*) FROM operations.shipment_exceptions WHERE shipment_id = @id AND type = 'NoMovement'", ("id", id)));
        }
    }

    [Fact]
    public async Task Summary_kpis_match_real_aggregates_for_an_isolated_hub()
    {
        var hubCode = $"K{(char)('A' + Random.Shared.Next(26))}{(char)('A' + Random.Shared.Next(26))}-{Random.Shared.Next(10, 99)}";
        await using (var owner = db.OpenOwnerConnection())
        {
            await TestApi.ScalarAsync(owner,
                "INSERT INTO operations.hubs (id, code, name, city, district, is_active, created_at) VALUES (gen_random_uuid(), @code, 'KPI Test Dağıtım', 'Ankara', NULL, true, now())",
                ("code", hubCode));
        }

        DateTimeOffset Local(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(3));
        var time = new FakeTimeProvider(Local(8, 3, 9)); // Pazartesi 09:00 → söz Çarşamba 5 Ağustos
        var factory = Factory(time);
        var carrier = factory.CreateHttpsClient();
        var ops = await Login(factory, "ops.agent");
        var shipments = new List<(Guid Id, string Tn)>();
        for (var i = 0; i < 4; i++)
        {
            var (id, _, _) = await TestApi.CreateAsync(ops, new ShipmentJson(destination: hubCode, packageCount: 1));
            var (detail, etag) = await TestApi.GetDetailAsync(ops, id);
            Assert.Equal("2026-08-05", detail.GetProperty("promisedDeliveryDate").GetString());
            if (i < 3)
            {
                await CarrierHelper.AssignSimexAsync(ops, id);
            }
            else
            {
                using var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = "KPI" }, etag);
                Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            }

            shipments.Add((id, detail.GetProperty("trackingNumber").GetString()!));
        }

        var processedAt = new List<DateTimeOffset>();
        async Task Event(int index, string type, DateTimeOffset at, object? attempt = null)
        {
            time.SetUtcNow(at);
            var receipt = await CarrierHelper.SendAsync(carrier, shipments[index].Tn, type, at.AddMinutes(-1), at, attempt);
            Assert.Equal("Applied", await CarrierHelper.ProcessAsync(factory, db, receipt));
            processedAt.Add(at);
        }

        for (var i = 0; i < 3; i++)
        {
            await Event(i, "PICKED_UP", Local(8, 3, 9, 30 + i));
        }

        await Event(0, "OUT_FOR_DELIVERY", Local(8, 5, 8));
        await Event(1, "OUT_FOR_DELIVERY", Local(8, 5, 8, 5));
        await Event(0, "DELIVERED", Local(8, 5, 11));
        await Event(1, "DELIVERY_FAILED", Local(8, 5, 11, 5), new { number = 1, reason = "RECIPIENT_ABSENT" });
        await Event(1, "OUT_FOR_DELIVERY", Local(8, 6, 9, 5));
        await Event(1, "DELIVERED", Local(8, 6, 11));

        var now = Local(8, 7, 9);
        time.SetUtcNow(now);
        Assert.NotNull(await factory.Services.GetRequiredService<SlaMonitor>().RunOnceAsync([shipments[2].Id], TestApi.Ct));
        var manager = await Login(factory, "ops.manager");

        using (var dueDay = await manager.GetAsync(new Uri($"/api/operations/summary?hubCode={hubCode}&date=2026-08-05", UriKind.Relative), TestApi.Ct))
        {
            var body = await TestApi.JsonAsync(dueDay);
            Assert.True(dueDay.StatusCode == HttpStatusCode.OK, body.ToString());
            Assert.Equal((3, 2), (body.GetProperty("dueToday").GetProperty("total").GetInt32(), body.GetProperty("dueToday").GetProperty("delivered").GetInt32()));
            Assert.Equal(0, body.GetProperty("delayedOpen").GetInt32());
            var reason = Assert.Single(body.GetProperty("failedAttemptReasonsToday").EnumerateArray());
            Assert.Equal(("RecipientAbsent", 1), (reason.GetProperty("key").GetString(), reason.GetProperty("count").GetInt32()));
        }

        using var today = await manager.GetAsync(new Uri($"/api/operations/summary?hubCode={hubCode}", UriKind.Relative), TestApi.Ct);
        var summary = await TestApi.JsonAsync(today);
        Assert.Equal("2026-08-07", summary.GetProperty("date").GetString());
        Assert.Equal("Europe/Istanbul", summary.GetProperty("timeZone").GetString());
        Assert.Equal(1, summary.GetProperty("delayedOpen").GetInt32());
        Assert.Equal((1, 2, 0.5m), Ratio(summary.GetProperty("onTimeDelivery7Days")));
        Assert.Equal((1, 2, 0.5m), Ratio(summary.GetProperty("firstAttemptDelivery7Days")));
        var exceptions = summary.GetProperty("openExceptions");
        Assert.Equal((2, 0, 0L), (exceptions.GetProperty("count").GetInt32(), exceptions.GetProperty("overdue").GetInt32(), exceptions.GetProperty("oldestAgeSeconds").GetInt64()));
        var applied = Assert.Single(summary.GetProperty("carrierOutcomes24Hours").EnumerateArray());
        Assert.Equal(("Applied", processedAt.Count(p => p >= now.AddHours(-24))), (applied.GetProperty("key").GetString(), applied.GetProperty("count").GetInt32()));
        Assert.Empty(summary.GetProperty("failedAttemptReasonsToday").EnumerateArray());

        static (int, int, decimal?) Ratio(JsonElement r) =>
            (r.GetProperty("numerator").GetInt32(), r.GetProperty("denominator").GetInt32(), r.GetProperty("rate").ValueKind == JsonValueKind.Null ? null : r.GetProperty("rate").GetDecimal());
    }

    [Fact]
    public async Task Summary_and_audit_enforce_roles_validation_and_never_return_pii()
    {
        var factory = Factory();
        var ops = await Login(factory, "ops.agent");
        var manager = await Login(factory, "ops.manager");
        var hub = await Login(factory, "hub.ist");
        var json = new ShipmentJson();
        var (id, _, etag) = await TestApi.CreateAsync(ops, json);
        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = "Gizli neden metni" }, etag))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        foreach (var (client, path, expected) in new[]
        {
            (hub, "/api/operations/summary", HttpStatusCode.Forbidden),
            (factory.CreateHttpsClient(), "/api/operations/summary", HttpStatusCode.Unauthorized),
            (ops, "/api/operations/summary?date=10.10.2026", HttpStatusCode.BadRequest),
            (ops, "/api/operations/summary?hubCode=ist-01", HttpStatusCode.BadRequest),
            (ops, "/api/operations/summary?hubCode=ZZZ-99", HttpStatusCode.UnprocessableEntity),
            (ops, "/api/operations/summary", HttpStatusCode.OK),
            (ops, $"/api/shipments/{id}/audit", HttpStatusCode.Forbidden),
            (hub, $"/api/shipments/{id}/audit", HttpStatusCode.Forbidden),
            (manager, $"/api/shipments/{Guid.NewGuid()}/audit", HttpStatusCode.NotFound),
            (ops, "/api/operations/outbox", HttpStatusCode.Forbidden),
        })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestApi.Ct);
            Assert.True(expected == response.StatusCode, $"{path}: {response.StatusCode}");
        }

        using var audit = await manager.GetAsync(new Uri($"/api/shipments/{id}/audit", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var text = await audit.Content.ReadAsStringAsync(TestApi.Ct);
        foreach (var pii in json.PiiValues.Append("Gizli neden metni"))
        {
            Assert.DoesNotContain(pii, text, StringComparison.Ordinal);
        }

        var page = await TestApi.JsonAsync(audit);
        var actions = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        Assert.Equal(["ShipmentCancelled", "ShipmentCreated"], actions);
        Assert.Equal("User", page.GetProperty("items")[0].GetProperty("actorType").GetString());
        Assert.False(string.IsNullOrEmpty(page.GetProperty("items")[0].GetProperty("actorDisplayName").GetString()));

        using var first = await manager.GetAsync(new Uri($"/api/shipments/{id}/audit?limit=1", UriKind.Relative), TestApi.Ct);
        var firstPage = await TestApi.JsonAsync(first);
        var before = firstPage.GetProperty("nextBefore").GetInt64();
        using var second = await manager.GetAsync(new Uri($"/api/shipments/{id}/audit?limit=1&before={before}", UriKind.Relative), TestApi.Ct);
        var secondPage = await TestApi.JsonAsync(second);
        Assert.Equal("ShipmentCreated", Assert.Single(secondPage.GetProperty("items").EnumerateArray()).GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Runtime_role_cannot_tamper_with_notification_deliveries()
    {
        await using var runtime = db.OpenRuntimeConnection();
        foreach (var sql in new[] { "DELETE FROM platform.notification_deliveries WHERE false", "UPDATE platform.notification_deliveries SET template = template WHERE false" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => TestApi.ScalarAsync(runtime, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    private sealed record UnhandledPayload(Guid ShipmentId) : IOutboxPayload
    {
        public static string MessageType => "test.unhandled.v1";
    }

    private sealed record PoisonPayload(Guid ShipmentId) : IOutboxPayload
    {
        public static string MessageType => "test.poison.v1";
    }

    private sealed class PoisonHandler : IOutboxHandler
    {
        public string MessageType => PoisonPayload.MessageType;

        public Task HandleAsync(OutboxEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated poison handler.");
    }

    /// <summary>Gönderi değiştiğinde CHECK kısıtını ihlal eden bir outbox satırı ekler: outbox INSERT hatası benzetimi.</summary>
    private sealed class BrokenOutboxInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            if (context.ChangeTracker.Entries<Lst.Modules.Shipments.Domain.Shipment>().Any(e => e.State == EntityState.Modified))
            {
                context.Add(OutboxMessage.Create(new BrokenPayload(Guid.NewGuid()), DateTimeOffset.UtcNow));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed record BrokenPayload(Guid ShipmentId) : IOutboxPayload
    {
        public static string MessageType => "INVALID TYPE";
    }
}
