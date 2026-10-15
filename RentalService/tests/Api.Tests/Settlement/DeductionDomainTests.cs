using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class DeductionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static RentalItem ReturnedDamagedItem()
    {
        var item = Returns.ReturnTests.NewOrder(1).Items.Single();
        var unit = item.UnitAssignments.Single();
        var result = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId);
        Assert.True(result.IsSuccess);
        return item;
    }

    [Fact]
    public void ProposalStoresOneTotalAndAffectedAssignmentWithoutInventedUnitAmount()
    {
        var item = ReturnedDamagedItem();
        var unit = item.UnitAssignments.Single();
        var mediaId = Guid.NewGuid();
        var amount = Math.Min(100_000m, item.DepositAmount);
        var submitted = item.SubmitDeduction(item.SupplierId, amount, "Repair lens",
            [mediaId], [unit.AssetId], item.DeductionDeadline!.Value);

        Assert.True(submitted.IsSuccess, submitted.Error.Message);
        Assert.Equal(amount, submitted.Value.TotalProposedAmount);
        Assert.Equal(amount, item.DeductionProposal!.TotalProposedAmount);
        Assert.Null(unit.ProposedDeductionAmount);
        Assert.Equal(submitted.Value.Id, unit.DeductionProposalId);
        Assert.Equal(item.DeductionDeadline.Value.AddHours(48), submitted.Value.RenterResponseDeadline);
        Assert.False(item.NeedsDeductionTimeout(item.DeductionDeadline.Value.AddTicks(1)));
    }

    [Fact]
    public void DeadlineBoundaryAllowsSubmissionAtDeadlineAndTimesOutStrictlyAfter()
    {
        var item = ReturnedDamagedItem();
        var deadline = item.DeductionDeadline!.Value;
        Assert.True(item.IsDeductionWindowOpen(deadline));
        Assert.False(item.IsDeductionWindowExpired(deadline));
        Assert.False(item.NeedsDeductionTimeout(deadline));
        Assert.True(item.NeedsDeductionTimeout(deadline.AddTicks(1)));
        Assert.True(item.TryFinalizeZeroDeduction(deadline.AddTicks(1), "timeout"));
        Assert.Equal(0m, item.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.False(item.TryFinalizeZeroDeduction(deadline.AddMinutes(1), "timeout"));
        Assert.Null(item.DeductionProposal);
    }

    [Fact]
    public void RejectsInvalidAmountSupplierEvidenceAndUnaffectedAsset()
    {
        var item = ReturnedDamagedItem();
        var assetId = item.UnitAssignments.Single().AssetId;
        var mediaId = Guid.NewGuid();
        Assert.True(item.SubmitDeduction(Guid.NewGuid(), 1, "Damage", [mediaId], [assetId], Now).IsFailure);
        Assert.True(item.SubmitDeduction(item.SupplierId, 0, "Damage", [mediaId], [assetId], Now).IsFailure);
        Assert.True(item.SubmitDeduction(item.SupplierId, item.DepositAmount + 1, "Damage", [mediaId], [assetId], Now).IsFailure);
        Assert.True(item.SubmitDeduction(item.SupplierId, 1, "Damage", [], [assetId], Now).IsFailure);
        Assert.True(item.SubmitDeduction(item.SupplierId, 1, "Damage", [mediaId], [Guid.NewGuid()], Now).IsFailure);
        Assert.Null(item.DeductionProposal);
    }

    [Fact]
    public void DamagedReturnWithoutDepositDoesNotOpenDeductionWindow()
    {
        var item = Returns.ReturnTests.NewOrder(1).Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.DepositAmount))!.SetValue(item, 0m);
        var unit = item.UnitAssignments.Single();
        var returned = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId);
        Assert.True(returned.IsSuccess);
        Assert.Null(item.DeductionDeadline);
        Assert.Equal(0m, item.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
    }
}
