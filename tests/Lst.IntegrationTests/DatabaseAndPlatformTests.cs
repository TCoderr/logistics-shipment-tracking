using System.Net;
using Lst.IntegrationTests.Infrastructure;
using Npgsql;

namespace Lst.IntegrationTests;

public sealed class DatabaseAndPlatformTests(LstDatabaseFixture db) : IDisposable
{
    private readonly LstApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> CreateShipmentAsync()
    {
        using var client = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("ops.manager"));
        using var created = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), new ShipmentJson().ToString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await TestApi.JsonAsync(created)).GetProperty("id").GetGuid();
    }

    private static async Task<PostgresException> ViolationAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestApi.Ct));
    }

    [Fact]
    public async Task I15_database_constraints_reject_invalid_rows()
    {
        var id = await CreateShipmentAsync();
        await using var connection = db.OpenOwnerConnection();

        var format = await ViolationAsync(connection, "UPDATE shipments.shipments SET tracking_number = 'XX123' WHERE id = @id", id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, format.SqlState);
        Assert.Equal("ck_shipments_tracking_number_format", format.ConstraintName);

        var sequence = await ViolationAsync(connection, "UPDATE shipments.packages SET sequence = 1 WHERE shipment_id = @id AND sequence = 2", id);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, sequence.SqlState);
        Assert.Equal("ux_packages_shipment_sequence", sequence.ConstraintName);

        var pair = await ViolationAsync(connection, "UPDATE shipments.shipments SET declared_value_currency = NULL WHERE id = @id", id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, pair.SqlState);
        Assert.Equal("ck_shipments_declared_value_pair", pair.ConstraintName);

        var status = await ViolationAsync(connection, "UPDATE shipments.shipments SET status = 'Lost' WHERE id = @id", id);
        Assert.Equal("ck_shipments_status", status.ConstraintName);
    }

    [Fact]
    public async Task Runtime_role_cannot_update_or_delete_audit_entries()
    {
        await CreateShipmentAsync();
        await using var runtime = db.OpenRuntimeConnection();
        foreach (var sql in new[]
                 {
                     "UPDATE platform.audit_entries SET action = action",
                     "DELETE FROM platform.audit_entries",
                     "TRUNCATE platform.audit_entries",
                 })
        {
            await using var command = new NpgsqlCommand(sql, runtime);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestApi.Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await using var select = new NpgsqlCommand("SELECT count(*) FROM platform.audit_entries", runtime);
        Assert.True((long)(await select.ExecuteScalarAsync(TestApi.Ct))! > 0);
    }

    [Fact]
    public async Task Incomplete_idempotency_claim_cannot_be_committed()
    {
        await using var connection = db.OpenOwnerConnection();
        await using var transaction = await connection.BeginTransactionAsync(TestApi.Ct);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO platform.idempotency_records (id, actor_scope, endpoint, idempotency_key, request_hash, request_hash_version, created_at, expires_at)
            VALUES (@id, 'user:test', 'POST /api/shipments', @key, decode(repeat('00', 32), 'hex'), 1, now(), now() + interval '1 hour')
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("key", TestApi.NewKey());
            await insert.ExecuteNonQueryAsync(TestApi.Ct);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync(TestApi.Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task I16_ready_health_check_reports_healthy_with_migrated_database()
    {
        using var client = _factory.CreateHttpsClient();
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(TestApi.Ct));

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task M2_openapi_document_is_published_in_development_and_lists_slice_endpoints()
    {
        using var development = new LstApiFactory(db, environment: "Development");
        using var client = development.CreateHttpsClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadAsStringAsync(TestApi.Ct);
        foreach (var path in new[] { "/api/auth/csrf", "/api/auth/login", "/api/auth/logout", "/api/auth/me", "/api/shipments", "/api/shipments/{id}" })
        {
            Assert.Contains($"\"{path}\"", document, StringComparison.Ordinal);
        }

        using var testing = _factory.CreateHttpsClient();
        using var hidden = await testing.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }
}
