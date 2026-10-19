using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Policies;

public sealed class RentalOrderStatusPolicyTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private static RentalOrder CreateOrderWithItemStatuses(params RentalItemStatus[] itemStatuses)
        => CreateOrderWithItemStatuses(false, itemStatuses);

    private static RentalOrder CreateOrderWithItemStatuses(bool markPaid, params RentalItemStatus[] itemStatuses)
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();

        var itemDtos = itemStatuses.Select((status, index) =>
        {
            Guid itemId = Guid.NewGuid();
            var assignments = new List<InitialRentalUnitAssignmentDto>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            };

            return new InitialRentalItemDto(
                itemId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                new DateOnly(2026, 10, 20),
                new DateOnly(2026, 10, 25),
                $"Listing {index + 1}",
                "Description",
                "Kho HCM",
                100_000m,
                500_000m,
                $"group-{index + 1}",
                assignments,
                TestListingSnapshots.Default);
        }).ToList();

        var order = RentalOrder.Create(
            orderId,
            $"ORD-{Guid.NewGuid():N}"[..12],
            renterId,
            "VND",
            "Pickup",
            "Nguyen Van A",
            "0901234567",
            "123 Duong 1",
            null,
            "v1.0",
            "terms-ref",
            "hash-123",
            "op-checkout",
            FixedNow,
            itemDtos).Value;
        if (markPaid)
        {
            order.MarkAsPaid("INV-1", "PAY-1", FixedNow, FixedNow);
        }

        // Force item statuses using reflection for testing policy combinations
        var items = order.Items.ToList();
        var statusProp = typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!;
        for (int i = 0; i < items.Count; i++)
        {
            statusProp.SetValue(items[i], itemStatuses[i]);
        }
        return order;
    }

    [Fact]
    public void CandidateStatus_MatchesSpecificationTable()
    {
        // 1. Còn PendingSupplierConfirmation -> Paid
        var order1 = CreateOrderWithItemStatuses(RentalItemStatus.PendingSupplierConfirmation, RentalItemStatus.AwaitingHandover);
        Assert.Equal(RentalOrderStatus.Paid, RentalOrderStatusPolicy.CalculateCandidateStatus(order1.Items));

        // 2. Có active item AwaitingHandover -> AwaitingHandover
        var order2 = CreateOrderWithItemStatuses(RentalItemStatus.AwaitingHandover, RentalItemStatus.InRental);
        Assert.Equal(RentalOrderStatus.AwaitingHandover, RentalOrderStatusPolicy.CalculateCandidateStatus(order2.Items));

        // 3. Có active item InRental -> InRental
        var order3 = CreateOrderWithItemStatuses(RentalItemStatus.InRental, RentalItemStatus.PendingSettlement);
        Assert.Equal(RentalOrderStatus.InRental, RentalOrderStatusPolicy.CalculateCandidateStatus(order3.Items));

        // 4. Có active item PendingSettlement -> PendingSettlement
        var order4 = CreateOrderWithItemStatuses(RentalItemStatus.PendingSettlement, RentalItemStatus.Completed);
        Assert.Equal(RentalOrderStatus.PendingSettlement, RentalOrderStatusPolicy.CalculateCandidateStatus(order4.Items));

        // 5. Có active item và tất cả đã Completed -> Completed
        var order5 = CreateOrderWithItemStatuses(RentalItemStatus.Completed, RentalItemStatus.Completed);
        Assert.Equal(RentalOrderStatus.Completed, RentalOrderStatusPolicy.CalculateCandidateStatus(order5.Items));

        // 6. Không còn active item, có Cancelled -> Cancelled
        var order6 = CreateOrderWithItemStatuses(RentalItemStatus.Cancelled, RentalItemStatus.Expired, RentalItemStatus.Rejected);
        Assert.Equal(RentalOrderStatus.Cancelled, RentalOrderStatusPolicy.CalculateCandidateStatus(order6.Items));

        // 7. Không còn active item, có Expired -> Expired
        var order7 = CreateOrderWithItemStatuses(RentalItemStatus.Expired, RentalItemStatus.Rejected);
        Assert.Equal(RentalOrderStatus.Expired, RentalOrderStatusPolicy.CalculateCandidateStatus(order7.Items));

        // 8. Tất cả item Rejected -> Rejected
        var order8 = CreateOrderWithItemStatuses(RentalItemStatus.Rejected, RentalItemStatus.Rejected);
        Assert.Equal(RentalOrderStatus.Rejected, RentalOrderStatusPolicy.CalculateCandidateStatus(order8.Items));
    }

    [Fact]
    public void FullLifecycle_AdvancesStepByStep()
    {
        var order = CreateOrderWithItemStatuses(RentalItemStatus.PendingSupplierConfirmation);
        order.MarkAsPaid("INV-1", "PAY-1", FixedNow, FixedNow);
        Assert.Equal(RentalOrderStatus.Paid, order.Status);

        var item = order.Items.Single();
        var statusProp = typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!;

        // 1. Paid -> AwaitingHandover
        statusProp.SetValue(item, RentalItemStatus.AwaitingHandover);
        bool changed = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(5), "op-accept");
        Assert.True(changed);
        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);

        // 2. AwaitingHandover -> InRental
        statusProp.SetValue(item, RentalItemStatus.InRental);
        changed = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(10), "op-handover");
        Assert.True(changed);
        Assert.Equal(RentalOrderStatus.InRental, order.Status);

        // 3. InRental -> PendingSettlement
        statusProp.SetValue(item, RentalItemStatus.PendingSettlement);
        changed = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(15), "op-return");
        Assert.True(changed);
        Assert.Equal(RentalOrderStatus.PendingSettlement, order.Status);

        // 4. PendingSettlement -> Completed
        statusProp.SetValue(item, RentalItemStatus.Completed);
        changed = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(20), "op-settle");
        Assert.True(changed);
        Assert.Equal(RentalOrderStatus.Completed, order.Status);

        // Transitions count: CreateOrder (1) + Paid (1) + 4 OrderRollup transitions = 6
        Assert.Equal(6, order.StateTransitions.Count);
        var rollups = order.StateTransitions.Where(t => t.Action == "OrderRollup").ToList();
        Assert.Equal(4, rollups.Count);
        Assert.Equal(["AwaitingHandover", "InRental", "PendingSettlement", "Completed"], rollups.Select(r => r.ToState));
    }

    [Fact]
    public void AdvanceDirectly_IfItemsFartherAhead_CreatesSingleTransitionWithoutFakeIntermediates()
    {
        var order = CreateOrderWithItemStatuses(RentalItemStatus.PendingSupplierConfirmation);
        order.MarkAsPaid("INV-1", "PAY-1", FixedNow, FixedNow);
        Assert.Equal(RentalOrderStatus.Paid, order.Status);

        var item = order.Items.Single();
        var statusProp = typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!;

        // Directly to Completed
        statusProp.SetValue(item, RentalItemStatus.Completed);
        bool changed = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(10), "op-direct");
        Assert.True(changed);
        Assert.Equal(RentalOrderStatus.Completed, order.Status);

        var rollups = order.StateTransitions.Where(t => t.Action == "OrderRollup").ToList();
        var singleRollup = Assert.Single(rollups);
        Assert.Equal("Paid", singleRollup.FromState);
        Assert.Equal("Completed", singleRollup.ToState);
        Assert.Equal("op-direct", singleRollup.OperationId);
    }

    [Theory]
    [InlineData(RentalItemStatus.AwaitingHandover, RentalItemStatus.InRental)]
    [InlineData(RentalItemStatus.InRental, RentalItemStatus.AwaitingHandover)]
    [InlineData(RentalItemStatus.InRental, RentalItemStatus.PendingSettlement)]
    [InlineData(RentalItemStatus.PendingSettlement, RentalItemStatus.InRental)]
    [InlineData(RentalItemStatus.PendingSettlement, RentalItemStatus.Completed)]
    [InlineData(RentalItemStatus.Completed, RentalItemStatus.PendingSettlement)]
    [InlineData(RentalItemStatus.Cancelled, RentalItemStatus.Expired)]
    [InlineData(RentalItemStatus.Expired, RentalItemStatus.Cancelled)]
    [InlineData(RentalItemStatus.Expired, RentalItemStatus.Rejected)]
    [InlineData(RentalItemStatus.Rejected, RentalItemStatus.Expired)]
    public void ItemOrdering_DoesNotAffectCandidateStatus(RentalItemStatus status1, RentalItemStatus status2)
    {
        var orderA = CreateOrderWithItemStatuses(status1, status2);
        var orderB = CreateOrderWithItemStatuses(status2, status1);

        var candidateA = RentalOrderStatusPolicy.CalculateCandidateStatus(orderA.Items);
        var candidateB = RentalOrderStatusPolicy.CalculateCandidateStatus(orderB.Items);

        Assert.Equal(candidateA, candidateB);
    }

    [Fact]
    public void Completed_MixedWithTerminalItems_RollsUpToCompleted()
    {
        // Active items are only Completed items; inactive terminal items do not block Completed
        var order1 = CreateOrderWithItemStatuses(RentalItemStatus.Completed, RentalItemStatus.Rejected);
        Assert.Equal(RentalOrderStatus.Completed, RentalOrderStatusPolicy.CalculateCandidateStatus(order1.Items));

        var order2 = CreateOrderWithItemStatuses(RentalItemStatus.Completed, RentalItemStatus.Expired);
        Assert.Equal(RentalOrderStatus.Completed, RentalOrderStatusPolicy.CalculateCandidateStatus(order2.Items));

        var order3 = CreateOrderWithItemStatuses(RentalItemStatus.Completed, RentalItemStatus.Cancelled);
        Assert.Equal(RentalOrderStatus.Completed, RentalOrderStatusPolicy.CalculateCandidateStatus(order3.Items));

        var order4 = CreateOrderWithItemStatuses(
            RentalItemStatus.Completed,
            RentalItemStatus.Rejected,
            RentalItemStatus.Expired,
            RentalItemStatus.Cancelled);
        Assert.Equal(RentalOrderStatus.Completed, RentalOrderStatusPolicy.CalculateCandidateStatus(order4.Items));
    }

    [Fact]
    public void TerminalPrecedence_CancelledOverExpiredOverRejected()
    {
        // Cancelled + Expired + Rejected -> Cancelled
        var order1 = CreateOrderWithItemStatuses(RentalItemStatus.Cancelled, RentalItemStatus.Expired, RentalItemStatus.Rejected);
        Assert.Equal(RentalOrderStatus.Cancelled, RentalOrderStatusPolicy.CalculateCandidateStatus(order1.Items));

        // Expired + Rejected -> Expired
        var order2 = CreateOrderWithItemStatuses(RentalItemStatus.Expired, RentalItemStatus.Rejected);
        Assert.Equal(RentalOrderStatus.Expired, RentalOrderStatusPolicy.CalculateCandidateStatus(order2.Items));

        // All Rejected -> Rejected
        var order3 = CreateOrderWithItemStatuses(RentalItemStatus.Rejected, RentalItemStatus.Rejected);
        Assert.Equal(RentalOrderStatus.Rejected, RentalOrderStatusPolicy.CalculateCandidateStatus(order3.Items));
    }

    [Fact]
    public void LifecycleProgression_BlocksBackwardTransitions()
    {
        var order = CreateOrderWithItemStatuses(true, RentalItemStatus.InRental);
        RentalOrderStatusPolicy.Apply(order, FixedNow, "op-handover");
        Assert.Equal(RentalOrderStatus.InRental, order.Status);

        var item = order.Items.Single();
        var statusProp = typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!;

        // Attempt backward to AwaitingHandover
        statusProp.SetValue(item, RentalItemStatus.AwaitingHandover);
        bool changed = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(5), "op-back");
        Assert.False(changed);
        Assert.Equal(RentalOrderStatus.InRental, order.Status);

        // Attempt backward to Paid
        statusProp.SetValue(item, RentalItemStatus.PendingSupplierConfirmation);
        changed = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(10), "op-back-paid");
        Assert.False(changed);
        Assert.Equal(RentalOrderStatus.InRental, order.Status);
    }

    [Theory]
    [InlineData(RentalOrderStatus.Completed)]
    [InlineData(RentalOrderStatus.Cancelled)]
    [InlineData(RentalOrderStatus.Expired)]
    [InlineData(RentalOrderStatus.Rejected)]
    public void TerminalOrders_NeverReopen(RentalOrderStatus terminalStatus)
    {
        var order = CreateOrderWithItemStatuses(RentalItemStatus.InRental);
        var statusProp = typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!;
        statusProp.SetValue(order, terminalStatus);

        bool changed = RentalOrderStatusPolicy.Apply(order, FixedNow, "op-reopen");
        Assert.False(changed);
        Assert.Equal(terminalStatus, order.Status);
    }

    [Fact]
    public void PendingPayment_OrEmptyItems_NeverRollsUp()
    {
        var order = CreateOrderWithItemStatuses(RentalItemStatus.PendingPayment);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);

        bool changed = RentalOrderStatusPolicy.Apply(order, FixedNow, "op-pending");
        Assert.False(changed);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);

        // Candidate with PendingPayment item never deduces Completed
        var candidate = RentalOrderStatusPolicy.CalculateCandidateStatus(order.Items);
        Assert.NotEqual(RentalOrderStatus.Completed, candidate);
    }

    [Fact]
    public void RepeatedRollupCalls_AreIdempotent()
    {
        var order = CreateOrderWithItemStatuses(true, RentalItemStatus.AwaitingHandover);

        bool first = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(1), "op-1");
        Assert.True(first);
        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);
        int transitionCount = order.StateTransitions.Count;
        DateTimeOffset updated = order.UpdatedAt;
        long version = order.Version;

        // Repeated call with same or later time
        bool second = RentalOrderStatusPolicy.Apply(order, FixedNow.AddMinutes(2), "op-1");
        Assert.False(second);
        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);
        Assert.Equal(transitionCount, order.StateTransitions.Count);
        Assert.Equal(updated, order.UpdatedAt);
        Assert.Equal(version, order.Version);
    }
}
