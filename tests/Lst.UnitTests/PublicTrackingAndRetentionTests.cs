using System.Reflection;
using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Retention;

namespace Lst.UnitTests;

/// <summary>Dilim 8: public görünüm alan allowlist'i, kod biçimi, tracking numarası maskeleme ve saklama zamanlaması.</summary>
public sealed class PublicTrackingAndRetentionTests
{
    [Fact]
    public void Public_view_field_set_is_fixed()
    {
        static string[] Names(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order().ToArray();
        Assert.Equal(
            ["DestinationCity", "IsDelayed", "LastUpdatedAt", "PackageCount", "PromisedDeliveryDate", "Status", "StatusLabel", "Timeline", "TrackingNumberMasked"],
            Names(typeof(PublicTrackingView)));
        Assert.Equal(["City", "Label", "OccurredAt"], Names(typeof(PublicTimelineItem)));
    }

    [Theory]
    [InlineData("LS4829105735", "LS48******35")]
    [InlineData("LS0000000000", "LS00******00")]
    [InlineData("ABC", "***")]
    public void Tracking_number_is_masked_keeping_prefix_and_last_two(string input, string expected) =>
        Assert.Equal(expected, PublicTrackingHandler.MaskTrackingNumber(input));

    [Fact]
    public void Public_codes_must_be_26_char_crockford_uppercase()
    {
        var generated = new RandomShipmentCodeGenerator().NextPublicTrackingCode();
        Assert.True(PublicTrackingCodes.IsWellFormed(generated));
        Assert.False(PublicTrackingCodes.IsWellFormed(null));
        Assert.False(PublicTrackingCodes.IsWellFormed(generated[..25]));
        Assert.False(PublicTrackingCodes.IsWellFormed(generated.ToLowerInvariant().Replace('0', 'a')));
        Assert.False(PublicTrackingCodes.IsWellFormed(new string('I', 26)));
        Assert.False(PublicTrackingCodes.IsWellFormed("LS4829105735"));
    }

    [Theory]
    [InlineData("2026-10-10T23:00:00Z", "2026-10-11T00:00:00Z")]
    [InlineData("2026-10-10T01:00:00Z", "2026-10-11T00:00:00Z")]
    [InlineData("2026-10-09T23:59:00Z", "2026-10-10T00:00:00Z")]
    public void Retention_runs_daily_at_03_00_istanbul(string now, string expectedUtc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture),
            RetentionWorker.NextRun(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), zone, new TimeOnly(3, 0)));
    }
}
