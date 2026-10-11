using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lst.Modules.Tracking.Application;

/// <summary>Simülasyon bayrağı. Yalnız Development'ta true olabilir; aksi halde uygulama açılmaz (başlangıç doğrulaması).</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";

    public bool Enabled { get; set; }

    /// <summary>Simülatörün olayları gönderdiği kendi HTTPS adresi (yalnız loopback).</summary>
    public Uri BaseUrl { get; set; } = new("https://localhost:7210");
}

public enum SimulationScenario
{
    HappyPath,
    DuplicateDelivery,
    BusinessDuplicate,
    OutOfOrderAfterDelivery,
    LateArrival,
    SameIdDifferentPayload,
    ContradictsTerminal,
    InvalidSignature,
}

public sealed record SimulationRunRequest
{
    public string? TrackingNumber { get; init; }

    public string? Scenario { get; init; }
}

public sealed record SimulatedDelivery(string EventId, string EventType, int HttpStatus, string? Status);

public sealed record SimulationRunResponse(Guid RunId, string Scenario, IReadOnlyList<SimulatedDelivery> Deliveries);

/// <summary>Simülatörün webhook'a HTTP gönderimi. Varsayılan gerçek HTTPS çağrısıdır; testler TestServer istemcisiyle değiştirir.</summary>
public interface ICarrierWebhookTransport
{
    Task<(int Status, string Body)> SendAsync(string carrierCode, byte[] body, string signatureHeader, CancellationToken cancellationToken);
}

internal sealed class HttpCarrierWebhookTransport(HttpClient client) : ICarrierWebhookTransport
{
    public async Task<(int Status, string Body)> SendAsync(string carrierCode, byte[] body, string signatureHeader, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"api/carrier-webhooks/{Uri.EscapeDataString(carrierCode)}", UriKind.Relative));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(CarrierWebhookParser.SignatureHeader, signatureHeader);
        using var response = await client.SendAsync(request, cancellationToken);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

/// <summary>
/// SIMEX simülatörü (yalnız Development + Simulation:Enabled). Olayları gerçek imzayla kendi webhook endpoint'ine HTTP üzerinden gönderir;
/// sonuçlar inbox worker'ı tarafından asenkron belirlenir. Gerçek carrier'a hiçbir istek gitmez.
/// </summary>
public sealed class CarrierSimulator(
    ICarrierWebhookTransport transport, IOptions<CarrierOptions> carrierOptions, TimeProvider timeProvider, LogisticsDbContext db)
{
    public const string CarrierCode = "SIMEX";

    private static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(6);

    public async Task<SimulationRunResponse> RunAsync(string trackingNumber, SimulationScenario scenario, CancellationToken cancellationToken)
    {
        if (!carrierOptions.Value.Secrets.TryGetValue(CarrierCode, out var secrets) || secrets.Values.Count == 0)
        {
            throw new InvalidOperationException("SIMEX secret is not configured.");
        }

        var secret = secrets.Values[0];
        var now = timeProvider.GetUtcNow();
        var runId = Guid.CreateVersion7(now);
        // Sequence her koşuda artan bir tabandan başlar; aynı gönderide tekrar koşmak sequence tekrarı üretmez.
        var sequenceBase = now.ToUnixTimeSeconds() * 100;
        // Olay zamanları gönderinin son durum değişikliğinden sonraya yerleştirilir (aksi halde yeni gönderide hepsi Stale olur).
        var shipment = await db.Set<Shipment>().AsNoTracking()
            .Where(x => x.TrackingNumber == trackingNumber)
            .Select(x => new { x.StatusOccurredAt, x.CarrierHandedOverAt })
            .FirstOrDefaultAsync(cancellationToken);
        var start = shipment is null || shipment.StatusOccurredAt < now.AddMinutes(-10) ? now.AddMinutes(-10) : shipment.StatusOccurredAt.AddSeconds(1);
        // Olaylar birbirinden ayrışsın diye pencere en az MinimumWindow kadardır. Yeni oluşturulmuş gönderide (son durum birkaç ms önce)
        // pencere kısa süreliğine ileriye taşar; bu, webhook'un gelecek zaman toleransının (15 dk) çok altındadır. Önceki davranışta
        // tüm olaylar aynı ana düşüyor ve saniyeye yuvarlanınca son durumdan eski kalıp Stale sonuçlanıyordu.
        var timeline = new Timeline(start, now - start >= MinimumWindow ? now : start + MinimumWindow);
        var needsPickup = shipment is not null && shipment.CarrierHandedOverAt is null;
        var events = Build(scenario, runId, trackingNumber, timeline, needsPickup, sequenceBase);
        var deliveries = new List<SimulatedDelivery>(events.Count);
        foreach (var (payload, signWithWrongSecret) in events)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload, CarrierWebhookParser.Options);
            var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var signingSecret = signWithWrongSecret ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : secret;
            var header = string.Create(CultureInfo.InvariantCulture, $"t={timestamp},v1={CarrierWebhookParser.Sign(signingSecret, timestamp, body)}");
            var (status, responseBody) = await transport.SendAsync(CarrierCode, body, header, cancellationToken);
            deliveries.Add(new SimulatedDelivery(payload.EventId!, payload.EventType!, status, ReadStatus(responseBody)));
        }

        return new SimulationRunResponse(runId, scenario.ToString(), deliveries);
    }

    private static List<(CarrierWebhookPayload Payload, bool WrongSecret)> Build(
        SimulationScenario scenario, Guid runId, string trackingNumber, Timeline timeline, bool needsPickup, long sequenceBase)
    {
        var n = 0;
        CarrierWebhookPayload Event(string type, DateTimeOffset at, string? facility = "IST-SX1", string? city = "İstanbul", AttemptPayload? attempt = null)
        {
            n++;
            return new CarrierWebhookPayload
            {
                EventId = $"sim-{runId:N}-{n}",
                CarrierCode = CarrierCode,
                TrackingNumber = trackingNumber,
                EventType = type,
                OccurredAt = at.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                Sequence = sequenceBase + n,
                Location = facility is null ? null : new LocationPayload { FacilityCode = facility, City = city },
                Attempt = attempt,
            };
        }

        var result = new List<(CarrierWebhookPayload, bool)>();
        // Teslim içeren senaryolar carrier'a henüz teslim edilmemiş gönderide önce teslim alma olayı gönderir.
        if (needsPickup && scenario is SimulationScenario.DuplicateDelivery or SimulationScenario.OutOfOrderAfterDelivery or SimulationScenario.ContradictsTerminal)
        {
            result.Add((Event("PICKED_UP", timeline.At(0, 4)), false));
        }

        switch (scenario)
        {
            case SimulationScenario.HappyPath:
                result.Add((Event("PICKED_UP", timeline.At(0, 5)), false));
                result.Add((Event("IN_TRANSIT", timeline.At(1, 5)), false));
                result.Add((Event("ARRIVED_AT_FACILITY", timeline.At(2, 5), "ANK-SX3", "Ankara"), false));
                result.Add((Event("OUT_FOR_DELIVERY", timeline.At(3, 5), "ANK-SX3", "Ankara"), false));
                result.Add((Event("DELIVERED", timeline.At(4, 5), "ANK-SX3", "Ankara"), false));
                break;
            case SimulationScenario.DuplicateDelivery:
            {
                var delivered = Event("DELIVERED", timeline.At(2, 4), "ANK-SX3", "Ankara");
                result.Add((delivered, false));
                result.Add((delivered, false));
                break;
            }

            case SimulationScenario.BusinessDuplicate:
            {
                var first = timeline.At(1, 4);
                result.Add((Event("IN_TRANSIT", first), false));
                result.Add((Event("IN_TRANSIT", first.AddSeconds(30) > timeline.End ? timeline.End : first.AddSeconds(30)), false));
                break;
            }

            case SimulationScenario.OutOfOrderAfterDelivery:
                result.Add((Event("DELIVERED", timeline.At(3, 4), "ANK-SX3", "Ankara"), false));
                result.Add((Event("IN_TRANSIT", timeline.At(1, 4)), false));
                break;
            case SimulationScenario.LateArrival:
                result.Add((Event("IN_TRANSIT", timeline.Start.AddHours(-3)), false));
                break;
            case SimulationScenario.SameIdDifferentPayload:
            {
                var first = Event("IN_TRANSIT", timeline.At(1, 4));
                result.Add((first, false));
                result.Add((first with { Location = new LocationPayload { FacilityCode = "BRS-SX9", City = "Bursa" } }, false));
                break;
            }

            case SimulationScenario.ContradictsTerminal:
                result.Add((Event("DELIVERED", timeline.At(2, 4), "ANK-SX3", "Ankara"), false));
                result.Add((Event("DELIVERY_FAILED", timeline.At(3, 4), "ANK-SX3", "Ankara", new AttemptPayload { Number = 1, Reason = "RECIPIENT_ABSENT" }), false));
                break;
            case SimulationScenario.InvalidSignature:
                result.Add((Event("IN_TRANSIT", timeline.At(1, 4)), true));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return result;
    }

    /// <summary>[Start, End] aralığına eşit aralıklı olay zamanları.</summary>
    private sealed record Timeline(DateTimeOffset Start, DateTimeOffset End)
    {
        public DateTimeOffset At(int index, int count) =>
            End <= Start ? End : Start + TimeSpan.FromTicks((End - Start).Ticks * index / Math.Max(1, count));
    }

    private static string? ReadStatus(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            return document.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
