using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lst.Modules.Operations.Application;
using Lst.Modules.Operations.Domain;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Outbox;

namespace Lst.UnitTests;

/// <summary>Dilim 7: outbox payload PII sınırı ve serileştirme, backoff, SLA iş saati takvimi, istisna önem yükseltme.</summary>
public sealed partial class OutboxAndSlaTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static SlaPolicy Policy(params DateOnly[] holidays) => new(Istanbul, new TimeOnly(15, 0), holidays.ToHashSet());

    private static DateTimeOffset Local(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Outbox_payloads_carry_only_identifiers_codes_and_times()
    {
        var assemblies = new[]
        {
            typeof(Shipment).Assembly,
            typeof(ShipmentException).Assembly,
            typeof(Lst.Modules.Tracking.TrackingModule).Assembly,
            typeof(OutboxMessage).Assembly,
        };
        var payloadTypes = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IOutboxPayload).IsAssignableFrom(t))
            .ToList();
        Assert.True(payloadTypes.Count >= 4, string.Join(", ", payloadTypes.Select(t => t.Name)));
        var allowed = new[] { typeof(Guid), typeof(Guid?), typeof(string), typeof(DateTimeOffset), typeof(int), typeof(long), typeof(IReadOnlyList<Guid>) };
        foreach (var type in payloadTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(allowed.Contains(property.PropertyType), $"{type.Name}.{property.Name}: {property.PropertyType.Name}");
                Assert.False(PiiName().IsMatch(property.Name), $"{type.Name}.{property.Name} looks like personal data");
            }
        }
    }

    [GeneratedRegex("name|phone|email|address|note|recipient|sender|street|postal|district", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PiiName();

    private sealed record SamplePayload(Guid ShipmentId, string StatusAfter) : IOutboxPayload
    {
        public static string MessageType => "test.sample.v1";
    }

    [Fact]
    public void Outbox_message_is_typed_camel_cased_and_has_a_version7_message_id()
    {
        var now = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var message = OutboxMessage.Create(new SamplePayload(id, "Delivered"), now);
        Assert.Equal("test.sample.v1", message.Type);
        Assert.Equal(7, message.MessageId.Version);
        Assert.Equal((now, now), (message.OccurredAt, message.AvailableAt));
        var json = JsonDocument.Parse(message.Payload).RootElement;
        Assert.Equal(id, json.GetProperty("shipmentId").GetGuid());
        Assert.Equal("Delivered", json.GetProperty("statusAfter").GetString());
        Assert.False(json.TryGetProperty("messageType", out _));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(3, 40)]
    [InlineData(7, 640)]
    [InlineData(8, 900)]
    [InlineData(20, 900)]
    public void Outbox_backoff_is_exponential_capped_with_at_most_20_percent_jitter(int attempts, double baseSeconds)
    {
        for (var i = 0; i < 50; i++)
        {
            var seconds = OutboxDispatcher.Backoff(attempts).TotalSeconds;
            Assert.InRange(seconds, baseSeconds, baseSeconds * 1.2 + 0.001);
        }
    }

    [Fact]
    public void Business_hours_count_saturday_but_not_sunday()
    {
        // Cuma 10:00 → Pazartesi 10:00: Cuma 14 + Cumartesi 24 + Pazar 0 + Pazartesi 10 = 48.
        Assert.Equal(48, SlaCalendar.BusinessHoursBetween(Policy(), Local(2026, 10, 9, 10), Local(2026, 10, 12, 10)), 6);
        Assert.Equal(0, SlaCalendar.BusinessHoursBetween(Policy(), Local(2026, 10, 11, 1), Local(2026, 10, 11, 23)), 6);
    }

    [Fact]
    public void Holidays_are_excluded_from_business_hours()
    {
        // 29 Ekim Perşembe resmî tatil: Çarşamba 12:00 → Cuma 12:00 = 12 + 0 + 12.
        var withHoliday = Policy(new DateOnly(2026, 10, 29));
        Assert.Equal(24, SlaCalendar.BusinessHoursBetween(withHoliday, Local(2026, 10, 28, 12), Local(2026, 10, 30, 12)), 6);
        Assert.Equal(48, SlaCalendar.BusinessHoursBetween(Policy(), Local(2026, 10, 28, 12), Local(2026, 10, 30, 12)), 6);
    }

    [Fact]
    public void Business_hours_are_zero_for_reversed_ranges_and_wall_clock_for_very_old_ones()
    {
        Assert.Equal(0, SlaCalendar.BusinessHoursBetween(Policy(), Local(2026, 10, 12, 10), Local(2026, 10, 9, 10)));
        var from = Local(2026, 1, 1, 0);
        var to = Local(2026, 6, 1, 0);
        Assert.Equal((to - from).TotalHours, SlaCalendar.BusinessHoursBetween(Policy(), from, to));
    }

    [Fact]
    public void Exception_severity_only_escalates_and_tightens_due_time()
    {
        var opened = new DateTimeOffset(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);
        var exception = ShipmentException.Open(Guid.NewGuid(), ExceptionType.DeliveryOverdue, ExceptionSeverity.Medium, null, "SlaMonitor", opened);
        Assert.False(exception.Escalate(ExceptionSeverity.Medium, opened.AddHours(1)));
        Assert.False(exception.Escalate(ExceptionSeverity.Low, opened.AddHours(1)));
        Assert.True(exception.Escalate(ExceptionSeverity.High, opened.AddHours(1)));
        Assert.Equal(ExceptionSeverity.High, exception.Severity);
        Assert.Equal(opened.AddHours(5), exception.DueAt);
        Assert.Equal(1, exception.OccurrenceCount);
    }
}
