using Lst.Modules.Shipments.Domain;

namespace Lst.UnitTests;

/// <summary>U3 — Cumartesi iş günü, Pazar değil; kesim 15:00 Europe/Istanbul (UTC+3). 2026-10-09 Cuma.</summary>
public sealed class PromisedDeliveryCalculatorTests
{
    private static readonly PromisedDeliveryCalculator Calculator = new(new SlaPolicy(
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"),
        new TimeOnly(15, 0),
        new HashSet<DateOnly> { new(2026, 10, 29) }));

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(9, 14, 59, ServiceLevel.Standard, 12)] // Cuma 14:59 → kabul Cuma → Pazartesi
    [InlineData(9, 15, 0, ServiceLevel.Standard, 13)]  // Tam 15:00 kesim sonrası → kabul Cumartesi → Salı
    [InlineData(9, 15, 1, ServiceLevel.Standard, 13)]  // Cuma 15:01 → kabul Cumartesi → Salı
    [InlineData(11, 10, 0, ServiceLevel.Express, 13)]  // Pazar 10:00 → kabul Pazartesi → Salı
    [InlineData(10, 10, 0, ServiceLevel.Express, 12)]  // Cumartesi 10:00 → kabul Cumartesi → Pazartesi
    [InlineData(9, 14, 59, ServiceLevel.Express, 10)]  // Cuma 14:59 Express → Cumartesi
    public void U3_promised_date_follows_business_calendar(int day, int hour, int minute, ServiceLevel level, int expectedDay)
    {
        var result = Calculator.Calculate(Local(2026, 10, day, hour, minute), level);
        Assert.Equal(new DateOnly(2026, 10, expectedDay), result);
    }

    [Fact]
    public void U3_configured_holiday_is_skipped_when_counting()
    {
        // Çarşamba 28 Ekim Express → 29 Ekim tatil → 30 Ekim Cuma
        Assert.Equal(new DateOnly(2026, 10, 30), Calculator.Calculate(Local(2026, 10, 28, 10, 0), ServiceLevel.Express));
    }

    [Fact]
    public void U3_creation_on_holiday_is_accepted_next_business_day()
    {
        // 29 Ekim (tatil) 10:00 Express → kabul 30 Ekim → 31 Ekim Cumartesi
        Assert.Equal(new DateOnly(2026, 10, 31), Calculator.Calculate(Local(2026, 10, 29, 10, 0), ServiceLevel.Express));
    }

    [Fact]
    public void Utc_input_is_converted_to_istanbul_before_cutoff_check()
    {
        // 12:00Z = 15:00 yerel → kesim sonrası
        var utc = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 13), Calculator.Calculate(utc, ServiceLevel.Standard));
        Assert.Equal(180, Calculator.OffsetMinutes(utc));
    }

    [Fact]
    public void Delayed_flag_excludes_terminal_and_return_statuses()
    {
        var promised = new DateOnly(2026, 10, 9);
        var today = new DateOnly(2026, 10, 12);
        Assert.True(ShipmentStatus.Created.IsDelayed(promised, today));
        Assert.False(ShipmentStatus.Delivered.IsDelayed(promised, today));
        Assert.False(ShipmentStatus.ReturningToSender.IsDelayed(promised, today));
        Assert.False(ShipmentStatus.Created.IsDelayed(today, today));
    }
}
