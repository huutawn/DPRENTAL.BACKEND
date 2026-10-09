using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderSettlementAndDisputeTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private (RentalOrder Order, RentalItem Item, RentalUnitAssignment Unit) CreateItemInPendingSettlement(decimal deposit = 500_000m)
    {
        Guid unitId = Guid.NewGuid();
        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera Kit",
            null,
            "District 1",
            100_000m,
            deposit,
            "group-01",
            [new(unitId, Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        RentalOrder order = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-SETTLE-001",
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
            "op-settle",
            _now,
            [itemDto]).Value;

        order.ConfirmPayment("INV-001", "PAY-001", _now, TimeSpan.FromHours(24), "op-pay");
        order.SupplierAcceptItem(order.Items.First().Id, _supplierId, _now.AddHours(1), "op-acc");

        RentalItem item = order.Items.First();
        RentalUnitAssignment unit = item.UnitAssignments.First();

        order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), [new(unit.Id, UnitCondition.Normal)], null, _now.AddDays(1), "op-hand");
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), [new(unit.Id, UnitCondition.Damaged, "Scratched sensor", [Guid.NewGuid()])], "Damage recorded", _now.AddDays(5), "op-ret");

        return (order, item, unit);
    }

    [Fact]
    public void SubmitDeductionProposal_AmountExceedingDeposit_Fails()
    {
        var (order, item, unit) = CreateItemInPendingSettlement(deposit: 500_000m);

        // Propose 600,000 VND when deposit is only 500,000 VND
        List<DeductionLineDto> lines =
        [
            new(unit.Id, 600_000m, "Sensor replacement")
        ];

        Result result = order.SubmitDeductionProposal(item.Id, _supplierId, lines, _now.AddDays(5).AddHours(2), "op-prop-fail");
        Assert.True(result.IsFailure);
        Assert.Equal("Deduction.AmountExceedsDeposit", result.Error.Code);
    }

    [Fact]
    public void SubmitDeductionProposal_After48Hours_FailsWithDeadlineExpired()
    {
        var (order, item, unit) = CreateItemInPendingSettlement(deposit: 500_000m);

        DateTimeOffset pastDeadline = _now.AddDays(5).AddHours(49); // Returned on day 5 + 49h > 48h deadline
        Result result = order.SubmitDeductionProposal(item.Id, _supplierId, [new(unit.Id, 200_000m, "Repair")], pastDeadline, "op-late");

        Assert.True(result.IsFailure);
        Assert.Equal("Deduction.DeadlineExpired", result.Error.Code);
    }

    [Fact]
    public void SupplierTimeout_ResultsInZeroDeduction()
    {
        var (order, item, _) = CreateItemInPendingSettlement(deposit: 500_000m);

        Result result = order.ExpireDeductionSubmission(item.Id, _now.AddDays(5).AddHours(50), "op-exp-ded");
        Assert.True(result.IsSuccess);
        Assert.Equal(0m, item.FinalDeductionAmount);
    }

    [Fact]
    public void RenterAcceptsProposal_SetsFinalDeductionToProposedAmount()
    {
        var (order, item, unit) = CreateItemInPendingSettlement(deposit: 500_000m);

        order.SubmitDeductionProposal(item.Id, _supplierId, [new(unit.Id, 300_000m, "Repair")], _now.AddDays(5).AddHours(1), "op-prop");

        Result result = order.AcceptDeductionProposal(item.Id, _renterId, _now.AddDays(5).AddHours(2), "op-accept");
        Assert.True(result.IsSuccess);
        Assert.Equal(300_000m, item.FinalDeductionAmount);
        Assert.Equal(DeductionProposalStatus.Accepted, item.DeductionProposal!.Status);
    }

    [Fact]
    public void RenterResponseTimeout_AcceptsProposalByTimeout()
    {
        var (order, item, unit) = CreateItemInPendingSettlement(deposit: 500_000m);

        DateTimeOffset submitTime = _now.AddDays(5).AddHours(1);
        order.SubmitDeductionProposal(item.Id, _supplierId, [new(unit.Id, 300_000m, "Repair")], submitTime, "op-prop");

        DateTimeOffset responseDeadlinePassed = submitTime.AddHours(49);
        Result result = order.TimeoutDeductionProposal(item.Id, responseDeadlinePassed, "op-prop-timeout");

        Assert.True(result.IsSuccess);
        Assert.Equal(300_000m, item.FinalDeductionAmount);
        Assert.Equal(DeductionProposalStatus.AcceptedByTimeout, item.DeductionProposal!.Status);
    }

    [Fact]
    public void DisputeFlow_AdminResolvesWithinLimits_AndCompletesItem()
    {
        var (order, item, unit) = CreateItemInPendingSettlement(deposit: 500_000m);

        order.SubmitDeductionProposal(item.Id, _supplierId, [new(unit.Id, 400_000m, "Heavy damage")], _now.AddDays(5).AddHours(1), "op-prop");

        // Renter disputes
        Result disputeRes = order.DisputeDeductionProposal(item.Id, _renterId, "Damage was pre-existing", [Guid.NewGuid()], _now.AddDays(5).AddHours(2), "op-dispute");
        Assert.True(disputeRes.IsSuccess);
        Assert.Equal(DeductionProposalStatus.Disputed, item.DeductionProposal!.Status);
        Assert.Equal(DisputeStatus.Open, item.DeductionProposal.Dispute!.Status);

        // Open dispute blocks item completion
        Result earlyComp = order.ConfirmClosuresAndComplete(item.Id, _now.AddDays(6), _now.AddDays(6), "SETTLE-01", null, _now.AddDays(6), "op-comp");
        Assert.True(earlyComp.IsFailure);
        Assert.Equal("Dispute.BlocksCompletion", earlyComp.Error.Code);

        // Admin cannot resolve with amount > proposed total (400,000 VND)
        Guid adminId = Guid.NewGuid();
        Result excessRes = order.ResolveDispute(item.Id, adminId, 450_000m, "Admin decision", null, _now.AddDays(6), "op-resolve-fail");
        Assert.True(excessRes.IsFailure);
        Assert.Equal("Dispute.ResolutionExceedsProposal", excessRes.Error.Code);

        // Admin resolves with 250,000 VND
        Result validResolve = order.ResolveDispute(item.Id, adminId, 250_000m, "Compromise based on photos", [Guid.NewGuid()], _now.AddDays(6), "op-resolve");
        Assert.True(validResolve.IsSuccess);
        Assert.Equal(250_000m, item.FinalDeductionAmount);
        Assert.Equal(DisputeStatus.Resolved, item.DeductionProposal.Dispute.Status);
        Assert.Equal(DeductionProposalStatus.Resolved, item.DeductionProposal.Status);

        // Now completion succeeds
        Result compRes = order.ConfirmClosuresAndComplete(item.Id, _now.AddDays(6), _now.AddDays(6), "SETTLE-01", "REFUND-01", _now.AddDays(6), "op-comp");
        Assert.True(compRes.IsSuccess);
        Assert.Equal(RentalItemStatus.Completed, item.Status);
        Assert.Equal(RentalOrderStatus.Completed, order.Status);
    }

    [Fact]
    public void OrderRollup_WithOneCompletedAndOneRejected_ResultsInCompletedOrder()
    {
        // 2 items: item 1 completes, item 2 was rejected
        InitialRentalItemDto item1Dto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new(2026, 10, 10),
            new(2026, 10, 12),
            "Item 1",
            null,
            "Loc 1",
            100_000m,
            200_000m,
            "group-1",
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        Guid supplier2 = Guid.NewGuid();
        InitialRentalItemDto item2Dto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            supplier2,
            1,
            new(2026, 10, 10),
            new(2026, 10, 12),
            "Item 2",
            null,
            "Loc 2",
            100_000m,
            200_000m,
            "group-2",
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        RentalOrder order = RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-ROLLUP-01",
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
            "op-roll",
            _now,
            [item1Dto, item2Dto]).Value;

        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");

        RentalItem item1 = order.Items.First(i => i.Id == item1Dto.Id);
        RentalItem item2 = order.Items.First(i => i.Id == item2Dto.Id);

        // Supplier 2 rejects item 2
        order.SupplierRejectItem(item2.Id, supplier2, "Unavailable", _now.AddHours(1), "op-rej");

        // Supplier 1 accepts item 1, handovers, returns normal, completes
        order.SupplierAcceptItem(item1.Id, item1.SupplierId, _now.AddHours(1), "op-acc");
        order.SubmitHandover(item1.Id, item1.SupplierId, _now.AddDays(1), [new(item1.UnitAssignments.First().Id, UnitCondition.Normal)], null, _now.AddDays(1), "op-hand");
        order.SubmitReturn(item1.Id, item1.SupplierId, _now.AddDays(2), [new(item1.UnitAssignments.First().Id, UnitCondition.Normal)], null, _now.AddDays(2), "op-ret");
        order.ConfirmClosuresAndComplete(item1.Id, _now.AddDays(2), _now.AddDays(2), "SETTLE-01", null, _now.AddDays(2), "op-comp");

        // Order rollup must be COMPLETED because item 1 completed!
        Assert.Equal(RentalItemStatus.Completed, item1.Status);
        Assert.Equal(RentalItemStatus.Rejected, item2.Status);
        Assert.Equal(RentalOrderStatus.Completed, order.Status);
    }
}
