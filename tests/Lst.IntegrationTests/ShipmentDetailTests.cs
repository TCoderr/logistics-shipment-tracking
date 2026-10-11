using System.Net;
using Lst.IntegrationTests.Infrastructure;

namespace Lst.IntegrationTests;

public sealed class ShipmentDetailTests(LstDatabaseFixture db) : IDisposable
{
    private readonly LstApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task I11_detail_access_is_scoped_by_current_hub_assignments_and_phone_visibility()
    {
        using var ops = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(ops, db.User("ops.agent"));
        var request = new ShipmentJson(origin: "IST-01", destination: "ANK-02");
        using var created = await TestApi.CreateShipmentAsync(ops, TestApi.NewKey(), request.ToString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!;

        using var izm = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(izm, db.User("hub.izm"));
        using (var outOfScope = await izm.GetAsync(location, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
            Assert.Equal("not-found", await TestApi.ProblemCodeAsync(outOfScope));
        }

        using (var missing = await izm.GetAsync(new Uri($"/api/shipments/{Guid.NewGuid()}", UriKind.Relative), TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using var ist = _factory.CreateHttpsClient();
        await TestApi.LoginAsync(ist, db.User("hub.ist"));
        using var hubView = await ist.GetAsync(location, TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, hubView.StatusCode);
        Assert.Equal(created.Headers.ETag, hubView.Headers.ETag);
        Assert.Equal("no-store", hubView.Headers.CacheControl?.ToString());
        var hubBody = await TestApi.JsonAsync(hubView);
        var maskedPhone = hubBody.GetProperty("recipient").GetProperty("phone").GetString()!;
        Assert.Equal(request.RecipientPhone[..6] + "*****" + request.RecipientPhone[^2..], maskedPhone);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, hubBody.GetProperty("sender").GetProperty("phone").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, hubBody.GetProperty("sender").GetProperty("addressLine1").ValueKind);
        Assert.Equal("Created", hubBody.GetProperty("status").GetString());

        using var opsView = await ops.GetAsync(location, TestApi.Ct);
        Assert.Equal(HttpStatusCode.OK, opsView.StatusCode);
        var opsBody = await TestApi.JsonAsync(opsView);
        Assert.Equal(request.RecipientPhone, opsBody.GetProperty("recipient").GetProperty("phone").GetString());
        Assert.Equal("+902124445566", opsBody.GetProperty("sender").GetProperty("phone").GetString());

        var timeline = opsBody.GetProperty("timeline");
        var created0 = Assert.Single(timeline.EnumerateArray());
        Assert.Equal("Created", created0.GetProperty("eventType").GetString());
        Assert.Equal("Operator", created0.GetProperty("source").GetString());
        Assert.Equal("Applied", created0.GetProperty("outcome").GetString());
        Assert.Equal(0, opsBody.GetProperty("openExceptions").GetArrayLength());
        // Dilim 4: allowedActions gerçek durum + güncel roldan türetilir; Created gönderide Ops için yalnız iptal.
        Assert.Equal(["Cancel"], opsBody.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(0, hubBody.GetProperty("allowedActions").GetArrayLength());
        Assert.Equal("IST-01", opsBody.GetProperty("originHub").GetProperty("code").GetString());
        Assert.Equal(3, opsBody.GetProperty("deliveryAttempts").GetProperty("max").GetInt32());
    }
}
