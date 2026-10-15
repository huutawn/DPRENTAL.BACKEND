using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class DeductionResponseDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static (RentalOrder Order, RentalItem Item, Guid RenterId, decimal Amount) Proposed()
    {
        var order = Returns.ReturnTests.NewOrder(1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        Assert.True(item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId).IsSuccess);
        decimal amount = Math.Min(100_000m, item.DepositAmount);
        Assert.True(item.SubmitDeduction(item.SupplierId, amount, "Repair", [Guid.NewGuid()],
            [unit.AssetId], Now).IsSuccess);
        return (order, item, order.RenterId, amount);
    }

    [Fact]
    public void AcceptAtDeadlineFinalizesAmount_AndLaterTimeoutIsNoOp()
    {
        var (_, item, renterId, amount) = Proposed();
        var deadline = item.DeductionProposal!.RenterResponseDeadline;
        Assert.True(item.IsRenterResponseOpen(deadline));
        Assert.True(item.AcceptDeductionByRenter(renterId, deadline, "accept").IsSuccess);
        Assert.Equal(amount, item.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.False(item.TryAutoAcceptDeduction(deadline.AddTicks(1), "timeout"));
    }

    [Fact]
    public void TimeoutOnlyRunsStrictlyAfterDeadline()
    {
        var (_, item, _, amount) = Proposed();
        var deadline = item.DeductionProposal!.RenterResponseDeadline;
        Assert.False(item.TryAutoAcceptDeduction(deadline, "timeout"));
        Assert.True(item.TryAutoAcceptDeduction(deadline.AddTicks(1), "timeout"));
        Assert.Equal(amount, item.FinalDeductionAmount);
        Assert.False(item.TryAutoAcceptDeduction(deadline.AddSeconds(1), "timeout"));
    }

    [Fact]
    public void DisputeKeepsAmountOpen_UntilAdminRecordsFinalDecision()
    {
        var (_, item, renterId, amount) = Proposed();
        Assert.True(item.DisputeDeductionByRenter(renterId, "Too high", [], Now.AddHours(1), "dispute").IsSuccess);
        Assert.Null(item.FinalDeductionAmount);
        Assert.False(item.TryAutoAcceptDeduction(Now.AddDays(3), "timeout"));
        Assert.True(item.ResolveDeductionDispute(Guid.NewGuid(), amount + 1, "Upheld", [],
            Now.AddHours(2), "resolve-invalid").IsFailure);
        Guid admin = Guid.NewGuid();
        Assert.True(item.ResolveDeductionDispute(admin, amount / 2, "Partial damage", [],
            Now.AddHours(2), "resolve").IsSuccess);
        Assert.Equal(amount / 2, item.FinalDeductionAmount);
        Assert.Equal(admin, item.DeductionProposal!.Dispute!.ResolvedById);
        Assert.Equal(DisputeStatus.Resolved, item.DeductionProposal.Dispute.Status);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
    }

    [Fact]
    public void WrongRenterAndClosedWindowCannotRespond()
    {
        var (_, item, _, _) = Proposed();
        Assert.True(item.AcceptDeductionByRenter(Guid.NewGuid(), Now, "wrong").IsFailure);
        Assert.True(item.AcceptDeductionByRenter(item.RentalOrder!.RenterId,
            item.DeductionProposal!.RenterResponseDeadline.AddTicks(1), "late").IsFailure);
        Assert.Null(item.FinalDeductionAmount);
    }
}
