using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lst.Modules.Tracking.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Lst.IntegrationTests.Infrastructure;

/// <summary>İmzalı SIMEX olayı gönderme ve yalnız verilen makbuzları işleme (testler birbirinin makbuzuna dokunmaz).</summary>
public static class CarrierHelper
{
    public static async Task AssignSimexAsync(HttpClient ops, Guid shipmentId)
    {
        var (_, etag) = await TestApi.GetDetailAsync(ops, shipmentId);
        using var response = await TestApi.PostCommandAsync(ops, $"/api/shipments/{shipmentId}/carrier", new { carrierCode = TestCarrier.Code }, etag);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(TestApi.Ct)}");
    }

    public static async Task<Guid> SendAsync(
        HttpClient client, string trackingNumber, string type, DateTimeOffset occurredAt, DateTimeOffset signedAt, object? attempt = null, string facility = "ANK-SX3")
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["eventId"] = "it-" + Guid.NewGuid().ToString("N"),
            ["carrierCode"] = TestCarrier.Code,
            ["trackingNumber"] = trackingNumber,
            ["eventType"] = type,
            ["occurredAt"] = occurredAt.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["location"] = new { facilityCode = facility, city = "Ankara" },
            ["attempt"] = attempt,
        });
        var t = signedAt.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/carrier-webhooks/{TestCarrier.Code}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(CarrierWebhookParser.SignatureHeader, $"t={t},v1={CarrierWebhookParser.Sign(TestCarrier.Secret, t, body)}");
        using var response = await client.SendAsync(request, TestApi.Ct);
        var json = await TestApi.JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {json}");
        return json.GetProperty("receiptId").GetGuid();
    }

    /// <summary>Makbuzu işler ve sonucu döner (Applied/Stale/...).</summary>
    public static async Task<string> ProcessAsync(LstApiFactory factory, LstDatabaseFixture db, Guid receiptId)
    {
        var processor = factory.Services.GetRequiredService<CarrierInboxProcessor>();
        var claimed = Assert.Single(await processor.ClaimAsync(5, [receiptId], TestApi.Ct));
        Assert.Equal(ProcessResult.Processed, await processor.ProcessAsync(claimed, TestApi.Ct));
        await using var connection = db.OpenOwnerConnection();
        return (string)(await TestApi.ScalarAsync(connection, "SELECT outcome FROM tracking.carrier_event_receipts WHERE id = @id", ("id", receiptId)))!;
    }
}
