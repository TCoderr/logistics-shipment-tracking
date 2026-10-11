namespace Lst.Modules.Shipments.Domain;

/// <summary>İş günü: Pazartesi–Cumartesi, konfigüre edilmiş tatiller hariç (docs/01 §4.4).</summary>
public sealed record SlaPolicy(TimeZoneInfo TimeZone, TimeOnly Cutoff, IReadOnlySet<DateOnly> Holidays)
{
    public bool IsBusinessDay(DateOnly date) => date.DayOfWeek != DayOfWeek.Sunday && !Holidays.Contains(date);
}

public sealed class PromisedDeliveryCalculator(SlaPolicy policy)
{
    public SlaPolicy Policy { get; } = policy;

    /// <summary>
    /// Kabul günü: yerel saat kesimden (dahil) önce ve iş günüyse o gün; aksi halde sonraki iş günü.
    /// Söz verilen tarih: kabul günü + Standard 2 / Express 1 iş günü. Tam kesim saati (15:00) sonrası sayılır.
    /// </summary>
    public DateOnly Calculate(DateTimeOffset nowUtc, ServiceLevel serviceLevel)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, Policy.TimeZone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var acceptance = Policy.IsBusinessDay(today) && TimeOnly.FromDateTime(local.DateTime) < Policy.Cutoff
            ? today
            : NextBusinessDay(today);

        var businessDays = serviceLevel switch
        {
            ServiceLevel.Standard => 2,
            ServiceLevel.Express => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(serviceLevel), serviceLevel, null),
        };

        var result = acceptance;
        for (var i = 0; i < businessDays; i++)
        {
            result = NextBusinessDay(result);
        }

        return result;
    }

    public DateOnly TodayLocal(DateTimeOffset nowUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, Policy.TimeZone).DateTime);

    public short OffsetMinutes(DateTimeOffset nowUtc) =>
        checked((short)Policy.TimeZone.GetUtcOffset(nowUtc).TotalMinutes);

    private DateOnly NextBusinessDay(DateOnly date)
    {
        var next = date.AddDays(1);
        while (!Policy.IsBusinessDay(next))
        {
            next = next.AddDays(1);
        }

        return next;
    }
}
