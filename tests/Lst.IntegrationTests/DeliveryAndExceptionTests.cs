using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;

namespace Lst.IntegrationTests;

/// <summary>Dilim 5: teslimat denemeleri, istisna kuyruğu ve göndericiye iade (gerçek PostgreSQL).</summary>
public sealed class DeliveryAndExceptionTests(LstDatabaseFixture db) : IDisposable
{
    private readonly LstApiFactory _factory = new(db);
    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddSeconds(1);

    public void Dispose() => _factory.Dispose();

    private DateTimeOffset Next() => _clock = _clock.AddSeconds(10);

    /// <summary>Aynı koli + tip + hub taramasını iş tekrarı penceresinin (120 sn) dışına taşır.</summary>
    private DateTimeOffset Later() => _clock = _clock.AddSeconds(130);

    private async Task<HttpClient> Client(string user)
    {
        var client = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User(user));
        return client;
    }

    private static async Task Ok(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(TestApi.Ct)}");
    }

    /// <summary>IST-01 → ANK-02: varış, çıkış, varış ve dağıtıma çıkış (verilen koliler için).</summary>
    private async Task<(Guid Id, string[] Labels)> OutForDeliveryAsync(HttpClient ops, HttpClient ist, HttpClient ank, int packages = 1, int ofdPackages = -1)
    {
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson(packageCount: packages));
        foreach (var label in labels)
        {
            await Ok(TestApi.ScanAsync(ist, "IST-01", label, "Arrival", Next()));
        }

        foreach (var label in labels)
        {
            await Ok(TestApi.ScanAsync(ist, "IST-01", label, "Departure", Next()));
        }

        foreach (var label in labels)
        {
            await Ok(TestApi.ScanAsync(ank, "ANK-02", label, "Arrival", Next()));
        }

        foreach (var label in labels.Take(ofdPackages < 0 ? labels.Length : ofdPackages))
        {
            await Ok(TestApi.ScanAsync(ank, "ANK-02", label, "OutForDelivery", Next()));
        }

        return (id, labels);
    }

    private Task<HttpResponseMessage> AttemptAsync(HttpClient client, Guid id, string outcome, string? reason = null, string? key = null, DateTimeOffset? at = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/shipments/{id}/delivery-attempts")
        {
            Content = JsonContent.Create(new { outcome, failureReason = reason, occurredAt = (at ?? Next()).ToString("O") }),
        };
        request.Headers.Add("Idempotency-Key", key ?? TestApi.NewKey());
        return client.SendAsync(request, TestApi.Ct);
    }

    private static async Task<JsonElement> ExceptionAsync(HttpClient ops, Guid shipmentId, string type)
    {
        var (detail, _) = await TestApi.GetDetailAsync(ops, shipmentId);
        return detail.GetProperty("openExceptions").EnumerateArray().Single(e => e.GetProperty("type").GetString() == type);
    }

    private static async Task<(JsonElement Item, string ETag)> QueueItemAsync(HttpClient ops, Guid exceptionId)
    {
        string? cursor = null;
        do
        {
            using var response = await ops.GetAsync(new Uri("/api/exceptions?limit=200" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), UriKind.Relative), TestApi.Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await TestApi.JsonAsync(response);
            foreach (var item in body.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("id").GetGuid() == exceptionId)
                {
                    return (item, item.GetProperty("etag").GetString()!);
                }
            }

            cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        throw new InvalidOperationException("istisna kuyrukta yok");
    }

    [Fact]
    public async Task Successful_delivery_is_terminal_and_replays_byte_for_byte()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        var (id, _) = await OutForDeliveryAsync(ops, ist, ank);
        var (before, _) = await TestApi.GetDetailAsync(ank, id);
        Assert.Contains("RecordDeliveryAttempt", before.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));

        var key = TestApi.NewKey();
        var at = Next();
        using var first = await AttemptAsync(ank, id, "Delivered", key: key, at: at);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal($"/api/shipments/{id}/delivery-attempts/1", first.Headers.Location!.OriginalString);
        using var replay = await AttemptAsync(ank, id, "Delivered", key: key, at: at);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(TestApi.Ct), await replay.Content.ReadAsByteArrayAsync(TestApi.Ct));

        var (detail, etag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(first.Headers.ETag!.ToString(), etag);
        Assert.Equal("Delivered", detail.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("deliveredAt").ValueKind);
        Assert.Equal(1, detail.GetProperty("deliveryAttempts").GetProperty("count").GetInt32());
        Assert.Empty(detail.GetProperty("allowedActions").EnumerateArray());

        using var again = await AttemptAsync(ank, id, "Delivered");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("transition-not-allowed", await TestApi.ProblemCodeAsync(again));
    }

    [Fact]
    public async Task Delivery_is_all_or_nothing_and_missing_package_is_a_failed_attempt()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        var (id, _) = await OutForDeliveryAsync(ops, ist, ank, packages: 2, ofdPackages: 1);

        using var delivered = await AttemptAsync(ank, id, "Delivered");
        Assert.Equal(HttpStatusCode.Conflict, delivered.StatusCode);
        Assert.Equal("packages-not-out-for-delivery", await TestApi.ProblemCodeAsync(delivered));

        using var missing = await AttemptAsync(ank, id, "Failed", "PackageMissing");
        Assert.Equal(HttpStatusCode.Created, missing.StatusCode);
        var (detail, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("DeliveryFailed", detail.GetProperty("status").GetString());
        Assert.Equal("PackageMissing", detail.GetProperty("deliveryAttempts").GetProperty("attempts")[0].GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Failed_attempts_refresh_one_exception_and_max_attempts_need_a_single_grant()
    {
        using var ops = await Client("ops.agent");
        using var manager = await Client("ops.manager");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        var (id, labels) = await OutForDeliveryAsync(ops, ist, ank);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                await Ok(TestApi.ScanAsync(ank, "ANK-02", labels[0], "OutForDelivery", Later()));
            }

            using var failed = await AttemptAsync(ank, id, "Failed", "RecipientAbsent");
            Assert.Equal(HttpStatusCode.Created, failed.StatusCode);
        }

        var deliveryFailed = await ExceptionAsync(ops, id, "DeliveryFailed");
        Assert.Equal(3, deliveryFailed.GetProperty("occurrenceCount").GetInt32());
        var max = await ExceptionAsync(ops, id, "MaxAttemptsReached");
        Assert.Equal("High", max.GetProperty("severity").GetString());

        using (var blocked = await TestApi.ScanAsync(ank, "ANK-02", labels[0], "OutForDelivery", Later()))
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal("max-attempts-reached", await TestApi.ProblemCodeAsync(blocked));
        }

        var (_, maxETag) = await QueueItemAsync(manager, max.GetProperty("id").GetGuid());
        using (var grant = await TestApi.PostCommandAsync(manager, $"/api/exceptions/{max.GetProperty("id").GetGuid()}/resolve",
                   new { resolutionCode = "ReattemptApproved", note = "Alıcı yarın evde olacak" }, maxETag))
        {
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
            Assert.Equal("Resolved", (await TestApi.JsonAsync(grant)).GetProperty("status").GetString());
        }

        using (var replayGrant = await TestApi.PostCommandAsync(manager, $"/api/exceptions/{max.GetProperty("id").GetGuid()}/resolve",
                   new { resolutionCode = "ReattemptApproved", note = "tekrar" }, maxETag))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, replayGrant.StatusCode);
        }

        var (afterGrant, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(4, afterGrant.GetProperty("deliveryAttempts").GetProperty("max").GetInt32());
        await Ok(TestApi.ScanAsync(ank, "ANK-02", labels[0], "OutForDelivery", Later()));
        using (var fourth = await AttemptAsync(ank, id, "Failed", "AddressNotFound"))
        {
            Assert.Equal(HttpStatusCode.Created, fourth.StatusCode);
        }

        using (var blockedAgain = await TestApi.ScanAsync(ank, "ANK-02", labels[0], "OutForDelivery", Later()))
        {
            Assert.Equal("max-attempts-reached", await TestApi.ProblemCodeAsync(blockedAgain));
        }

        await using var connection = db.OpenOwnerConnection();
        Assert.Equal(4, await TestApi.CountAsync(connection, "SELECT count(*) FROM shipments.delivery_attempts WHERE shipment_id = @id", ("id", id)));
        Assert.Equal(1, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM platform.audit_entries WHERE entity_id = @id AND action = 'ExtraAttemptGranted'", ("id", id.ToString())));
    }

    [Fact]
    public async Task Concurrent_attempts_with_different_keys_record_a_single_attempt_number()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        using var ank1 = await Client("hub.ank");
        using var ank2 = await Client("hub.ank");
        var (id, _) = await OutForDeliveryAsync(ops, ist, ank1);
        var at = Next();

        using var barrier = new Barrier(2);
        var responses = await Task.WhenAll(new[] { ank1, ank2 }.Select(c => Task.Run(async () =>
        {
            barrier.SignalAndWait(TestApi.Ct);
            return await AttemptAsync(c, id, "Failed", "RecipientAbsent", at: at);
        })));
        var codes = responses.Select(r => r.StatusCode).Order().ToList();
        foreach (var r in responses)
        {
            r.Dispose();
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], codes);
        await using var connection = db.OpenOwnerConnection();
        Assert.Equal(1, await TestApi.CountAsync(connection, "SELECT count(*) FROM shipments.delivery_attempts WHERE shipment_id = @id", ("id", id)));
    }

    [Fact]
    public async Task Concurrent_assignment_with_same_etag_yields_one_412_and_resolution_needs_assignment()
    {
        using var ops1 = await Client("ops.agent");
        using var ops2 = await Client("ops.agent2");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        var (id, _) = await OutForDeliveryAsync(ops1, ist, ank);
        using (var failed = await AttemptAsync(ank, id, "Failed", "RecipientRefused"))
        {
            Assert.Equal(HttpStatusCode.Created, failed.StatusCode);
        }

        var exceptionId = (await ExceptionAsync(ops1, id, "DeliveryFailed")).GetProperty("id").GetGuid();
        var (_, etag) = await QueueItemAsync(ops1, exceptionId);

        using (var unassigned = await TestApi.PostCommandAsync(ops1, $"/api/exceptions/{exceptionId}/resolve", new { resolutionCode = "NoActionNeeded", note = "x" }, etag))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unassigned.StatusCode);
        }

        var users = new[] { (ops1, await db.UserIdAsync("ops.agent")), (ops2, await db.UserIdAsync("ops.agent2")) };
        using var barrier = new Barrier(2);
        var responses = await Task.WhenAll(users.Select(u => Task.Run(async () =>
        {
            barrier.SignalAndWait(TestApi.Ct);
            return await TestApi.PostCommandAsync(u.Item1, $"/api/exceptions/{exceptionId}/assign", new { assigneeUserId = u.Item2 }, etag);
        })));
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.PreconditionFailed, responses.Single(r => r != winner).StatusCode);
        var assigned = await TestApi.JsonAsync(winner);
        var winnerClient = assigned.GetProperty("assignedTo").GetProperty("id").GetGuid() == users[0].Item2 ? ops1 : ops2;
        var newETag = winner.Headers.ETag!.ToString();
        foreach (var r in responses)
        {
            r.Dispose();
        }

        // OpsAgent yalnız kendini atayabilir: kaybeden görevlinin istisnayı başkasına (kazanana) ataması reddedilir.
        using (var otherAgent = await TestApi.PostCommandAsync(winnerClient == ops1 ? ops2 : ops1, $"/api/exceptions/{exceptionId}/assign",
                   new { assigneeUserId = assigned.GetProperty("assignedTo").GetProperty("id").GetGuid() }, newETag))
        {
            Assert.Equal(HttpStatusCode.Forbidden, otherAgent.StatusCode);
        }

        using var resolved = await TestApi.PostCommandAsync(winnerClient, $"/api/exceptions/{exceptionId}/resolve", new { resolutionCode = "NoActionNeeded", note = "Alıcı tekrar arandı" }, newETag);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var resolvedETag = resolved.Headers.ETag!.ToString();
        using var again = await TestApi.PostCommandAsync(winnerClient, $"/api/exceptions/{exceptionId}/resolve", new { resolutionCode = "NoActionNeeded", note = "tekrar" }, resolvedETag);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("exception-resolved", await TestApi.ProblemCodeAsync(again));
    }

    [Fact]
    public async Task Refused_shipment_action_rolls_back_the_resolution()
    {
        using var ops = await Client("ops.agent");
        using var manager = await Client("ops.manager");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        var (id, labels) = await OutForDeliveryAsync(ops, ist, ank);
        using (var failed = await AttemptAsync(ank, id, "Failed", "RecipientAbsent"))
        {
            Assert.Equal(HttpStatusCode.Created, failed.StatusCode);
        }

        // Yeniden dağıtıma çıkış (T16): artık OutForDelivery, iade başlatılamaz.
        await Ok(TestApi.ScanAsync(ank, "ANK-02", labels[0], "OutForDelivery", Later()));
        var exceptionId = (await ExceptionAsync(ops, id, "DeliveryFailed")).GetProperty("id").GetGuid();
        var (_, etag) = await QueueItemAsync(manager, exceptionId);
        var (_, shipmentETag) = await TestApi.GetDetailAsync(ops, id);

        using var refused = await TestApi.PostCommandAsync(manager, $"/api/exceptions/{exceptionId}/resolve",
            new { resolutionCode = "ReturnToSenderInitiated", note = "İade" }, etag);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await TestApi.JsonAsync(refused);
        Assert.Equal("return-not-allowed", problem.GetProperty("code").GetString());
        Assert.Equal("OutForDelivery", problem.GetProperty("currentShipmentStatus").GetString());

        var (item, etagAfter) = await QueueItemAsync(manager, exceptionId);
        Assert.Equal(etag, etagAfter);
        Assert.Equal("Open", item.GetProperty("status").GetString());
        var (detail, shipmentETagAfter) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(shipmentETag, shipmentETagAfter);
        Assert.Equal("OutForDelivery", detail.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Return_to_sender_lifecycle_auto_resolves_exceptions_and_is_terminal()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        var (id, labels) = await OutForDeliveryAsync(ops, ist, ank);
        using (var failed = await AttemptAsync(ank, id, "Failed", "AddressNotFound"))
        {
            Assert.Equal(HttpStatusCode.Created, failed.StatusCode);
        }

        var (detail, etag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Contains("ReturnToSender", detail.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        using (var start = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/return-to-sender", new { reason = "Adres bulunamadı" }, etag))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            Assert.Equal("ReturningToSender", (await TestApi.JsonAsync(start)).GetProperty("status").GetString());
        }

        var arrival = await TestApi.JsonAsync(await TestApi.ScanAsync(ank, "ANK-02", labels[0], "Arrival", Later()));
        Assert.Equal("ReturningToSender", arrival.GetProperty("shipment").GetProperty("status").GetString());
        Assert.Equal("ANK-02", arrival.GetProperty("shipment").GetProperty("currentHubCode").GetString());

        var (returning, returningETag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(["CompleteReturn"], returning.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        using (var cancel = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = "x" }, returningETag))
        {
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        }

        using (var complete = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/return-completed", new { }, returningETag))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        var (returned, returnedETag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("ReturnedToSender", returned.GetProperty("status").GetString());
        Assert.Empty(returned.GetProperty("openExceptions").EnumerateArray());
        Assert.Empty(returned.GetProperty("allowedActions").EnumerateArray());
        using (var again = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/return-completed", new { }, returnedETag))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        await using var connection = db.OpenOwnerConnection();
        Assert.Equal(1, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM operations.shipment_exceptions WHERE shipment_id = @id AND resolution_code = 'AutoResolved'", ("id", id)));
    }

    [Fact]
    public async Task Return_to_sender_is_refused_before_departure_and_after_delivery()
    {
        using var ops = await Client("ops.agent");
        var (id, _, etag) = await TestApi.CreateAsync(ops, new ShipmentJson());
        using var refused = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/return-to-sender", new { reason = "x" }, etag);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("return-not-allowed", await TestApi.ProblemCodeAsync(refused));
        using var missingIfMatch = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/return-to-sender", new { reason = "x" }, null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingIfMatch.StatusCode);
    }

    [Fact]
    public async Task Attempt_and_exception_authorization_follow_hub_scope_and_roles()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        using var ank = await Client("hub.ank");
        using var izm = await Client("hub.izm");
        var (id, _) = await OutForDeliveryAsync(ops, ist, ank);

        using (var outOfScope = await AttemptAsync(izm, id, "Delivered"))
        {
            Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        }

        using (var originHub = await AttemptAsync(ist, id, "Delivered"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, originHub.StatusCode);
        }

        using (var queue = await ank.GetAsync(new Uri("/api/exceptions", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);
        }

        using var byOps = await AttemptAsync(ops, id, "Failed", "Other");
        Assert.Equal(HttpStatusCode.BadRequest, byOps.StatusCode); // Other için not zorunlu
        using var noKey = await ops.PostAsync(new Uri($"/api/shipments/{id}/delivery-attempts", UriKind.Relative),
            JsonContent.Create(new { outcome = "Delivered", occurredAt = Next().ToString("O") }), TestApi.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("idempotency-key-required", await TestApi.ProblemCodeAsync(noKey));
    }

    [Fact]
    public async Task Consolidation_rejection_opens_package_split_and_queue_pages_by_assignee()
    {
        using var ops = await Client("ops.agent");
        using var manager = await Client("ops.manager");
        using var ist = await Client("hub.ist");
        var queueUser = await db.UserIdAsync("ops.queue");
        var exceptionIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson());
            await Ok(TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", Next()));
            using (var rejected = await TestApi.ScanAsync(ist, "IST-01", labels[0], "Departure", Next()))
            {
                Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            }

            var split = await ExceptionAsync(ops, id, "PackageSplit");
            Assert.Equal("High", split.GetProperty("severity").GetString());
            var exceptionId = split.GetProperty("id").GetGuid();
            var (_, etag) = await QueueItemAsync(manager, exceptionId);
            using var assigned = await TestApi.PostCommandAsync(manager, $"/api/exceptions/{exceptionId}/assign", new { assigneeUserId = queueUser }, etag);
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            exceptionIds.Add(exceptionId);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            using var response = await manager.GetAsync(new Uri($"/api/exceptions?assignedTo={queueUser}&type=PackageSplit&limit=1"
                + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), UriKind.Relative), TestApi.Ct);
            var body = await TestApi.JsonAsync(response);
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : body.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(exceptionIds.Order(), seen.Order());
        Assert.Equal(3, seen.Distinct().Count());

        using var invalid = await manager.GetAsync(new Uri("/api/exceptions?type=Lost&overdue=maybe", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
