using System.Net;
using Lst.IntegrationTests.Infrastructure;

namespace Lst.IntegrationTests;

public sealed class AuthAndCsrfTests(LstDatabaseFixture db) : IDisposable
{
    private readonly LstApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Https_cookie_flow_anonymous_csrf_login_authenticated_csrf_create_and_replay()
    {
        using var client = _factory.CreateHttpsClient();
        var anonymous = await TestApi.GetAnonymousCsrfAsync(client);

        using (var login = await TestApi.PostLoginAsync(client, anonymous, db.User("ops.flow").UserName, db.User("ops.flow").Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
            var setCookie = string.Join("\n", login.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
            Assert.Contains("__host-lst.auth=", setCookie, StringComparison.Ordinal);
            Assert.Contains("secure", setCookie, StringComparison.Ordinal);
            Assert.Contains("httponly", setCookie, StringComparison.Ordinal);
            Assert.Contains("samesite=strict", setCookie, StringComparison.Ordinal);
            Assert.Contains("path=/", setCookie, StringComparison.Ordinal);
        }

        using var me = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("no-store", me.Headers.CacheControl?.ToString());
        var meBody = await TestApi.JsonAsync(me);
        var authenticatedToken = meBody.GetProperty("csrfToken").GetString()!;
        Assert.NotEqual(anonymous, authenticatedToken);
        Assert.Equal("OpsAgent", Assert.Single(meBody.GetProperty("roles").EnumerateArray()).GetString());

        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        using var first = await TestApi.CreateShipmentAsync(client, key, request.ToString(), csrfOverride: authenticatedToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var replay = await TestApi.CreateShipmentAsync(client, key, request.ToReorderedJson(), csrfOverride: authenticatedToken);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(TestApi.Ct), await replay.Content.ReadAsByteArrayAsync(TestApi.Ct));
    }

    [Fact]
    public async Task Login_without_or_with_invalid_csrf_is_rejected()
    {
        using var client = _factory.CreateHttpsClient();
        var user = db.User("ops.csrf");

        using var missing = await TestApi.PostLoginAsync(client, null, user.UserName, user.Password);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("csrf-validation-failed", await TestApi.ProblemCodeAsync(missing));

        await TestApi.GetAnonymousCsrfAsync(client); // cookie mevcut, header sahte
        using var invalid = await TestApi.PostLoginAsync(client, "not-a-valid-token", user.UserName, user.Password);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("csrf-validation-failed", await TestApi.ProblemCodeAsync(invalid));
        Assert.False(invalid.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("__Host-lst.auth=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Create_and_logout_require_authenticated_csrf_token()
    {
        using var client = _factory.CreateHttpsClient();
        var anonymous = await TestApi.GetAnonymousCsrfAsync(client);
        using (var login = await TestApi.PostLoginAsync(client, anonymous, db.User("ops.csrf").UserName, db.User("ops.csrf").Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        var request = new ShipmentJson();
        using var noToken = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), request.ToString(), omitCsrf: true);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal("csrf-validation-failed", await TestApi.ProblemCodeAsync(noToken));

        // Oturum öncesi (anonim) token, oturum açmış kullanıcı için geçersizdir.
        using var anonymousToken = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), request.ToString(), csrfOverride: anonymous);
        Assert.Equal(HttpStatusCode.BadRequest, anonymousToken.StatusCode);

        await using (var connection = db.OpenOwnerConnection())
        {
            Assert.Equal(0, await TestApi.CountAsync(connection,
                "SELECT count(*) FROM shipments.shipments WHERE external_reference = @r", ("r", request.ExternalReference)));
        }

        using (var logoutWithoutToken = await client.PostAsync(new Uri("/api/auth/logout", UriKind.Relative), null, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, logoutWithoutToken.StatusCode);
        }

        using var me = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), TestApi.Ct);
        var token = (await TestApi.JsonAsync(me)).GetProperty("csrfToken").GetString()!;
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add(TestApi.CsrfHeader, token);
        using (var loggedOut = await client.SendAsync(logout, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        }

        using var afterLogout = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task I9_unauthenticated_requests_get_401_problem_without_redirect()
    {
        using var client = _factory.CreateHttpsClient();
        using var create = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), new ShipmentJson().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Null(create.Headers.Location);
        Assert.Equal("unauthenticated", await TestApi.ProblemCodeAsync(create));

        using var detail = await client.GetAsync(new Uri($"/api/shipments/{Guid.NewGuid()}", UriKind.Relative), TestApi.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, detail.StatusCode);
        Assert.Null(detail.Headers.Location);
        Assert.Equal("unauthenticated", await TestApi.ProblemCodeAsync(detail));
    }

    [Fact]
    public async Task I10_hub_operator_cannot_create_shipment()
    {
        using var client = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(client, db.User("hub.ist"));
        using var response = await TestApi.CreateShipmentAsync(client, TestApi.NewKey(), new ShipmentJson().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await TestApi.ProblemCodeAsync(response));
    }

    private const string LoginFailedSql =
        "SELECT count(*) FROM platform.audit_entries WHERE action = 'LoginFailed' AND entity_type = 'User' AND entity_id = @e " +
        "AND actor_type = 'Anonymous' AND actor_id = 'anonymous' AND outcome = 'Rejected' AND reason_code = @r AND changes = '{}'::jsonb";

    private static async Task<long> CountLoginFailedAsync(LstDatabaseFixture db, string entityId, string reason)
    {
        await using var connection = db.OpenOwnerConnection();
        return await TestApi.CountAsync(connection, LoginFailedSql, ("e", entityId), ("r", reason));
    }

    [Fact]
    public async Task Login_failure_is_generic_and_audited_without_credentials()
    {
        const string unknownUserName = "no.such.user.audit-probe";
        const string wrongPassword = "Wrong!Password-audit-probe-123";
        var wrongPwUser = db.User("ops.wrongpw");
        var wrongPwUserId = (await db.UserIdAsync(wrongPwUser.UserName)).ToString();
        var unknownBefore = await CountLoginFailedAsync(db, "unknown", "invalid-credentials");
        var knownBefore = await CountLoginFailedAsync(db, wrongPwUserId, "invalid-credentials");

        using var client = _factory.CreateHttpsClient();
        var token = await TestApi.GetAnonymousCsrfAsync(client);
        using var unknown = await TestApi.PostLoginAsync(client, token, unknownUserName, wrongPassword);
        using var wrong = await TestApi.PostLoginAsync(client, token, wrongPwUser.UserName, wrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var a = await TestApi.JsonAsync(unknown);
        var b = await TestApi.JsonAsync(wrong);
        foreach (var field in new[] { "type", "title", "detail", "code", "status" })
        {
            Assert.Equal(a.GetProperty(field).ToString(), b.GetProperty(field).ToString());
        }

        Assert.Equal(unknownBefore + 1, await CountLoginFailedAsync(db, "unknown", "invalid-credentials"));
        Assert.Equal(knownBefore + 1, await CountLoginFailedAsync(db, wrongPwUserId, "invalid-credentials"));

        await using var connection = db.OpenOwnerConnection();
        Assert.Equal(0, await TestApi.CountAsync(connection,
            "SELECT count(*) FROM platform.audit_entries WHERE concat_ws('|', actor_id, entity_id, reason_code, correlation_id, changes::text) LIKE '%audit-probe%'"));
    }

    [Fact]
    public async Task Lockout_returns_the_same_generic_response_and_is_audited()
    {
        var user = db.User("ops.lockout");
        var userId = (await db.UserIdAsync(user.UserName)).ToString();
        using var client = _factory.CreateHttpsClient();
        var token = await TestApi.GetAnonymousCsrfAsync(client);

        for (var i = 0; i < 5; i++)
        {
            using var failed = await TestApi.PostLoginAsync(client, token, user.UserName, "Wrong!Password123");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Doğru parola da kilit süresince reddedilir; yanıt hatalı parola ile aynıdır.
        using var locked = await TestApi.PostLoginAsync(client, token, user.UserName, user.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("invalid-credentials", await TestApi.ProblemCodeAsync(locked));
        Assert.False(locked.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("__Host-lst.auth=", StringComparison.Ordinal)));

        Assert.Equal(4, await CountLoginFailedAsync(db, userId, "invalid-credentials"));
        Assert.Equal(2, await CountLoginFailedAsync(db, userId, "locked-out"));
    }
}
