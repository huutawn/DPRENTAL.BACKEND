using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Services.Orders;
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
        ReturnTestDataSeeder.SeedReturn(item, item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId);
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
        Assert.Equal(item.DeductionDeadline.Value, submitted.Value.RenterResponseDeadline);
        Assert.False(item.NeedsDeductionTimeout(item.DeductionDeadline.Value.AddTicks(1)));
    }

    [Fact]
    public void OverCapDisputeHold_TransfersOnlyProposedAmountAndIsIdempotent()
    {
        var item = ReturnedDamagedItem();
        var unit = item.UnitAssignments.Single();
        decimal proposed = item.DepositAmount / 2m;
        var submitted = item.SubmitDeduction(item.SupplierId, proposed, "Damaged",
            [Guid.NewGuid()], [unit.AssetId], Now, item.DepositAmount + 1m);

        Assert.True(submitted.IsSuccess, submitted.Error.Message);
        Assert.Equal(RentalItemStatus.Disputed, item.Status);
        decimal loweredOffer = proposed / 2m;
        Assert.True(item.OfferDisputeAmountBySupplier(item.SupplierId, loweredOffer,
            Now.AddHours(1), 12, "lower-offer").IsSuccess);
        DateTimeOffset boundary = item.ReturnsCompletedAt!.Value.AddDays(7);
        Assert.False(item.TryFinalizeDisputeAfterHold(boundary.AddTicks(-1), 7, "hold-timeout"));
        Assert.True(item.TryFinalizeDisputeAfterHold(boundary, 7, "hold-timeout"));
        Assert.Equal(loweredOffer, item.FinalDeductionAmount);
        Assert.Equal(item.DepositAmount - loweredOffer, item.DepositRefundAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.False(item.TryFinalizeDisputeAfterHold(boundary.AddMinutes(1), 7, "hold-timeout"));
        var settlement = OrderQueryMapper.ToSettlement(item)!;
        Assert.Equal("DISPUTE_HOLD_TIMEOUT", settlement.FinalizationSource);
        Assert.Equal(loweredOffer, settlement.FinalDeductionAmount);
        Assert.Equal(item.DepositAmount - loweredOffer, settlement.DepositRefundAmount);
    }

    [Fact]
    public void DisputedItem_AcceptsLowerSupplierOfferWithinSixtyHours()
    {
        var item = ReturnedDamagedItem();
        var unit = item.UnitAssignments.Single();
        decimal proposed = item.DepositAmount / 2m;
        Assert.True(item.SubmitDeduction(item.SupplierId, proposed, "Damage",
            [Guid.NewGuid()], [unit.AssetId], Now).IsSuccess);
        var renterId = item.RentalOrder!.RenterId;
        Assert.True(item.DisputeDeductionByRenter(renterId, "Disagree", [],
            Now.AddHours(1), "dispute").IsSuccess);

        var deadline = item.DeductionProposal!.RenterResponseDeadline.AddHours(12);
        Assert.True(item.OfferDisputeAmountBySupplier(item.SupplierId, proposed / 2m,
            deadline, 12, "offer").IsSuccess);
        Assert.True(item.AcceptDeductionByRenter(renterId, deadline, "accept", 12).IsSuccess);
        Assert.Equal(proposed / 2m, item.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.Equal(DisputeStatus.Resolved, item.DeductionProposal.Dispute!.Status);
    }

    [Fact]
    public void DisputedItem_RejectsAgreementAfterSixtyHours()
    {
        var item = ReturnedDamagedItem();
        var unit = item.UnitAssignments.Single();
        decimal proposed = item.DepositAmount / 2m;
        Assert.True(item.SubmitDeduction(item.SupplierId, proposed, "Damage",
            [Guid.NewGuid()], [unit.AssetId], Now).IsSuccess);
        var renterId = item.RentalOrder!.RenterId;
        Assert.True(item.DisputeDeductionByRenter(renterId, "Disagree", [],
            Now.AddHours(1), "dispute").IsSuccess);

        var afterDeadline = item.DeductionProposal!.RenterResponseDeadline.AddHours(12).AddTicks(1);
        Assert.True(item.OfferDisputeAmountBySupplier(item.SupplierId, proposed / 2m,
            afterDeadline, 12, "offer").IsFailure);
        Assert.True(item.AcceptDeductionByRenter(renterId, afterDeadline, "accept", 12).IsFailure);
        Assert.Null(item.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.Disputed, item.Status);
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
    public void ClaimedDamageAboveDepositOpensDisputeWithoutOverchargingDeposit()
    {
        var item = ReturnedDamagedItem();
        var assetId = item.UnitAssignments.Single().AssetId;
        decimal deposit = item.DepositAmount;
        var submitted = item.SubmitDeduction(item.SupplierId, deposit, "Damage exceeds deposit",
            [Guid.NewGuid()], [assetId], Now, deposit + 1m);

        Assert.True(submitted.IsSuccess, submitted.Error.Message);
        Assert.Equal(RentalItemStatus.Disputed, item.Status);
        Assert.Equal(deposit, submitted.Value.TotalProposedAmount);
        Assert.Equal(deposit + 1m, submitted.Value.ClaimedDamageAmount);
        Assert.Equal(DeductionProposalStatus.Disputed, submitted.Value.Status);
        Assert.NotNull(submitted.Value.Dispute);
        Assert.Null(item.FinalDeductionAmount);
        Assert.False(item.IsReadyForSettlement);
        Assert.False(item.TryAutoAcceptDeduction(Now.AddDays(3), "timeout"));
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
        ReturnTestDataSeeder.SeedReturn(item, item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId);
        Assert.Null(item.DeductionDeadline);
        Assert.Equal(0m, item.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
    }
}
