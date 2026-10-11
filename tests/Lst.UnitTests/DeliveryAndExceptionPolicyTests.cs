using Lst.Modules.Operations.Domain;
using Lst.Modules.Shipments.Domain;

namespace Lst.UnitTests;

public sealed class DeliveryAndExceptionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ank = Guid.NewGuid();

    private static ShipmentSnapshot Ofd(DateTimeOffset? statusAt = null) =>
        new(ShipmentStatus.OutForDelivery, statusAt ?? Now.AddHours(-1), null, Ank, true, 0, 0);

    private static PackageSnapshot Out(Guid? hub = null) => new(Guid.NewGuid(), hub, hub is null ? ScanType.OutForDelivery : ScanType.Arrival, Now.AddHours(-1));

    [Fact]
    public void Delivered_requires_every_package_out_for_delivery()
    {
        Assert.Null(DeliveryAttemptPolicy.Validate(Ofd(), [Out(), Out()], DeliveryOutcome.Delivered, null, null, Now.AddMinutes(-5), Now));
        Assert.Equal(DeliveryAttemptPolicy.PackagesNotOutForDelivery,
            DeliveryAttemptPolicy.Validate(Ofd(), [Out(), Out(Ank)], DeliveryOutcome.Delivered, null, null, Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void Missing_package_is_recorded_as_failed_attempt()
    {
        Assert.Null(DeliveryAttemptPolicy.Validate(Ofd(), [Out(), Out(Ank)], DeliveryOutcome.Failed, DeliveryFailureReason.PackageMissing, null, Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void Failed_requires_reason_and_other_requires_note()
    {
        Assert.Equal(DeliveryAttemptPolicy.ReasonRequired, DeliveryAttemptPolicy.Validate(Ofd(), [Out()], DeliveryOutcome.Failed, null, null, Now, Now));
        Assert.Equal(DeliveryAttemptPolicy.NoteRequired,
            DeliveryAttemptPolicy.Validate(Ofd(), [Out()], DeliveryOutcome.Failed, DeliveryFailureReason.Other, " ", Now, Now));
    }

    [Theory]
    [InlineData(ShipmentStatus.Created)]
    [InlineData(ShipmentStatus.AtHub)]
    [InlineData(ShipmentStatus.DeliveryFailed)]
    [InlineData(ShipmentStatus.Delivered)]
    [InlineData(ShipmentStatus.ReturningToSender)]
    public void Attempt_only_while_out_for_delivery(ShipmentStatus status)
    {
        var snapshot = new ShipmentSnapshot(status, Now.AddHours(-1), null, Ank, true, 0, 0);
        Assert.Equal(DeliveryAttemptPolicy.NotOutForDelivery,
            DeliveryAttemptPolicy.Validate(snapshot, [Out()], DeliveryOutcome.Delivered, null, null, Now, Now));
    }

    [Fact]
    public void Attempt_time_must_be_after_out_for_delivery_and_not_in_future()
    {
        Assert.Equal(DeliveryAttemptPolicy.BeforeOutForDelivery,
            DeliveryAttemptPolicy.Validate(Ofd(Now.AddMinutes(-10)), [Out()], DeliveryOutcome.Delivered, null, null, Now.AddMinutes(-20), Now));
        Assert.Equal(DeliveryAttemptPolicy.FutureTimestamp,
            DeliveryAttemptPolicy.Validate(Ofd(), [Out()], DeliveryOutcome.Delivered, null, null, Now.AddMinutes(20), Now));
    }

    [Fact]
    public void Exception_lifecycle_open_occurrence_assign_resolve()
    {
        var exception = ShipmentException.Open(Guid.NewGuid(), ExceptionType.MaxAttemptsReached, ExceptionSeverity.High, Ank, "HubScan", Now);
        Assert.Equal(Now.AddHours(4), exception.DueAt);
        exception.RecordOccurrence(Now.AddMinutes(5));
        Assert.Equal(2, exception.OccurrenceCount);
        var user = Guid.NewGuid();
        exception.Assign(user, Now.AddMinutes(6));
        Assert.Equal(ExceptionStatus.Assigned, exception.Status);
        Assert.Throws<InvalidOperationException>(() => exception.Resolve(ResolutionCode.NoActionNeeded, "not", user, Now));
        exception.Resolve(ResolutionCode.ReattemptApproved, "Ek deneme onaylandı", user, Now.AddMinutes(7));
        Assert.False(exception.IsOpen);
        Assert.Throws<InvalidOperationException>(() => exception.Assign(user, Now));
        Assert.Throws<InvalidOperationException>(() => exception.RecordOccurrence(Now));
    }

    [Fact]
    public void Carrier_exceptions_do_not_auto_resolve_on_terminal()
    {
        Assert.True(ExceptionResolutionPolicy.AutoResolvesOnTerminal(ExceptionType.DeliveryFailed));
        Assert.True(ExceptionResolutionPolicy.AutoResolvesOnTerminal(ExceptionType.PackageSplit));
        Assert.False(ExceptionResolutionPolicy.AutoResolvesOnTerminal(ExceptionType.CarrierEventConflict));
        Assert.False(ExceptionResolutionPolicy.AutoResolvesOnTerminal(ExceptionType.CarrierEventPendingReview));
    }

    [Fact]
    public void Every_type_has_resolutions_and_auto_resolved_is_never_user_selectable()
    {
        foreach (var type in Enum.GetValues<ExceptionType>())
        {
            Assert.NotEmpty(ExceptionResolutionPolicy.AllowedFor(type));
            Assert.False(ExceptionResolutionPolicy.IsAllowed(type, ResolutionCode.AutoResolved));
        }
    }
}
