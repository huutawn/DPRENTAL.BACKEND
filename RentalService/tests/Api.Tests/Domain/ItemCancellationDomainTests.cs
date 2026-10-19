using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class ItemCancellationDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly StartDate = new(2026, 10, 22);
    private static readonly DateOnly EndDate = new(2026, 10, 27);

    private static RentalOrder CreateOrderWithStatus(RentalItemStatus itemStatus)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();

        var assignments = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };

        InitialRentalItemDto item = new(
            itemId,
            Guid.NewGuid(),
            supplierId,
            1,
            StartDate,
            EndDate,
            "Máy đầm cóc Mikasa MT-77H",
            "Thiết bị đầm đất công trình",
            "Công trường Quận 2, TP.HCM",
            300_000m,
            2_000_000m,
            "grp-mikasa-01",
            assignments,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            "ORD-CONST-001",
            renterId,
            "VND",
            "Pickup",
            "Nhà Thầu Xây Dựng",
            "0909123456",
            "Công trường Quận 2",
            null,
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create",
            FixedNow,
            [item]).Value;

        var targetItem = order.Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(targetItem, itemStatus);
        return order;
    }

    private static RentalOrder CreateTwoItemOrder(RentalItemStatus status1, RentalItemStatus status2)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();

        var assignments1 = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };
        var assignments2 = new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) };

        InitialRentalItemDto item1 = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            StartDate,
            EndDate,
            "Máy lu rung Sakai SV520",
            "Máy lu tĩnh và rung cho nền đường",
            "Công trường Quận 2",
            1_500_000m,
            10_000_000m,
            "grp-sakai-01",
            assignments1,
            TestListingSnapshots.Default);

        InitialRentalItemDto item2 = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            StartDate,
            EndDate,
            "Máy đầm cóc Mikasa MT-77H",
            "Máy đầm cóc thi công móng",
            "Công trường Quận 2",
            300_000m,
            2_000_000m,
            "grp-mikasa-02",
            assignments2,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            "ORD-MULTI-CONST-001",
            renterId,
            "VND",
            "Pickup",
            "Nhà Thầu Xây Dựng",
            "0909123456",
            "Công trường Quận 2",
            null,
            "terms-1",
            "doc-1",
            "hash-1",
            "op-create-multi",
            FixedNow,
            [item1, item2]).Value;

        var list = order.Items.ToList();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(list[0], status1);
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(list[1], status2);
        return order;
    }

    [Fact]
    public void ApplyNotDeliveredCancellation_WhenAwaitingHandover_TransitionsToCancelled()
    {
        var order = CreateOrderWithStatus(RentalItemStatus.AwaitingHandover);
        var item = order.Items.Single();
        int initialTransitions = order.StateTransitions.Count;

        var result = item.ApplyNotDeliveredCancellation(
            order.RenterId,
            ActorRole.Renter,
            FixedNow,
            "Không giao máy đầm đúng hẹn",
            "op-cancel-01");

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalItemStatus.Cancelled, item.Status);
        Assert.Equal(initialTransitions + 1, order.StateTransitions.Count);

        var transition = order.StateTransitions.Last();
        Assert.Equal(RentalItem.CancelNotDeliveredAction, transition.Action);
        Assert.Equal(RentalItemStatus.AwaitingHandover.ToString(), transition.FromState);
        Assert.Equal(RentalItemStatus.Cancelled.ToString(), transition.ToState);
        Assert.Equal(ActorRole.Renter, transition.ActorRole);
        Assert.Equal("Không giao máy đầm đúng hẹn", transition.Reason);
    }

    [Theory]
    [InlineData(RentalItemStatus.PendingSupplierConfirmation)]
    [InlineData(RentalItemStatus.InRental)]
    [InlineData(RentalItemStatus.Completed)]
    [InlineData(RentalItemStatus.Rejected)]
    public void ApplyNotDeliveredCancellation_WhenNotAwaitingHandover_ReturnsInvalidItemStatus(RentalItemStatus invalidStatus)
    {
        var order = CreateOrderWithStatus(invalidStatus);
        var item = order.Items.Single();
        int initialTransitions = order.StateTransitions.Count;

        var result = item.ApplyNotDeliveredCancellation(
            order.RenterId,
            ActorRole.Renter,
            FixedNow,
            "Hủy",
            "op-cancel-err");

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidItemStatus.Code, result.Error.Code);
        Assert.Equal(invalidStatus, item.Status);
        Assert.Equal(initialTransitions, order.StateTransitions.Count);
    }

    [Fact]
    public void ApplyNotDeliveredCancellation_WhenAlreadyCancelled_IsIdempotentWithoutNewTransition()
    {
        var order = CreateOrderWithStatus(RentalItemStatus.AwaitingHandover);
        var item = order.Items.Single();

        var first = item.ApplyNotDeliveredCancellation(
            order.RenterId,
            ActorRole.Renter,
            FixedNow,
            "Lần 1",
            "op-cancel-01");
        Assert.True(first.IsSuccess);
        int transitionsCount = order.StateTransitions.Count;

        var second = item.ApplyNotDeliveredCancellation(
            order.RenterId,
            ActorRole.Renter,
            FixedNow.AddMinutes(5),
            "Lần 2",
            "op-cancel-02");

        Assert.True(second.IsSuccess);
        Assert.Equal(RentalItemStatus.Cancelled, item.Status);
        Assert.Equal(transitionsCount, order.StateTransitions.Count);
    }

    [Fact]
    public void EvaluateOrderStatusRollup_WhenAllItemsCancelled_RollsUpToCancelled()
    {
        var order = CreateTwoItemOrder(RentalItemStatus.Cancelled, RentalItemStatus.Cancelled);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!.SetValue(order, RentalOrderStatus.AwaitingHandover);

        order.EvaluateOrderStatusRollup(FixedNow, "op-rollup-cancelled");

        Assert.Equal(RentalOrderStatus.Cancelled, order.Status);
        var transition = order.StateTransitions.Last();
        Assert.Equal("OrderRollup", transition.Action);
        Assert.Equal(RentalOrderStatus.Cancelled.ToString(), transition.ToState);
    }

    [Fact]
    public void EvaluateOrderStatusRollup_WhenOneInRentalAndOneCancelled_RollsUpToInRental()
    {
        var order = CreateTwoItemOrder(RentalItemStatus.InRental, RentalItemStatus.Cancelled);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!.SetValue(order, RentalOrderStatus.AwaitingHandover);

        order.EvaluateOrderStatusRollup(FixedNow, "op-rollup-inrental");

        Assert.Equal(RentalOrderStatus.InRental, order.Status);
    }

    [Fact]
    public void EvaluateOrderStatusRollup_WhenOneAwaitingHandoverAndOneCancelled_RemainsAwaitingHandover()
    {
        var order = CreateTwoItemOrder(RentalItemStatus.AwaitingHandover, RentalItemStatus.Cancelled);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!.SetValue(order, RentalOrderStatus.AwaitingHandover);

        order.EvaluateOrderStatusRollup(FixedNow, "op-rollup-awaiting");

        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);
    }

    [Fact]
    public void EvaluateOrderStatusRollup_WhenOneRejectedAndOneCancelled_RollsUpToCancelled()
    {
        var order = CreateTwoItemOrder(RentalItemStatus.Rejected, RentalItemStatus.Cancelled);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!.SetValue(order, RentalOrderStatus.AwaitingHandover);

        order.EvaluateOrderStatusRollup(FixedNow, "op-rollup-mixed-terminal");

        Assert.Equal(RentalOrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void HandoverOverdue_And_AutoCancellable_Milestones_AreCalculatedProperly()
    {
        var order = CreateOrderWithStatus(RentalItemStatus.AwaitingHandover);
        var item = order.Items.Single();

        // StartDate = 2026-10-22, EndDate = 2026-10-27
        // Quá hạn giao: 00:00 VN ngày 23/10/2026 = 17:00 UTC ngày 22/10/2026
        DateTimeOffset beforeOverdue = new(2026, 10, 22, 23, 59, 59, TimeSpan.FromHours(7));
        DateTimeOffset atOverdue = new(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(7));

        Assert.False(item.IsHandoverOverdue(beforeOverdue));
        Assert.True(item.IsHandoverOverdue(atOverdue));

        // Tự hủy: 00:00 VN ngày 28/10/2026 (sau ngày kết thúc 27/10)
        DateTimeOffset beforeAutoCancel = new(2026, 10, 27, 23, 59, 59, TimeSpan.FromHours(7));
        DateTimeOffset atAutoCancel = new(2026, 10, 28, 0, 0, 0, TimeSpan.FromHours(7));

        Assert.False(item.IsHandoverAutoCancellable(beforeAutoCancel));
        Assert.True(item.IsHandoverAutoCancellable(atAutoCancel));
    }
}
