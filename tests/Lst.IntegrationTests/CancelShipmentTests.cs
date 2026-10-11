using System.Net;
using System.Text.Json;
using Lst.IntegrationTests.Infrastructure;

namespace Lst.IntegrationTests;

/// <summary>Dilim 4: operatör iptali (If-Match, çıkış sonrası red, eşzamanlı ETag, allowedActions).</summary>
public sealed class CancelShipmentTests(LstDatabaseFixture db) : IDisposable
{
    private readonly LstApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> Client(string user)
    {
        var client = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User(user));
        return client;
    }

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid id, string? etag, string reason = "Gönderici siparişi iptal etti") =>
        TestApi.PostCommandAsync(client, $"/api/shipments/{id}/cancel", new { reason }, etag);

    [Fact]
    public async Task Created_shipment_is_cancelled_with_matching_etag_and_audited_without_reason_text()
    {
        using var ops = await Client("ops.agent");
        var (id, _, etag) = await TestApi.CreateAsync(ops, new ShipmentJson());
        var (detail, detailETag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(etag, detailETag);
        Assert.Equal(["Cancel"], detail.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));

        const string reason = "Gizli müşteri notu 12345";
        using var response = await CancelAsync(ops, id, etag, reason);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestApi.JsonAsync(response);
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        var newETag = response.Headers.ETag!.ToString();
        Assert.NotEqual(etag, newETag);

        var (after, afterETag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(newETag, afterETag);
        Assert.Empty(after.GetProperty("allowedActions").EnumerateArray());
        Assert.Contains(after.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "Cancelled");

        await using var connection = db.OpenOwnerConnection();
        Assert.Equal(1, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM platform.audit_entries WHERE entity_id = @id AND action = 'ShipmentCancelled' AND changes->>'reasonProvided' = 'true'", ("id", id.ToString())));
        Assert.Equal(0, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM platform.audit_entries WHERE entity_id = @id AND changes::text LIKE '%12345%'", ("id", id.ToString())));
    }

    [Fact]
    public async Task Missing_or_stale_if_match_is_rejected()
    {
        using var ops = await Client("ops.agent");
        var (id, _, etag) = await TestApi.CreateAsync(ops, new ShipmentJson());

        using (var missing = await CancelAsync(ops, id, null))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
            Assert.Equal("precondition-required", await TestApi.ProblemCodeAsync(missing));
        }

        using (var stale = await CancelAsync(ops, id, "\"1\""))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            Assert.Equal(etag, stale.Headers.ETag!.ToString());
        }

        using var empty = await TestApi.PostCommandAsync(ops, $"/api/shipments/{id}/cancel", new { reason = " " }, etag);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Cancel_after_departure_is_refused_with_return_to_sender_hint()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        var (id, labels, _) = await TestApi.CreateAsync(ops, new ShipmentJson(packageCount: 1));
        var at = DateTimeOffset.UtcNow.AddSeconds(5);
        using (var arrival = await TestApi.ScanAsync(ist, "IST-01", labels[0], "Arrival", at))
        {
            Assert.Equal(HttpStatusCode.OK, arrival.StatusCode);
        }

        var (atHub, atHubETag) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal(["Cancel"], atHub.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));

        using (var departure = await TestApi.ScanAsync(ist, "IST-01", labels[0], "Departure", at.AddSeconds(10)))
        {
            Assert.Equal(HttpStatusCode.OK, departure.StatusCode);
        }

        using (var oldETag = await CancelAsync(ops, id, atHubETag))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, oldETag.StatusCode);
        }

        var (inTransit, etag) = await TestApi.GetDetailAsync(ops, id);
        // Çıkış sonrası iptal yok; durdurma talebi göndericiye iadedir (Dilim 5).
        Assert.Equal(["ReturnToSender"], inTransit.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        using var refused = await CancelAsync(ops, id, etag);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await TestApi.JsonAsync(refused);
        Assert.Equal("shipment-not-cancellable", problem.GetProperty("code").GetString());
        Assert.Equal("InTransit", problem.GetProperty("currentStatus").GetString());
        Assert.True(problem.GetProperty("returnToSenderAllowed").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_cancels_with_same_etag_produce_one_success_and_one_412()
    {
        using var ops1 = await Client("ops.agent");
        using var ops2 = await Client("ops.manager");
        var (id, _, etag) = await TestApi.CreateAsync(ops1, new ShipmentJson());

        using var barrier = new Barrier(2);
        var responses = await Task.WhenAll(new[] { ops1, ops2 }.Select(c => Task.Run(async () =>
        {
            barrier.SignalAndWait(TestApi.Ct);
            return await CancelAsync(c, id, etag);
        })));
        var codes = responses.Select(r => r.StatusCode).Order().ToList();
        foreach (var r in responses)
        {
            r.Dispose();
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], codes);
        await using var connection = db.OpenOwnerConnection();
        Assert.Equal(1, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id AND event_type = 'Cancelled'", ("id", id)));
    }

    [Fact]
    public async Task Hub_operator_cannot_cancel_and_sees_no_cancel_action()
    {
        using var ops = await Client("ops.agent");
        using var ist = await Client("hub.ist");
        var (id, _, etag) = await TestApi.CreateAsync(ops, new ShipmentJson());
        var (detail, _) = await TestApi.GetDetailAsync(ist, id);
        Assert.Empty(detail.GetProperty("allowedActions").EnumerateArray());

        using var response = await CancelAsync(ist, id, etag);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var (after, _) = await TestApi.GetDetailAsync(ops, id);
        Assert.Equal("Created", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_shipment_cancel_is_404()
    {
        using var ops = await Client("ops.agent");
        using var response = await CancelAsync(ops, Guid.NewGuid(), "\"1\"");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(JsonValueKind.Object, (await TestApi.JsonAsync(response)).ValueKind);
    }
}
