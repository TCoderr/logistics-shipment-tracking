using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lst.IntegrationTests.Infrastructure;
using Lst.Modules.Identity;
using Lst.Modules.Shipments.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;

namespace Lst.IntegrationTests;

public sealed class CreateShipmentIdempotencyTests(LstDatabaseFixture db) : IDisposable
{
    private readonly LstApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> LoggedInClientAsync(string userName, LstApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User(userName));
        return client;
    }

    private async Task<long> CountShipmentsAsync(string externalReference)
    {
        await using var connection = db.OpenOwnerConnection();
        return await TestApi.CountAsync(connection,
            "SELECT count(*) FROM shipments.shipments WHERE external_reference = @r", ("r", externalReference));
    }

    [Fact]
    public async Task I1_replay_returns_identical_bytes_location_etag_and_creates_nothing_new()
    {
        using var client = await LoggedInClientAsync("ops.agent");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();

        using var first = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());

        using var replay = await TestApi.CreateShipmentAsync(client, key, request.ToReorderedJson());
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));

        var firstBytes = await first.Content.ReadAsByteArrayAsync(TestApi.Ct);
        Assert.Equal(firstBytes, await replay.Content.ReadAsByteArrayAsync(TestApi.Ct));
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(first.Content.Headers.ContentType, replay.Content.Headers.ContentType);

        var body = await TestApi.JsonAsync(first);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/shipments/{id}", first.Headers.Location!.OriginalString);
        Assert.Equal(2, body.GetProperty("packages").GetArrayLength());

        using var detail = await client.GetAsync(first.Headers.Location, TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(first.Headers.ETag, detail.Headers.ETag);

        await using var connection = db.OpenOwnerConnection();
        Assert.Equal(1, await CountShipmentsAsync(request.ExternalReference));
        Assert.Equal(2, await TestApi.CountAsync(connection, "SELECT count(*) FROM shipments.packages WHERE shipment_id = @id", ("id", id)));
        Assert.Equal(1, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id AND event_type = 'Created'", ("id", id)));
        Assert.Equal(1, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM platform.audit_entries WHERE entity_id = @id AND action = 'ShipmentCreated'", ("id", id.ToString())));
        var etagFromDb = await TestApi.ScalarAsync(connection, "SELECT xmin::text FROM shipments.shipments WHERE id = @id", ("id", id));
        Assert.Equal($"\"{etagFromDb}\"", first.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task I2_same_key_with_different_body_is_rejected()
    {
        using var client = await LoggedInClientAsync("ops.agent");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        using var first = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var changed = request.With(n => n["recipient"]!["address"]!["postalCode"] = "06421").ToString();
        using var second = await TestApi.CreateShipmentAsync(client, key, changed);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("idempotency-key-reused", await TestApi.ProblemCodeAsync(second));
        Assert.Equal(1, await CountShipmentsAsync(request.ExternalReference));
    }

    [Fact]
    public async Task Equivalent_declared_value_representation_replays_instead_of_conflicting()
    {
        using var client = await LoggedInClientAsync("ops.agent");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        using var first = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var integerAmount = request.ToString().Replace("\"amount\":1850.00", "\"amount\":1850", StringComparison.Ordinal);
        Assert.NotEqual(request.ToString(), integerAmount);
        using var second = await TestApi.CreateShipmentAsync(client, key, integerAmount);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task I3_ten_parallel_requests_with_same_key_create_exactly_one_shipment()
    {
        using var client = await LoggedInClientAsync("ops.parallel");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        var json = request.ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => TestApi.CreateShipmentAsync(client, key, json)));
        try
        {
            Assert.All(responses, r => Assert.True(
                r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Beklenmeyen durum: {r.StatusCode}"));
            var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
            Assert.NotEmpty(created);
            var ids = new HashSet<Guid>();
            foreach (var r in created)
            {
                ids.Add((await TestApi.JsonAsync(r)).GetProperty("id").GetGuid());
            }

            Assert.Single(ids);

            foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                Assert.Equal("idempotency-request-in-progress", await TestApi.ProblemCodeAsync(conflict));
                using var retry = await TestApi.CreateShipmentAsync(client, key, json);
                Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
                Assert.Equal(ids.Single(), (await TestApi.JsonAsync(retry)).GetProperty("id").GetGuid());
            }
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }

        Assert.Equal(1, await CountShipmentsAsync(request.ExternalReference));
    }

    [Fact]
    public async Task I4_different_actors_with_same_key_get_separate_shipments()
    {
        using var clientA = await LoggedInClientAsync("ops.agent");
        using var clientB = await LoggedInClientAsync("ops.agent2");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();

        using var a = await TestApi.CreateShipmentAsync(clientA, key, request.ToString());
        using var b = await TestApi.CreateShipmentAsync(clientB, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains("Idempotency-Replayed"));
        Assert.NotEqual((await TestApi.JsonAsync(a)).GetProperty("id").GetGuid(), (await TestApi.JsonAsync(b)).GetProperty("id").GetGuid());
        Assert.Equal(2, await CountShipmentsAsync(request.ExternalReference));
    }

    [Fact]
    public async Task I5_validation_failure_is_not_stored_and_corrected_body_succeeds_with_same_key()
    {
        using var client = await LoggedInClientAsync("ops.agent");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        var invalid = new ShipmentJson().With(n => n["recipient"]!["phone"] = "05321234567").ToString();

        using var first = await TestApi.CreateShipmentAsync(client, key, invalid);
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal("validation", await TestApi.ProblemCodeAsync(first));
        Assert.True((await TestApi.JsonAsync(first)).GetProperty("errors").TryGetProperty("recipient.phone", out _));

        using var second = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task I6_expired_key_creates_a_new_shipment()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new LstApiFactory(db, clock);
        using var client = await LoggedInClientAsync("ops.expiry", factory);
        var request = new ShipmentJson();
        var key = TestApi.NewKey();

        using var first = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await TestApi.JsonAsync(first)).GetProperty("id").GetGuid();

        clock.Advance(TimeSpan.FromHours(49));
        await TestApi.LoginAsync(client, db.User("ops.expiry")); // 8 saatlik cookie de sahte saatle doldu
        using var second = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(second.Headers.Contains("Idempotency-Replayed"));
        Assert.NotEqual(firstId, (await TestApi.JsonAsync(second)).GetProperty("id").GetGuid());
        Assert.Equal(2, await CountShipmentsAsync(request.ExternalReference));
    }

    [Fact]
    public async Task Ten_parallel_requests_on_expired_key_create_exactly_one_new_shipment()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new LstApiFactory(db, clock);
        using var client = await LoggedInClientAsync("ops.expiry10", factory);
        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        var json = request.ToString();

        using var first = await TestApi.CreateShipmentAsync(client, key, json);
        var oldId = (await TestApi.JsonAsync(first)).GetProperty("id").GetGuid();
        clock.Advance(TimeSpan.FromHours(49));
        await TestApi.LoginAsync(client, db.User("ops.expiry10"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => TestApi.CreateShipmentAsync(client, key, json)));
        var newIds = new HashSet<Guid>();
        try
        {
            foreach (var r in responses)
            {
                Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Beklenmeyen durum: {r.StatusCode}");
                if (r.StatusCode == HttpStatusCode.Created)
                {
                    newIds.Add((await TestApi.JsonAsync(r)).GetProperty("id").GetGuid());
                }
            }
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }

        var newId = Assert.Single(newIds);
        Assert.NotEqual(oldId, newId);
        using var retry = await TestApi.CreateShipmentAsync(client, key, json);
        Assert.Equal(newId, (await TestApi.JsonAsync(retry)).GetProperty("id").GetGuid());
        Assert.Equal(2, await CountShipmentsAsync(request.ExternalReference));
    }

    [Fact]
    public async Task I7_audit_write_failure_rolls_back_everything()
    {
        var userId = await db.UserIdAsync("ops.auditfail");
        await using (var owner = db.OpenOwnerConnection())
        {
            await using var create = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION platform.test_fail_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.actor_id = '{userId}' THEN RAISE EXCEPTION 'forced audit failure (test)'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_test_fail_audit BEFORE INSERT ON platform.audit_entries
                    FOR EACH ROW EXECUTE FUNCTION platform.test_fail_audit();
                """, owner);
            await create.ExecuteNonQueryAsync(TestApi.Ct);
        }

        try
        {
            using var client = await LoggedInClientAsync("ops.auditfail");
            var request = new ShipmentJson();
            var key = TestApi.NewKey();
            using var response = await TestApi.CreateShipmentAsync(client, key, request.ToString());
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("internal-error", await TestApi.ProblemCodeAsync(response));

            await using var connection = db.OpenOwnerConnection();
            Assert.Equal(0, await CountShipmentsAsync(request.ExternalReference));
            Assert.Equal(0, await TestApi.CountAsync(connection,
                "SELECT count(*) FROM platform.idempotency_records WHERE idempotency_key = @k", ("k", key)));
            Assert.Equal(0, await TestApi.CountAsync(connection,
                "SELECT count(*) FROM shipments.tracking_events WHERE actor_user_id = @u", ("u", userId)));
            Assert.Equal(0, await TestApi.CountAsync(connection,
                "SELECT count(*) FROM platform.audit_entries WHERE actor_id = @u", ("u", userId.ToString())));
        }
        finally
        {
            await using var owner = db.OpenOwnerConnection();
            await using var drop = new NpgsqlCommand(
                "DROP TRIGGER IF EXISTS trg_test_fail_audit ON platform.audit_entries; DROP FUNCTION IF EXISTS platform.test_fail_audit();", owner);
            await drop.ExecuteNonQueryAsync(TestApi.Ct);
        }
    }

    [Fact]
    public async Task I8_contract_errors_for_key_hubs_and_validation()
    {
        using var client = await LoggedInClientAsync("ops.agent");

        using (var noKey = await TestApi.CreateShipmentAsync(client, null, new ShipmentJson().ToString()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
            Assert.Equal("idempotency-key-required", await TestApi.ProblemCodeAsync(noKey));
        }

        using (var badKey = await TestApi.CreateShipmentAsync(client, "short", new ShipmentJson().ToString()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
            Assert.Equal("idempotency-key-invalid", await TestApi.ProblemCodeAsync(badKey));
        }

        using (var unknownHub = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), new ShipmentJson(origin: "XXX-99").ToString()))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownHub.StatusCode);
            Assert.Equal("unknown-hub", await TestApi.ProblemCodeAsync(unknownHub));
        }

        using (var inactiveHub = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), new ShipmentJson(destination: "BRS-01").ToString()))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, inactiveHub.StatusCode);
            Assert.Equal("hub-inactive", await TestApi.ProblemCodeAsync(inactiveHub));
        }

        using (var tooMany = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), new ShipmentJson(packageCount: 21).ToString()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.Equal("validation", await TestApi.ProblemCodeAsync(tooMany));
            Assert.True((await TestApi.JsonAsync(tooMany)).GetProperty("errors").TryGetProperty("packages", out _));
        }

        var duplicateJson = new ShipmentJson().ToString().Replace("\"serviceLevel\":\"Standard\"", "\"serviceLevel\":\"Standard\",\"serviceLevel\":\"Express\"", StringComparison.Ordinal);
        using (var duplicate = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), duplicateJson))
        {
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            Assert.Equal("validation", await TestApi.ProblemCodeAsync(duplicate));
        }

        using (var unknownField = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), new ShipmentJson().With(n => n["priority"] = "High").ToString()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        }

        var oversized = new ShipmentJson().With(n => n["externalReference"] = new string('x', 70_000)).ToString();
        using (var tooLarge = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), oversized))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
            Assert.Equal("payload-too-large", await TestApi.ProblemCodeAsync(tooLarge));
        }
    }

    [Fact]
    public async Task I13_stored_idempotency_response_contains_no_pii()
    {
        using var client = await LoggedInClientAsync("ops.agent");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        using var response = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var connection = db.OpenOwnerConnection();
        var stored = (byte[])(await TestApi.ScalarAsync(connection,
            "SELECT response_body FROM platform.idempotency_records WHERE idempotency_key = @k", ("k", key)))!;
        var text = Encoding.UTF8.GetString(stored);
        Assert.Equal(await response.Content.ReadAsByteArrayAsync(TestApi.Ct), stored);
        foreach (var pii in request.PiiValues.Append("Test Tekstil").Append("+902124445566").Append("lojistik@test.example"))
        {
            Assert.DoesNotContain(pii, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Concurrent_uncommitted_claim_times_out_with_409_then_succeeds_after_release()
    {
        using var client = await LoggedInClientAsync("ops.lock");
        var userId = await db.UserIdAsync("ops.lock");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();

        await using var holder = db.OpenOwnerConnection();
        await using var transaction = await holder.BeginTransactionAsync(TestApi.Ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO platform.idempotency_records (id, actor_scope, endpoint, idempotency_key, request_hash, request_hash_version, created_at, expires_at)
            VALUES (@id, @scope, 'POST /api/shipments', @key, @hash, 1, now(), now() + interval '1 hour')
            """, holder, transaction))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("scope", $"user:{userId}");
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = new byte[32] });
            await insert.ExecuteNonQueryAsync(TestApi.Ct);
        }

        using (var blocked = await TestApi.CreateShipmentAsync(client, key, request.ToString()))
        {
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal("idempotency-request-in-progress", await TestApi.ProblemCodeAsync(blocked));
            Assert.Equal("1", blocked.Headers.RetryAfter?.ToString());
        }

        await transaction.RollbackAsync(TestApi.Ct);

        using var released = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, released.StatusCode);
        Assert.Equal(1, await CountShipmentsAsync(request.ExternalReference));
    }

    [Fact]
    public async Task Actor_whose_role_was_removed_cannot_replay_previous_success()
    {
        using var client = await LoggedInClientAsync("ops.revoked");
        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        using (var first = await TestApi.CreateShipmentAsync(client, key, request.ToString()))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        await db.RemoveRoleAsync("ops.revoked", Roles.OpsAgent);

        using var replay = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.NotEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.True(replay.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"Durum: {replay.StatusCode}");
        Assert.False(replay.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task Tracking_number_collision_is_retried_after_savepoint_without_extra_rows()
    {
        var existing = TrackingNumbers.FromPayload(Random.Shared.Next(0, 999_999_999));
        var fresh = TrackingNumbers.FromPayload(Random.Shared.Next(0, 999_999_999));
        var generator = new ScriptedCodeGenerator(
            trackingNumbers: [existing, existing, fresh],
            publicCodes: [ScriptedCodeGenerator.RandomPublicCode(), ScriptedCodeGenerator.RandomPublicCode(), ScriptedCodeGenerator.RandomPublicCode()]);
        await AssertCollisionRecoveredAsync(generator, expectedTrackingNumber: fresh);
    }

    [Fact]
    public async Task Public_code_collision_is_retried_after_savepoint_without_extra_rows()
    {
        var sharedCode = ScriptedCodeGenerator.RandomPublicCode();
        var t1 = TrackingNumbers.FromPayload(Random.Shared.Next(0, 999_999_999));
        var t2 = TrackingNumbers.FromPayload(Random.Shared.Next(0, 999_999_999));
        var t3 = TrackingNumbers.FromPayload(Random.Shared.Next(0, 999_999_999));
        var generator = new ScriptedCodeGenerator(
            trackingNumbers: [t1, t2, t3],
            publicCodes: [sharedCode, sharedCode, ScriptedCodeGenerator.RandomPublicCode()]);
        await AssertCollisionRecoveredAsync(generator, expectedTrackingNumber: t3);
    }

    private async Task AssertCollisionRecoveredAsync(ScriptedCodeGenerator generator, string expectedTrackingNumber)
    {
        using var factory = new LstApiFactory(db, configureServices: s => s.AddSingleton<IShipmentCodeGenerator>(generator));
        using var client = await LoggedInClientAsync("ops.collide", factory);
        var userId = await db.UserIdAsync("ops.collide");

        var first = new ShipmentJson();
        using (var a = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), first.ToString()))
        {
            Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        }

        await using var connection = db.OpenOwnerConnection();
        var auditBefore = await TestApi.CountAsync(connection, "SELECT count(*) FROM platform.audit_entries WHERE actor_id = @u", ("u", userId.ToString()));

        var second = new ShipmentJson();
        var key = TestApi.NewKey();
        using var b = await TestApi.CreateShipmentAsync(client, key, second.ToString());
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        var body = await TestApi.JsonAsync(b);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(expectedTrackingNumber, body.GetProperty("trackingNumber").GetString());

        Assert.Equal(1, await CountShipmentsAsync(second.ExternalReference));
        Assert.Equal(2, await TestApi.CountAsync(connection, "SELECT count(*) FROM shipments.packages WHERE shipment_id = @id", ("id", id)));
        Assert.Equal(1, await TestApi.CountAsync(connection, "SELECT count(*) FROM shipments.tracking_events WHERE shipment_id = @id", ("id", id)));
        Assert.Equal(1, await TestApi.CountAsync(connection, "SELECT count(*) FROM platform.idempotency_records WHERE idempotency_key = @k", ("k", key)));
        Assert.Equal(auditBefore + 1, await TestApi.CountAsync(connection, "SELECT count(*) FROM platform.audit_entries WHERE actor_id = @u", ("u", userId.ToString())));
        Assert.Equal(0, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM shipments.shipments s LEFT JOIN shipments.tracking_events e ON e.shipment_id = s.id WHERE s.created_by_user_id = @u AND e.id IS NULL",
            ("u", userId)));
    }

    private sealed class ScriptedCodeGenerator(IEnumerable<string> trackingNumbers, IEnumerable<string> publicCodes) : IShipmentCodeGenerator
    {
        private readonly Queue<string> _trackingNumbers = new(trackingNumbers);
        private readonly Queue<string> _publicCodes = new(publicCodes);
        private readonly RandomShipmentCodeGenerator _fallback = new();

        public static string RandomPublicCode() => new RandomShipmentCodeGenerator().NextPublicTrackingCode();

        public string NextTrackingNumber()
        {
            lock (_trackingNumbers)
            {
                return _trackingNumbers.Count > 0 ? _trackingNumbers.Dequeue() : _fallback.NextTrackingNumber();
            }
        }

        public string NextPublicTrackingCode()
        {
            lock (_publicCodes)
            {
                return _publicCodes.Count > 0 ? _publicCodes.Dequeue() : _fallback.NextPublicTrackingCode();
            }
        }
    }
}
