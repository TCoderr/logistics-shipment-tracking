using System.Globalization;
using Lst.Modules.Shipments.Domain;

namespace Lst.Modules.Shipments.Application;

/// <summary>"Sla" konfigürasyon bölümü (docs/01 §4.4, varsayım A1).</summary>
public sealed class SlaOptions
{
    public const string SectionName = "Sla";

    public string TimeZoneId { get; set; } = "Europe/Istanbul";

    public string CutoffLocalTime { get; set; } = "15:00";

    /// <summary>yyyy-MM-dd biçiminde resmî tatiller.</summary>
    public IList<string> Holidays { get; } = [];

    public SlaPolicy ToPolicy() =>
        new(
            TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId),
            TimeOnly.ParseExact(CutoffLocalTime, "HH:mm", CultureInfo.InvariantCulture),
            Holidays.Select(h => DateOnly.ParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet());
}
