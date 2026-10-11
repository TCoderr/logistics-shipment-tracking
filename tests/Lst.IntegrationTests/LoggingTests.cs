using System.Net;
using Lst.IntegrationTests.Infrastructure;

namespace Lst.IntegrationTests;

public sealed class LoggingTests(LstDatabaseFixture db)
{
    [Fact]
    public async Task I14_logs_do_not_contain_pii_passwords_tokens_or_connection_secrets()
    {
        using var factory = new LstApiFactory(db);
        using var client = factory.CreateHttpsClient();
        var user = db.User("ops.logs");
        var session = await TestApi.LoginAsync(client, user);

        var request = new ShipmentJson();
        var key = TestApi.NewKey();
        using var created = await TestApi.CreateShipmentAsync(client, key, request.ToString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var publicCode = (await TestApi.JsonAsync(created)).GetProperty("publicTrackingCode").GetString()!;
        using (var detail = await client.GetAsync(created.Headers.Location, TestApi.Ct))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        }

        using (var replay = await TestApi.CreateShipmentAsync(client, key, request.ToString()))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        }

        var forbidden = request.PiiValues
            .Append(user.Password)
            .Append(session.CsrfToken)
            .Append(publicCode)
            .Append("lojistik@test.example")
            .ToList();

        var entries = factory.Logs.Entries;
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            foreach (var value in forbidden)
            {
                Assert.DoesNotContain(value, entry, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("Password=", entry, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("__Host-lst.auth=", entry, StringComparison.Ordinal);
        }
    }
}
