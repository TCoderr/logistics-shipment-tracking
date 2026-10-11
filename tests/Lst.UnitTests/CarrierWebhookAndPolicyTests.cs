using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lst.Modules.Shipments.Domain;
using Lst.Modules.Tracking.Application;
using Npgsql;

namespace Lst.UnitTests;

/// <summary>Carrier webhook imzası, şema normalizasyonu, inbox hata sınıflandırması ve carrier karar fonksiyonu (docs/02 §5.3, docs/05 §5).</summary>
public sealed class CarrierWebhookAndPolicyTests
{
    private const string Secret = "unit-test-secret-0123456789abcdef0123";
    private const string OtherSecret = "unit-test-secret-fedcba9876543210fedc";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"eventId\":\"e-1\"}");

    private static string Header(string secret, DateTimeOffset at, byte[] body) =>
        $"t={at.ToUnixTimeSeconds()},v1={CarrierWebhookParser.Sign(secret, at.ToUnixTimeSeconds(), body)}";

    private static bool Verify(string? header, byte[]? body = null, params string[] secrets) =>
        CarrierWebhookParser.VerifySignature(header, body ?? Body, secrets.Length == 0 ? [Secret] : secrets, Now, 300, out _);

    [Fact]
    public void Valid_signature_over_raw_bytes_is_accepted()
    {
        Assert.True(CarrierWebhookParser.VerifySignature(Header(Secret, Now.AddSeconds(-10), Body), Body, [Secret], Now, 300, out var signedAt));
        Assert.Equal(Now.AddSeconds(-10), signedAt);
    }

    [Fact]
    public void Signature_is_bound_to_exact_bytes()
    {
        // Anlamca aynı ama byte olarak farklı JSON (boşluk) imzayı bozar: doğrulama ham gövde üzerindedir.
        var reformatted = Encoding.UTF8.GetBytes("{ \"eventId\": \"e-1\" }");
        Assert.False(Verify(Header(Secret, Now, Body), reformatted));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Timestamp_outside_tolerance_is_rejected(int offsetSeconds) =>
        Assert.False(Verify(Header(Secret, Now.AddSeconds(offsetSeconds), Body)));

    [Fact]
    public void Wrong_secret_is_rejected() => Assert.False(Verify(Header(OtherSecret, Now, Body)));

    [Fact]
    public void Rotation_accepts_either_active_secret_and_multiple_v1_values()
    {
        Assert.True(Verify(Header(OtherSecret, Now, Body), null, Secret, OtherSecret));
        var t = Now.ToUnixTimeSeconds();
        var multi = $"t={t},v1={new string('0', 64)},v1={CarrierWebhookParser.Sign(Secret, t, Body)}";
        Assert.True(Verify(multi));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("t=1791534900")]
    [InlineData("t=abc,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=1791534900,v1=ZZ00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("garbage")]
    public void Malformed_headers_are_rejected(string? header) => Assert.False(Verify(header));

    [Fact]
    public void Uppercase_hex_signature_is_rejected_canonical_form_only()
    {
        var t = Now.ToUnixTimeSeconds();
        Assert.False(Verify($"t={t},v1={CarrierWebhookParser.Sign(Secret, t, Body).ToUpperInvariant()}"));
    }

    private static CarrierWebhookPayload Payload(string type = "DELIVERY_FAILED", string occurredAt = "2026-10-10T12:47:00+03:00", AttemptPayload? attempt = null) => new()
    {
        EventId = "simex-evt-000918273",
        CarrierCode = "SIMEX",
        TrackingNumber = SampleTrackingNumber(),
        EventType = type,
        OccurredAt = occurredAt,
        Sequence = 7,
        Location = new LocationPayload { FacilityCode = "ANK-SX3", City = "Ankara" },
        Attempt = attempt ?? new AttemptPayload { Number = 1, Reason = "RECIPIENT_ABSENT" },
    };

    private static string SampleTrackingNumber() => TrackingNumbers.FromPayload(482910573);

    [Theory]
    [InlineData("PICKED_UP", TrackingEventType.HandedToCarrier)]
    [InlineData("IN_TRANSIT", TrackingEventType.CarrierInTransit)]
    [InlineData("ARRIVED_AT_FACILITY", TrackingEventType.ArrivedAtHub)]
    [InlineData("DEPARTED_FACILITY", TrackingEventType.DepartedHub)]
    [InlineData("OUT_FOR_DELIVERY", TrackingEventType.OutForDelivery)]
    [InlineData("DELIVERED", TrackingEventType.Delivered)]
    [InlineData("DELIVERY_FAILED", TrackingEventType.DeliveryFailed)]
    [InlineData("RETURN_INITIATED", TrackingEventType.ReturnInitiated)]
    [InlineData("RETURNED_TO_SENDER", TrackingEventType.ReturnedToSender)]
    public void Event_types_map_per_contract(string carrierType, TrackingEventType expected)
    {
        var (value, errors) = CarrierWebhookParser.Normalize(Payload(carrierType));
        Assert.Empty(errors);
        Assert.Equal(expected, value!.EventType);
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("LOST")]
    [InlineData("")]
    public void Unknown_event_types_are_rejected(string type) =>
        Assert.Contains("eventType", CarrierWebhookParser.Normalize(Payload(type)).Errors.Keys);

    [Theory]
    [InlineData("2026-10-10T12:47:00")]
    [InlineData("10/10/2026 12:47")]
    [InlineData("2026-10-10")]
    public void Offsetless_or_non_iso_times_are_rejected(string occurredAt) =>
        Assert.Contains("occurredAt", CarrierWebhookParser.Normalize(Payload(occurredAt: occurredAt)).Errors.Keys);

    [Fact]
    public void Delivery_failed_requires_attempt_and_maps_unknown_reason_to_other()
    {
        Assert.Contains("attempt.number", CarrierWebhookParser.Normalize(Payload(attempt: new AttemptPayload { Reason = "REFUSED" })).Errors.Keys);
        Assert.Contains("attempt.reason", CarrierWebhookParser.Normalize(Payload(attempt: new AttemptPayload { Number = 1 })).Errors.Keys);
        var (value, _) = CarrierWebhookParser.Normalize(Payload(attempt: new AttemptPayload { Number = 2, Reason = "DOG_IN_YARD" }));
        Assert.Equal(DeliveryFailureReason.Other, value!.FailureReason);
        Assert.Equal((short)2, value.AttemptNumber);
        var (refused, _) = CarrierWebhookParser.Normalize(Payload(attempt: new AttemptPayload { Number = 1, Reason = "REFUSED" }));
        Assert.Equal(DeliveryFailureReason.RecipientRefused, refused!.FailureReason);
    }

    [Fact]
    public void Canonical_hash_ignores_formatting_but_detects_content_changes()
    {
        var a = CarrierWebhookParser.Normalize(Payload()).Value!;
        var sameInstantOtherFormat = CarrierWebhookParser.Normalize(Payload() with { OccurredAt = "2026-10-10T12:47:00.000+03:00" }).Value!;
        var moved = CarrierWebhookParser.Normalize(Payload() with { Location = new LocationPayload { FacilityCode = "BRS-SX9", City = "Bursa" } }).Value!;
        Assert.Equal(a.CanonicalSha256(), sameInstantOtherFormat.CanonicalSha256());
        Assert.NotEqual(a.CanonicalSha256(), moved.CanonicalSha256());
    }

    [Fact]
    public void Strict_json_rejects_duplicates_string_numbers_and_excess_depth_but_skips_unknown_fields()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CarrierWebhookPayload>("{\"eventId\":\"a\",\"eventId\":\"b\"}", CarrierWebhookParser.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CarrierWebhookPayload>("{\"sequence\":\"7\"}", CarrierWebhookParser.Options));
        var deep = "{\"x\":" + string.Concat(Enumerable.Repeat("[", 20)) + string.Concat(Enumerable.Repeat("]", 20)) + "}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CarrierWebhookPayload>(deep, CarrierWebhookParser.Options));
        var parsed = JsonSerializer.Deserialize<CarrierWebhookPayload>("{\"eventId\":\"a\",\"driverPhone\":\"+905551112233\"}", CarrierWebhookParser.Options);
        Assert.Equal("a", parsed!.EventId);
    }

    [Fact]
    public void Transient_classification_covers_connection_lock_and_serialization_failures_only()
    {
        Assert.True(CarrierInboxProcessor.IsTransient(new NpgsqlException("down", new SocketException())));
        Assert.True(CarrierInboxProcessor.IsTransient(new TimeoutException()));
        Assert.True(CarrierInboxProcessor.IsTransient(new InvalidOperationException("wrapped", new IOException())));
        Assert.True(CarrierInboxProcessor.IsTransient(new PostgresException("x", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable)));
        Assert.True(CarrierInboxProcessor.IsTransient(new PostgresException("x", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure)));
        Assert.True(CarrierInboxProcessor.IsTransient(new PostgresException("x", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown)));
        Assert.False(CarrierInboxProcessor.IsTransient(new PostgresException("x", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation)));
        Assert.False(CarrierInboxProcessor.IsTransient(new InvalidDataException("bad payload")));
        Assert.False(CarrierInboxProcessor.IsTransient(new InvalidOperationException("domain")));
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 40)]
    [InlineData(4, 160)]
    [InlineData(10, 600)]
    public void Backoff_is_exponential_and_capped(int attempts, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CarrierInboxProcessor.Backoff(attempts));

    private static readonly Guid Simex = Guid.NewGuid();

    private static CarrierShipmentState State(ShipmentStatus status, DateTimeOffset? at = null, Guid? carrier = null, long? lastSequence = null, short attempts = 0) =>
        new(status, at ?? Now.AddHours(-2), carrier ?? Simex, lastSequence, HandedOver: status != ShipmentStatus.Created, HasDeparted: true, attempts, 3);

    private static CarrierEventInput Event(TrackingEventType type, DateTimeOffset? occurred = null, Guid? carrier = null, long? sequence = null) =>
        new(carrier ?? Simex, "SIMEX", Guid.NewGuid(), "LS0000000000", type, occurred ?? Now.AddMinutes(-5), Now, sequence, "ANK-SX3", "Ankara",
            type == TrackingEventType.DeliveryFailed ? (short)1 : null, type == TrackingEventType.DeliveryFailed ? DeliveryFailureReason.RecipientAbsent : null);

    [Fact]
    public void Unassigned_carrier_is_rejected_before_anything_else() =>
        Assert.Equal(new CarrierDecision(CarrierOutcome.Rejected, CarrierReasons.CarrierNotAssigned),
            CarrierEventPolicy.Decide(State(ShipmentStatus.InTransit), Event(TrackingEventType.Delivered, carrier: Guid.NewGuid()), false, false));

    [Fact]
    public void Out_of_order_event_after_delivery_is_stale()
    {
        var d = CarrierEventPolicy.Decide(State(ShipmentStatus.Delivered, Now.AddMinutes(-10)), Event(TrackingEventType.CarrierInTransit, Now.AddMinutes(-30)), false, false);
        Assert.Equal(CarrierOutcome.Stale, d.Outcome);
        Assert.Null(d.StatusAfter);
    }

    [Fact]
    public void Newer_event_contradicting_terminal_goes_to_review_never_changes_status()
    {
        var d = CarrierEventPolicy.Decide(State(ShipmentStatus.Delivered, Now.AddMinutes(-10)), Event(TrackingEventType.DeliveryFailed, Now.AddMinutes(-2)), false, false);
        Assert.Equal(new CarrierDecision(CarrierOutcome.PendingReview, CarrierReasons.ContradictsTerminal), d);
    }

    [Fact]
    public void Sequence_rules_detect_duplicates_and_stale_events()
    {
        Assert.Equal(CarrierOutcome.Duplicate, CarrierEventPolicy.Decide(State(ShipmentStatus.InTransit), Event(TrackingEventType.OutForDelivery, sequence: 5), false, true).Outcome);
        Assert.Equal(CarrierReasons.SequenceStale,
            CarrierEventPolicy.Decide(State(ShipmentStatus.InTransit, lastSequence: 9), Event(TrackingEventType.OutForDelivery, sequence: 5), false, false).Reason);
        Assert.Equal(CarrierReasons.BusinessDuplicate,
            CarrierEventPolicy.Decide(State(ShipmentStatus.InTransit), Event(TrackingEventType.OutForDelivery), true, false).Reason);
    }

    [Fact]
    public void Skew_and_age_limits_apply()
    {
        Assert.Equal(CarrierReasons.FutureTimestamp,
            CarrierEventPolicy.Decide(State(ShipmentStatus.InTransit), Event(TrackingEventType.Delivered, Now.AddMinutes(16)), false, false).Reason);
        Assert.Equal(new CarrierDecision(CarrierOutcome.PendingReview, CarrierReasons.TooOld),
            CarrierEventPolicy.Decide(State(ShipmentStatus.InTransit, Now.AddDays(-9)), Event(TrackingEventType.Delivered, Now.AddDays(-8)), false, false));
    }

    [Fact]
    public void Forward_transitions_apply_and_mark_handover()
    {
        var pickup = CarrierEventPolicy.Decide(State(ShipmentStatus.Created) with { HandedOver = false }, Event(TrackingEventType.HandedToCarrier), false, false);
        Assert.Equal(CarrierOutcome.Applied, pickup.Outcome);
        Assert.Equal(ShipmentStatus.InTransit, pickup.StatusAfter);
        Assert.True(pickup.MarksHandover);

        var delivered = CarrierEventPolicy.Decide(State(ShipmentStatus.OutForDelivery), Event(TrackingEventType.Delivered), false, false);
        Assert.Equal(ShipmentStatus.Delivered, delivered.StatusAfter);

        var transitAgain = CarrierEventPolicy.Decide(State(ShipmentStatus.InTransit), Event(TrackingEventType.CarrierInTransit), false, false);
        Assert.Equal(new CarrierDecision(CarrierOutcome.Applied, CarrierReasons.LocationOnly, null, false), transitAgain);
    }

    [Fact]
    public void Out_for_delivery_after_max_attempts_goes_to_review() =>
        Assert.Equal(CarrierReasons.MaxAttemptsReached,
            CarrierEventPolicy.Decide(State(ShipmentStatus.DeliveryFailed, attempts: 3), Event(TrackingEventType.OutForDelivery), false, false).Reason);

    [Fact]
    public void Transition_outside_carrier_table_goes_to_review() =>
        Assert.Equal(new CarrierDecision(CarrierOutcome.PendingReview, CarrierReasons.TransitionNotAllowed),
            CarrierEventPolicy.Decide(State(ShipmentStatus.Created), Event(TrackingEventType.Delivered), false, false));
}
