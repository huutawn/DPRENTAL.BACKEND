using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderCompletionAndReceiptsTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private (RentalOrder Order, RentalItem Item, List<RentalUnitAssignment> Units) CreateInRentalOrder(int quantity = 2)
    {
        List<InitialRentalUnitAssignmentDto> assignments = [];
        for (int i = 0; i < quantity; i++)
        {
            assignments.Add(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        }

        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplierId,
            quantity,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Cinema Camera",
            null,
            "District 1",
            100_000m,
            200_000m,
            "group-01",
            assignments,
            TestListingSnapshots.Default);

        RentalOrder order = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-COMP-001",
            _renterId,
            "VND",
            "Pickup",
            "Recipient",
            "0909090909",
            "Address",
            null,
            "refund-ref",
            "v1.0",
            "terms-doc",
            "terms-hash",
            "op-create-comp",
            _now,
            [itemDto]).Value;

        order.ConfirmPayment("INV-001", "PAY-001", _now, TimeSpan.FromHours(24), "op-pay");
        order.SupplierAcceptItem(order.Items.First().Id, _supplierId, "INV-CONF-01", _now.AddHours(1), "op-acc");

        RentalItem item = order.Items.First();
        var units = item.UnitAssignments.ToList();
        var observations = units.Select(u => new HandoverObservationDto(u.Id, UnitCondition.Normal, "Handed over")).ToList();

        order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), observations, null, _now.AddDays(1), "op-hand");
        return (order, item, units);
    }

    [Fact]
    public void SubmitReturn_DoesNotSetAllocationClosedAt_UntilInventoryReceiptConfirmed()
    {
        var (order, item, units) = CreateInRentalOrder(2);

        // Return first unit normal
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), [new(units[0].Id, UnitCondition.Normal)], null, _now.AddDays(5), "op-ret-1");

        // AllocationClosedAt must remain null!
        Assert.Null(units[0].AllocationClosedAt);

        // Record inventory closure for unit 0
        order.RecordInventoryClosureReceipt(item.Id, "INV-CLOSURE-U0", _now.AddDays(5).AddHours(1), "op-inv-close-u0", [units[0].Id]);
        Assert.NotNull(units[0].AllocationClosedAt);
        Assert.Null(units[1].AllocationClosedAt);
    }

    [Fact]
    public void Completion_Fails_WhenDeductionProposalIsSubmitted_AndNotYetResponded()
    {
        var (order, item, units) = CreateInRentalOrder(1);

        // Return damaged
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), [new(units[0].Id, UnitCondition.Damaged, "Scratch", null)], null, _now.AddDays(5), "op-ret-dam");
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);

        // Submit proposal
        order.SubmitDeductionProposal(item.Id, _supplierId, [new(units[0].Id, 100_000m, "Repair")], _now.AddDays(5).AddHours(2), "op-prop");

        // Attempting to complete while proposal is still Submitted fails
        Result completeRes = order.ConfirmClosuresAndComplete(item.Id, _now.AddDays(6), _now.AddDays(6), "SETTLE-01", null, _now.AddDays(6), "op-comp-early");
        Assert.True(completeRes.IsFailure);
        Assert.Equal("Deduction.ProposalPendingResponse", completeRes.Error.Code);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
    }

    [Fact]
    public void Completion_Fails_WhenDeductionDeadlineNotYetExpired_AndNoProposal()
    {
        var (order, item, units) = CreateInRentalOrder(1);

        // Return damaged
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), [new(units[0].Id, UnitCondition.Damaged, "Scratch", null)], null, _now.AddDays(5), "op-ret-dam");
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);

        // 10 hours after return (< 48h deadline)
        DateTimeOffset beforeDeductionDeadline = _now.AddDays(5).AddHours(10);
        Result completeRes = order.ConfirmClosuresAndComplete(item.Id, beforeDeductionDeadline, beforeDeductionDeadline, "SETTLE-01", null, beforeDeductionDeadline, "op-comp-before-deadline");

        Assert.True(completeRes.IsFailure);
        Assert.Equal("Deduction.DeadlineNotExpired", completeRes.Error.Code);
    }

    [Fact]
    public void ReverseReceipts_PaymentFirstThenInventory_TransitionsToCompleted()
    {
        var (order, item, units) = CreateInRentalOrder(2);

        // Return all normal
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), units.Select(u => new ReturnObservationDto(u.Id, UnitCondition.Normal)).ToList(), null, _now.AddDays(5), "op-ret-all");
        Assert.Equal(RentalItemStatus.InRental, item.Status);
        Assert.True(item.AreAllUnitsReturned);

        // 1. Payment settlement receipt arrives first
        Result payRes = order.RecordPaymentSettlementReceipt(item.Id, "SETTLE-PAY-001", null, _now.AddDays(5).AddHours(2), "op-pay-settle");
        Assert.True(payRes.IsSuccess);
        Assert.Equal(RentalItemStatus.InRental, item.Status); // Not yet completed, waiting for Inventory closure
        Assert.NotNull(item.FinancialClosureConfirmedAt);
        Assert.Null(item.InventoryClosureConfirmedAt);

        // 2. Inventory closure receipt arrives second
        Result invRes = order.RecordInventoryClosureReceipt(item.Id, "INV-CLOSE-001", _now.AddDays(5).AddHours(3), "op-inv-close");
        Assert.True(invRes.IsSuccess);
        Assert.Equal(RentalItemStatus.Completed, item.Status);
        Assert.Equal(RentalOrderStatus.Completed, order.Status);
        Assert.NotNull(item.InventoryClosureConfirmedAt);
        Assert.All(units, u => Assert.NotNull(u.AllocationClosedAt));
    }

    [Fact]
    public void ReverseReceipts_InventoryFirstThenPayment_TransitionsToCompleted()
    {
        var (order, item, units) = CreateInRentalOrder(2);

        // Return all normal
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), units.Select(u => new ReturnObservationDto(u.Id, UnitCondition.Normal)).ToList(), null, _now.AddDays(5), "op-ret-all");

        // 1. Inventory closure receipt arrives first
        Result invRes = order.RecordInventoryClosureReceipt(item.Id, "INV-CLOSE-002", _now.AddDays(5).AddHours(1), "op-inv-close-2");
        Assert.True(invRes.IsSuccess);
        Assert.Equal(RentalItemStatus.InRental, item.Status); // Not yet completed, waiting for financial closure
        Assert.NotNull(item.InventoryClosureConfirmedAt);
        Assert.Null(item.FinancialClosureConfirmedAt);

        // 2. Payment settlement receipt arrives second
        Result payRes = order.RecordPaymentSettlementReceipt(item.Id, "SETTLE-PAY-002", "REFUND-002", _now.AddDays(5).AddHours(2), "op-pay-settle-2");
        Assert.True(payRes.IsSuccess);
        Assert.Equal(RentalItemStatus.Completed, item.Status);
        Assert.Equal(RentalOrderStatus.Completed, order.Status);
    }
}
