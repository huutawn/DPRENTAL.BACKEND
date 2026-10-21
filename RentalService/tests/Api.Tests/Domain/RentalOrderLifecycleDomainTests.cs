using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Application.Policies;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderLifecycleDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private static RentalOrder CreateTestOrder(DateOnly startDate, DateOnly endDate, int quantity = 2)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();

        var assignments = Enumerable.Range(0, quantity)
            .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        InitialRentalItemDto item = new(
            itemId,
            Guid.NewGuid(),
            supplierId,
            quantity,
            startDate,
            endDate,
            "Camera Sony",
            "Description",
            "Pickup",
            200_000m,
            500_000m,
            "grp-01",
            assignments,
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            orderId,
            "ORD-TEST-001",
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create",
            FixedNow,
            [item]).Value;
    }

    private static RentalOrder CreateMultiItemOrder()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();

        var item1Assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var item2Assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };

        InitialRentalItemDto item1 = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 18),
            "Camera A",
            null,
            "Pickup",
            200_000m,
            500_000m,
            "grp-01",
            item1Assignments,
            TestListingSnapshots.Default);

        InitialRentalItemDto item2 = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 18),
            "Camera B",
            null,
            "Pickup",
            300_000m,
            600_000m,
            "grp-02",
            item2Assignments,
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            orderId,
            "ORD-MULTI-001",
            renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909123456",
            "Address",
            null,
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create-multi",
            FixedNow,
            [item1, item2]).Value;
    }

    [Fact]
    public void DeadlineCalculation_WhenRentalFarAway_UsesPaidAtPlus24HoursInUtc()
    {
        DateTimeOffset paidAt = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);
        DateOnly expectedStartDate = new(2026, 10, 15);

        DateTimeOffset deadline = RentalItem.CalculateSupplierConfirmationDeadline(paidAt, expectedStartDate);

        Assert.Equal(TimeSpan.Zero, deadline.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero), deadline);
    }

    [Fact]
    public void DeadlineCalculation_WhenRentalStartsTomorrow_ShortenedToEndOfTodayInVietnamTime()
    {
        // Paid at 2026-10-08 10:00:00 UTC (17:00:00 VN)
        // Rental starts tomorrow: 2026-10-09
        // 00:00 on 2026-10-09 in VN time minus 1s = 2026-10-08 23:59:59 +07:00 = 2026-10-08 16:59:59 UTC
        DateTimeOffset paidAt = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
        DateOnly expectedStartDate = new(2026, 10, 9);

        DateTimeOffset deadline = RentalItem.CalculateSupplierConfirmationDeadline(paidAt, expectedStartDate);

        Assert.Equal(TimeSpan.Zero, deadline.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 16, 59, 59, TimeSpan.Zero), deadline);
    }

    [Fact]
    public void MarkAsPaid_TransitionsOrderAndItems_AndSetsFixedDeadline()
    {
        var order = CreateTestOrder(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));
        DateTimeOffset paidAt = new(2026, 10, 8, 8, 30, 0, TimeSpan.Zero);

        var result = order.MarkAsPaid("INV-001", "PAY-001", paidAt, FixedNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalOrderStatus.Paid, order.Status);
        Assert.Equal("INV-001", order.InvoiceRef);
        Assert.Equal("PAY-001", order.PaymentRef);
        Assert.Equal(paidAt, order.PaymentConfirmedAt);

        var item = order.Items.First();
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, item.Status);
        Assert.NotNull(item.SupplierConfirmationDeadline);
        Assert.Equal(paidAt.AddHours(24), item.SupplierConfirmationDeadline.Value);

        // Retry invariant: deadline remains fixed
        DateTimeOffset originalDeadline = item.SupplierConfirmationDeadline.Value;
        item.RecordPaid(paidAt.AddHours(2));
        Assert.Equal(originalDeadline, item.SupplierConfirmationDeadline.Value);
    }

    [Fact]
    public void MarkAsPaid_Fails_WhenOrderNotPendingPayment()
    {
        var order = CreateTestOrder(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        var secondCall = order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        Assert.True(secondCall.IsFailure);
        Assert.Equal("Order.NotPendingPayment", secondCall.Error.Code);
    }

    [Fact]
    public void MarkAsLatePayment_PreservesTerminalStatus_AndSetsRefundRequiredFlag()
    {
        var order = CreateTestOrder(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));
        order.MarkPaymentExpired(FixedNow, "op-expire");
        Assert.Equal(RentalOrderStatus.Expired, order.Status);

        order.MarkAsLatePayment("INV-LATE", "PAY-LATE", FixedNow.AddHours(1), FixedNow.AddHours(1));

        Assert.Equal(RentalOrderStatus.Expired, order.Status);
        Assert.True(order.LatePaymentRefundRequired);
        Assert.Equal("INV-LATE", order.InvoiceRef);
        Assert.Equal("PAY-LATE", order.PaymentRef);
    }

    [Fact]
    public void SupplierAccept_TransitionsItemToAwaitingHandover_AndRollsUpOrder()
    {
        var order = CreateTestOrder(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        var item = order.Items.First();
        Guid supplierId = item.SupplierId;
        DateTimeOffset acceptTime = FixedNow.AddMinutes(15);

        item.ApplySupplierAccept(supplierId, acceptTime, "CONF-INV-001", "op-accept");
        RentalOrderStatusPolicy.Apply(order, acceptTime, "op-accept");

        Assert.Equal(RentalItemStatus.AwaitingHandover, item.Status);
        Assert.Equal(supplierId, item.SupplierDecisionBy);
        Assert.Equal(acceptTime, item.SupplierDecisionAt);
        Assert.Equal("CONF-INV-001", item.InventoryConfirmationRef);

        // Order rollup moves to AwaitingHandover
        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);
    }

    [Fact]
    public void SupplierReject_ReleasesAssignments_TransitionsToRejected()
    {
        var order = CreateTestOrder(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));
        order.MarkAsPaid("INV-001", "PAY-001", FixedNow, FixedNow);

        var item = order.Items.First();
        Guid supplierId = item.SupplierId;
        DateTimeOffset rejectTime = FixedNow.AddMinutes(20);

        item.ApplySupplierReject(supplierId, rejectTime, "Device undergoing repair", "Optional note", "op-reject");
        RentalOrderStatusPolicy.Apply(order, rejectTime, "op-reject");

        Assert.Equal(RentalItemStatus.Rejected, item.Status);
        Assert.Equal("Device undergoing repair", item.SupplierDecisionReason);
        Assert.Equal("Optional note", item.SupplierDecisionNote);
        Assert.Equal(supplierId, item.SupplierDecisionBy);
        // Assignments remain Assigned until Inventory confirms release
        Assert.All(item.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Assigned, u.Status));
        item.MarkAssignmentsReleased(rejectTime);
        Assert.All(item.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Released, u.Status));
        // Single item order -> all rejected -> Order moves to Rejected
        Assert.Equal(RentalOrderStatus.Rejected, order.Status);
    }

    [Fact]
    public void MultiItemOrderRollup_OneAcceptedOneWaiting_RemainsPaid()
    {
        var order = CreateMultiItemOrder();
        order.MarkAsPaid("INV-MULTI", "PAY-MULTI", FixedNow, FixedNow);

        var items = order.Items.ToList();
        items[0].ApplySupplierAccept(items[0].SupplierId, FixedNow.AddMinutes(5), "CONF-1", "op-acc1");
        RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(5), "op-acc1");

        // Item 1 accepted, item 2 still waiting -> Order status must remain Paid
        Assert.Equal(RentalOrderStatus.Paid, order.Status);
    }

    [Fact]
    public void MultiItemOrderRollup_OneAcceptedOneRejected_MovesToAwaitingHandover()
    {
        var order = CreateMultiItemOrder();
        order.MarkAsPaid("INV-MULTI", "PAY-MULTI", FixedNow, FixedNow);

        var items = order.Items.ToList();
        items[0].ApplySupplierAccept(items[0].SupplierId, FixedNow.AddMinutes(5), "CONF-1", "op-acc1");
        items[1].ApplySupplierReject(items[1].SupplierId, FixedNow.AddMinutes(10), "Out of stock", null, "op-rej2");
        RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(10), "op-rej2");

        Assert.Equal(RentalItemStatus.AwaitingHandover, items[0].Status);
        Assert.Equal(RentalItemStatus.Rejected, items[1].Status);
        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);
    }

    [Fact]
    public void MultiItemOrderRollup_OneRejectedOneExpired_MovesToExpired()
    {
        var order = CreateMultiItemOrder();
        order.MarkAsPaid("INV-MULTI", "PAY-MULTI", FixedNow, FixedNow);

        var items = order.Items.ToList();
        items[0].ApplySupplierReject(items[0].SupplierId, FixedNow.AddMinutes(5), "Busy", null, "op-rej1");
        items[1].ApplySupplierTimeout(FixedNow.AddHours(25), "op-timeout2");
        RentalOrderStatusPolicy.Apply(order, FixedNow.AddHours(25), "op-timeout2");

        Assert.Equal(RentalItemStatus.Rejected, items[0].Status);
        Assert.Equal(RentalItemStatus.Expired, items[1].Status);
        Assert.Equal(RentalOrderStatus.Expired, order.Status);
    }

    [Fact]
    public void MarkCancelled_LeavesAssignmentsAssignedUntilInventoryConfirmsRelease()
    {
        var order = CreateTestOrder(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        order.MarkCancelled(FixedNow, "cancel-request-1", "Changed plans", order.RenterId, ActorRole.Renter);

        Assert.Equal(RentalOrderStatus.Cancelled, order.Status);
        var item = order.Items.First();
        Assert.Equal(RentalItemStatus.Cancelled, item.Status);
        Assert.All(item.UnitAssignments, unit => Assert.Equal(RentalUnitAssignmentStatus.Assigned, unit.Status));

        item.MarkAssignmentsReleased(FixedNow.AddMinutes(1));
        Assert.All(item.UnitAssignments, unit => Assert.Equal(RentalUnitAssignmentStatus.Released, unit.Status));
    }

    [Fact]
    public void MarkPaymentExpired_ExpiresOrderAndAllItems_AndReleasesUnits()
    {
        var order = CreateTestOrder(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));
        order.MarkPaymentExpired(FixedNow, "op-exp-pay");

        Assert.Equal(RentalOrderStatus.Expired, order.Status);
        var item = order.Items.First();
        Assert.Equal(RentalItemStatus.Expired, item.Status);
        Assert.All(item.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Released, u.Status));
    }
}
