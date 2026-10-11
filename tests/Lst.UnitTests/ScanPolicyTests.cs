using Lst.Modules.Shipments.Domain;

namespace Lst.UnitTests;

/// <summary>Hub taraması karar fonksiyonu: docs/02 §3.2 geçiş matrisi, §3.4 paket kuralları, §5.3 sıra.</summary>
public sealed class ScanPolicyTests
{
    private static readonly Guid Ist = Guid.NewGuid();
    private static readonly Guid Ank = Guid.NewGuid();
    private static readonly Guid Izm = Guid.NewGuid();
    private static readonly Guid P1 = Guid.NewGuid();
    private static readonly Guid P2 = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static ShipmentSnapshot Ship(ShipmentStatus status, Guid? hub = null, bool departed = false, short attempts = 0, short extra = 0, DateTimeOffset? statusAt = null) =>
        new(status, statusAt ?? Now.AddHours(-2), hub, Ank, departed, attempts, extra);

    private static PackageSnapshot Pkg(Guid id, Guid? hub, ScanType? last = null, DateTimeOffset? at = null) => new(id, hub, last, at);

    private static ScanDecision Decide(ShipmentSnapshot s, PackageSnapshot[] packages, ScanType type, Guid hub, DateTimeOffset? occurred = null, Guid? package = null, params DateTimeOffset[] same) =>
        ScanPolicy.Decide(s, packages, new ScanInput(package ?? P1, type, hub, occurred ?? Now.AddMinutes(-1), Now), same);

    [Theory]
    [InlineData(ShipmentStatus.Created)]
    [InlineData(ShipmentStatus.InTransit)]
    [InlineData(ShipmentStatus.DeliveryFailed)]
    public void Arrival_moves_shipment_to_at_hub(ShipmentStatus from)
    {
        var d = Decide(Ship(from), [Pkg(P1, null), Pkg(P2, null)], ScanType.Arrival, Ist);
        Assert.Equal(DecisionOutcome.Applied, d.Outcome);
        Assert.Equal(ShipmentStatus.AtHub, d.StatusAfter);
        Assert.Equal(Ist, d.ShipmentHubAfter);
        Assert.Equal(Ist, d.PackageHubAfter);
    }

    [Fact]
    public void Second_package_arrival_at_same_hub_is_location_only()
    {
        var d = Decide(Ship(ShipmentStatus.AtHub, Ist), [Pkg(P1, Ist, ScanType.Arrival, Now.AddMinutes(-5)), Pkg(P2, null)], ScanType.Arrival, Ist, package: P2);
        Assert.Equal(DecisionOutcome.Applied, d.Outcome);
        Assert.False(d.ChangesStatus);
        Assert.Equal(ScanReasons.PackageLocationOnly, d.ReasonCode);
        Assert.Equal(Ist, d.PackageHubAfter);
    }

    [Fact]
    public void Arrival_at_other_hub_without_departure_records_missing_departure()
    {
        var d = Decide(Ship(ShipmentStatus.AtHub, Ist), [Pkg(P1, Ist), Pkg(P2, Ist)], ScanType.Arrival, Izm);
        Assert.Equal(DecisionOutcome.Applied, d.Outcome);
        Assert.Equal(ScanReasons.MissingDepartureScan, d.ReasonCode);
        Assert.False(d.ChangesStatus);
        Assert.True(d.ShipmentHubChanges);
        Assert.Equal(Izm, d.ShipmentHubAfter);
        Assert.True(d.MarksDeparted);
    }

    [Fact]
    public void Arrival_while_out_for_delivery_is_rejected()
    {
        var d = Decide(Ship(ShipmentStatus.OutForDelivery), [Pkg(P1, null, ScanType.OutForDelivery, Now.AddHours(-1))], ScanType.Arrival, Ank);
        Assert.Equal(DecisionOutcome.Rejected, d.Outcome);
        Assert.Equal(ScanReasons.TransitionNotAllowed, d.ReasonCode);
    }

    [Fact]
    public void Departure_requires_all_packages_at_hub_before_moving_scanned_package()
    {
        var notConsolidated = Decide(Ship(ShipmentStatus.AtHub, Ist), [Pkg(P1, Ist), Pkg(P2, null)], ScanType.Departure, Ist);
        Assert.Equal(DecisionOutcome.Rejected, notConsolidated.Outcome);
        Assert.Equal(ScanReasons.PackagesNotConsolidated, notConsolidated.ReasonCode);

        var consolidated = Decide(Ship(ShipmentStatus.AtHub, Ist), [Pkg(P1, Ist), Pkg(P2, Ist)], ScanType.Departure, Ist);
        Assert.Equal(DecisionOutcome.Applied, consolidated.Outcome);
        Assert.Equal(ShipmentStatus.InTransit, consolidated.StatusAfter);
        Assert.Null(consolidated.ShipmentHubAfter);
        Assert.Null(consolidated.PackageHubAfter);
        Assert.True(consolidated.MarksDeparted);
    }

    [Fact]
    public void Departure_of_package_not_at_hub_is_rejected()
    {
        var d = Decide(Ship(ShipmentStatus.AtHub, Ist), [Pkg(P1, Izm), Pkg(P2, Ist)], ScanType.Departure, Ist);
        Assert.Equal(ScanReasons.PackageNotAtHub, d.ReasonCode);
    }

    [Fact]
    public void Second_package_departure_after_shipment_in_transit_is_location_only()
    {
        var d = Decide(Ship(ShipmentStatus.InTransit, departed: true), [Pkg(P1, null, ScanType.Departure, Now.AddMinutes(-3)), Pkg(P2, Ist)], ScanType.Departure, Ist, package: P2);
        Assert.Equal(DecisionOutcome.Applied, d.Outcome);
        Assert.Equal(ScanReasons.PackageLocationOnly, d.ReasonCode);
        Assert.Null(d.PackageHubAfter);
    }

    [Fact]
    public void Out_for_delivery_only_at_destination_with_all_packages_and_attempts_left()
    {
        var wrongHub = Decide(Ship(ShipmentStatus.AtHub, Ist), [Pkg(P1, Ist)], ScanType.OutForDelivery, Ist);
        Assert.Equal(ScanReasons.NotDestinationHub, wrongHub.ReasonCode);

        var missing = Decide(Ship(ShipmentStatus.AtHub, Ank, true), [Pkg(P1, Ank), Pkg(P2, null)], ScanType.OutForDelivery, Ank);
        Assert.Equal(ScanReasons.PackagesNotConsolidated, missing.ReasonCode);

        var maxed = Decide(Ship(ShipmentStatus.AtHub, Ank, true, attempts: 3), [Pkg(P1, Ank)], ScanType.OutForDelivery, Ank);
        Assert.Equal(ScanReasons.MaxAttemptsReached, maxed.ReasonCode);

        var granted = Decide(Ship(ShipmentStatus.AtHub, Ank, true, attempts: 3, extra: 1), [Pkg(P1, Ank)], ScanType.OutForDelivery, Ank);
        Assert.Equal(ShipmentStatus.OutForDelivery, granted.StatusAfter);

        var ok = Decide(Ship(ShipmentStatus.AtHub, Ank, true), [Pkg(P1, Ank), Pkg(P2, Ank)], ScanType.OutForDelivery, Ank);
        Assert.Equal(DecisionOutcome.Applied, ok.Outcome);
        Assert.Equal(ShipmentStatus.OutForDelivery, ok.StatusAfter);
    }

    [Fact]
    public void Reattempt_after_failure_allows_packages_still_with_courier()
    {
        var d = Decide(Ship(ShipmentStatus.DeliveryFailed, null, true, attempts: 1),
            [Pkg(P1, null, ScanType.OutForDelivery, Now.AddHours(-3)), Pkg(P2, Ank, ScanType.Arrival, Now.AddHours(-2))], ScanType.OutForDelivery, Ank, package: P2);
        Assert.Equal(DecisionOutcome.Applied, d.Outcome);
        Assert.Equal(ShipmentStatus.OutForDelivery, d.StatusAfter);
    }

    [Theory]
    [InlineData(ShipmentStatus.Delivered)]
    [InlineData(ShipmentStatus.ReturnedToSender)]
    [InlineData(ShipmentStatus.Cancelled)]
    public void Terminal_status_rejects_newer_and_stales_older_scans(ShipmentStatus terminal)
    {
        var newer = Decide(Ship(terminal, statusAt: Now.AddHours(-1)), [Pkg(P1, null)], ScanType.Arrival, Ist, Now.AddMinutes(-5));
        Assert.Equal(DecisionOutcome.Rejected, newer.Outcome);
        Assert.Equal(ScanReasons.TerminalState, newer.ReasonCode);

        var older = Decide(Ship(terminal, statusAt: Now.AddHours(-1)), [Pkg(P1, null)], ScanType.Arrival, Ist, Now.AddHours(-3));
        Assert.Equal(DecisionOutcome.Stale, older.Outcome);
    }

    [Fact]
    public void Clock_skew_rules()
    {
        Assert.Equal(ScanReasons.FutureTimestamp,
            Decide(Ship(ShipmentStatus.Created), [Pkg(P1, null)], ScanType.Arrival, Ist, Now.AddMinutes(16)).ReasonCode);
        Assert.Equal(DecisionOutcome.Applied,
            Decide(Ship(ShipmentStatus.Created, statusAt: Now.AddDays(-8)), [Pkg(P1, null)], ScanType.Arrival, Ist, Now.AddMinutes(14)).Outcome);
        Assert.Equal(ScanReasons.TooOld,
            Decide(Ship(ShipmentStatus.Created), [Pkg(P1, null)], ScanType.Arrival, Ist, Now.AddDays(-8)).ReasonCode);
    }

    [Fact]
    public void Business_duplicate_window_is_120_seconds()
    {
        var occurred = Now.AddMinutes(-10);
        var dup = Decide(Ship(ShipmentStatus.Created, statusAt: Now.AddHours(-3)), [Pkg(P1, null)], ScanType.Arrival, Ist, occurred, null, occurred.AddSeconds(120));
        Assert.Equal(DecisionOutcome.Duplicate, dup.Outcome);
        var fresh = Decide(Ship(ShipmentStatus.Created, statusAt: Now.AddHours(-3)), [Pkg(P1, null)], ScanType.Arrival, Ist, occurred, null, occurred.AddSeconds(121));
        Assert.Equal(DecisionOutcome.Applied, fresh.Outcome);
    }

    [Fact]
    public void Older_than_package_last_scan_is_stale()
    {
        var d = Decide(Ship(ShipmentStatus.AtHub, Ist), [Pkg(P1, Ist, ScanType.Arrival, Now.AddMinutes(-10))], ScanType.Arrival, Ist, Now.AddMinutes(-30));
        Assert.Equal(DecisionOutcome.Stale, d.Outcome);
        Assert.Equal(ScanReasons.PackageStale, d.ReasonCode);
    }

    [Fact]
    public void Status_changing_scan_older_than_status_is_stale_but_location_only_is_not()
    {
        // Gönderi 08:15'te AtHub oldu; 08:00 tarihli yeni bir varış durumu değiştirecek olsaydı stale olurdu.
        var statusAt = Now.AddMinutes(-45);
        var stale = Decide(Ship(ShipmentStatus.InTransit, statusAt: statusAt, departed: true), [Pkg(P1, null)], ScanType.Arrival, Ank, Now.AddMinutes(-60));
        Assert.Equal(DecisionOutcome.Stale, stale.Outcome);
        Assert.Equal(ScanReasons.ShipmentStale, stale.ReasonCode);

        // İkinci kolinin aynı hub'a daha erken varışı yalnız paket konumudur ve uygulanır.
        var locationOnly = Decide(Ship(ShipmentStatus.AtHub, Ank, true, statusAt: statusAt),
            [Pkg(P1, Ank, ScanType.Arrival, statusAt), Pkg(P2, null, ScanType.Departure, Now.AddHours(-5))], ScanType.Arrival, Ank, Now.AddMinutes(-60), P2);
        Assert.Equal(DecisionOutcome.Applied, locationOnly.Outcome);
        Assert.Equal(ScanReasons.PackageLocationOnly, locationOnly.ReasonCode);
    }

    [Fact]
    public void Returning_to_sender_scans_update_location_only()
    {
        var arrival = Decide(Ship(ShipmentStatus.ReturningToSender, null, true), [Pkg(P1, null)], ScanType.Arrival, Izm);
        Assert.Equal(DecisionOutcome.Applied, arrival.Outcome);
        Assert.False(arrival.ChangesStatus);
        Assert.Equal(Izm, arrival.ShipmentHubAfter);
    }
}
