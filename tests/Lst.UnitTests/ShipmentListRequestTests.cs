using Lst.Modules.Shipments.Application;
using Lst.Modules.Shipments.Domain;

namespace Lst.UnitTests;

public sealed class ShipmentListRequestTests
{
    private static ShipmentListRequest Valid(ShipmentListQuery query)
    {
        Assert.True(ShipmentListRequest.TryCreate(query, out var request, out var errors), string.Join(",", errors.Keys));
        return request!;
    }

    [Fact]
    public void Defaults_are_newest_first_with_50_items()
    {
        var request = Valid(new ShipmentListQuery());
        Assert.Equal(ShipmentSortField.CreatedAt, request.SortField);
        Assert.True(request.Descending);
        Assert.Equal(50, request.Limit);
        Assert.Equal(ShipmentSearchKind.None, request.SearchKind);
    }

    [Theory]
    [InlineData("LS4829105735", ShipmentSearchKind.TrackingNumber)]
    [InlineData("LS4829105735-02", ShipmentSearchKind.LabelCode)]
    [InlineData("ORD-2026-100245", ShipmentSearchKind.ExternalReference)]
    public void Search_value_is_classified(string q, ShipmentSearchKind expected) =>
        Assert.Equal(expected, Valid(new ShipmentListQuery { Q = q }).SearchKind);

    [Fact]
    public void Phone_last4_requires_another_filter()
    {
        Assert.False(ShipmentListRequest.TryCreate(new ShipmentListQuery { Q = "4567" }, out _, out var errors));
        Assert.Contains("q", errors.Keys);
        var request = Valid(new ShipmentListQuery { Q = "4567", Status = ["Created"] });
        Assert.Equal(ShipmentSearchKind.RecipientPhoneLast4, request.SearchKind);
    }

    [Fact]
    public void Dates_require_offset_and_are_normalized_to_utc()
    {
        var request = Valid(new ShipmentListQuery { CreatedFrom = "2026-10-01T03:00:00+03:00" });
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), request.CreatedFrom);
        Assert.Equal(TimeSpan.Zero, request.CreatedFrom!.Value.Offset);
        Assert.False(ShipmentListRequest.TryCreate(new ShipmentListQuery { CreatedFrom = "2026-10-01" }, out _, out _));
    }

    [Fact]
    public void Fingerprint_binds_filters_sort_and_user()
    {
        var user = Guid.NewGuid();
        var a = Valid(new ShipmentListQuery { Status = ["Created", "AtHub"] });
        var sameOtherOrder = Valid(new ShipmentListQuery { Status = ["AtHub", "Created"] });
        var otherFilter = Valid(new ShipmentListQuery { Status = ["Created"] });
        var otherSort = Valid(new ShipmentListQuery { Status = ["Created", "AtHub"], Sort = "createdAt" });

        Assert.Equal(a.FilterFingerprint(user), sameOtherOrder.FilterFingerprint(user));
        Assert.NotEqual(a.FilterFingerprint(user), otherFilter.FilterFingerprint(user));
        Assert.NotEqual(a.FilterFingerprint(user), otherSort.FilterFingerprint(user));
        Assert.NotEqual(a.FilterFingerprint(user), a.FilterFingerprint(Guid.NewGuid()));
    }

    [Fact]
    public void Statuses_are_parsed_case_sensitively()
    {
        Assert.Equal([ShipmentStatus.Created], Valid(new ShipmentListQuery { Status = ["Created"] }).Statuses);
        Assert.False(ShipmentListRequest.TryCreate(new ShipmentListQuery { Status = ["created"] }, out _, out _));
        Assert.False(ShipmentListRequest.TryCreate(new ShipmentListQuery { Status = ["1"] }, out _, out _));
    }
}
