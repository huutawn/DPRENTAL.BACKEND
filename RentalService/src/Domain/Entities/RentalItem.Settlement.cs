using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    internal Result SubmitDeductionProposal(
        Guid proposalId,
        Guid supplierId,
        IEnumerable<DeductionLineDto> lines,
        DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSettlement)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (SupplierId != supplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        if (DeductionProposal is not null)
        {
            return RentalErrors.DeductionAlreadySubmitted;
        }

        if (FinalDeductionAmount.HasValue)
        {
            return RentalErrors.ProposalAlreadyResponded;
        }

        if (DeductionDeadline.HasValue && utcNow >= DeductionDeadline.Value)
        {
            return RentalErrors.DeductionDeadlineExpired;
        }

        var lineList = lines?.ToList() ?? [];
        if (lineList.Count == 0)
        {
            return RentalErrors.InvalidSnapshot;
        }

        if (lineList.Select(l => l.AssignmentId).Distinct().Count() != lineList.Count)
        {
            return RentalErrors.DuplicateAssignment;
        }

        if (lineList.Any(l => l.ProposedAmount < 0))
        {
            return RentalErrors.NegativeAmount;
        }

        decimal totalAmount = lineList.Sum(l => l.ProposedAmount);
        if (totalAmount > DepositAmount)
        {
            return RentalErrors.DeductionAmountExceedsDeposit;
        }

        Dictionary<Guid, RentalUnitAssignment> assignDict = _unitAssignments.ToDictionary(u => u.Id);
        foreach (var line in lineList)
        {
            if (!assignDict.TryGetValue(line.AssignmentId, out RentalUnitAssignment? unit))
            {
                return RentalErrors.AssignmentNotFound;
            }

            if (unit.Status != RentalUnitAssignmentStatus.ReturnedDamaged)
            {
                return RentalErrors.InvalidAssignmentStatus;
            }
        }

        DeductionProposal proposal = new(
            proposalId,
            Id,
            supplierId,
            utcNow,
            utcNow.AddHours(48));

        foreach (var line in lineList)
        {
            RentalUnitAssignment unit = assignDict[line.AssignmentId];
            unit.RecordDeduction(proposalId, line.ProposedAmount, line.Reason, line.MediaIds);
            proposal.AddAssignment(unit);
        }

        DeductionProposal = proposal;
        return Result.Success();
    }

    internal Result ExpireDeductionSubmission(DateTimeOffset utcNow)
    {
        if (Status != RentalItemStatus.PendingSettlement)
        {
            return RentalErrors.InvalidItemStatus;
        }

        if (DeductionProposal is not null)
        {
            return RentalErrors.DeductionAlreadySubmitted;
        }

        if (FinalDeductionAmount.HasValue)
        {
            return RentalErrors.ProposalAlreadyResponded;
        }

        if (DeductionDeadline.HasValue && utcNow < DeductionDeadline.Value)
        {
            return RentalErrors.DeductionDeadlineNotExpired;
        }

        FinalDeductionAmount = 0m;
        return Result.Success();
    }

    internal Result AcceptDeductionProposal(Guid renterId, Guid orderRenterId, DateTimeOffset utcNow)
    {
        if (renterId != orderRenterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        if (DeductionProposal is null)
        {
            return RentalErrors.ProposalNotFound;
        }

        if (DeductionProposal.Status != DeductionProposalStatus.Submitted)
        {
            return RentalErrors.ProposalNotSubmitted;
        }

        if (utcNow >= DeductionProposal.RenterResponseDeadline)
        {
            return RentalErrors.ResponseDeadlineExpired;
        }

        DeductionProposal.Accept(renterId, utcNow);
        FinalDeductionAmount = DeductionProposal.TotalProposedAmount;
        return Result.Success();
    }

    internal Result TimeoutDeductionProposal(DateTimeOffset utcNow)
    {
        if (DeductionProposal is null)
        {
            return RentalErrors.ProposalNotFound;
        }

        if (DeductionProposal.Status != DeductionProposalStatus.Submitted)
        {
            return RentalErrors.ProposalNotSubmitted;
        }

        if (utcNow < DeductionProposal.RenterResponseDeadline)
        {
            return RentalErrors.ResponseDeadlineNotExpired;
        }

        DeductionProposal.AcceptByTimeout(utcNow);
        FinalDeductionAmount = DeductionProposal.TotalProposedAmount;
        return Result.Success();
    }

    internal Result DisputeDeductionProposal(
        Guid disputeId,
        Guid renterId,
        Guid orderRenterId,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow)
    {
        if (renterId != orderRenterId)
        {
            return RentalErrors.OrderCancellationForbidden;
        }

        if (DeductionProposal is null)
        {
            return RentalErrors.ProposalNotFound;
        }

        if (DeductionProposal.Status != DeductionProposalStatus.Submitted)
        {
            return RentalErrors.ProposalNotSubmitted;
        }

        if (utcNow >= DeductionProposal.RenterResponseDeadline)
        {
            return RentalErrors.ResponseDeadlineExpired;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        DeductionProposal.OpenDispute(disputeId, renterId, reason.Trim(), mediaIds, utcNow);
        return Result.Success();
    }

    internal Result ResolveDispute(
        Guid adminId,
        ActorRole actorRole,
        decimal finalDeductionAmount,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset utcNow)
    {
        if (actorRole != ActorRole.Admin)
        {
            return RentalErrors.AdminOnlyResolution;
        }

        if (DeductionProposal?.Dispute is null)
        {
            return RentalErrors.DisputeNotFound;
        }

        if (DeductionProposal.Dispute.Status != DisputeStatus.Open)
        {
            return RentalErrors.DisputeNotOpen;
        }

        if (finalDeductionAmount < 0)
        {
            return RentalErrors.NegativeAmount;
        }

        if (finalDeductionAmount > DeductionProposal.TotalProposedAmount)
        {
            return RentalErrors.ResolutionExceedsProposedAmount;
        }

        if (finalDeductionAmount > DepositAmount)
        {
            return RentalErrors.ResolutionExceedsDeposit;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return RentalErrors.InvalidSnapshot;
        }

        DeductionProposal.Dispute.Resolve(finalDeductionAmount, reason.Trim(), adminId, utcNow, mediaIds);
        DeductionProposal.ApplyResolution();
        FinalDeductionAmount = finalDeductionAmount;
        return Result.Success();
    }

    internal Result RecordInventoryClosure(
        string inventoryClosureRef,
        DateTimeOffset confirmedAt,
        IEnumerable<Guid>? closedAssignmentIds = null,
        string? dispositionRef = null)
    {
        if (string.IsNullOrWhiteSpace(inventoryClosureRef))
        {
            return RentalErrors.InvalidSnapshot;
        }

        InventoryClosureRef = inventoryClosureRef.Trim();
        InventoryClosureConfirmedAt = confirmedAt;

        var targetIds = closedAssignmentIds?.ToHashSet();
        foreach (var u in _unitAssignments)
        {
            if (targetIds is null || targetIds.Contains(u.Id))
            {
                u.RecordAllocationClosed(confirmedAt, dispositionRef);
            }
        }

        TryComplete(confirmedAt);
        return Result.Success();
    }

    internal Result RecordPaymentSettlement(
        string settlementRef,
        string? refundRef,
        DateTimeOffset confirmedAt)
    {
        if (string.IsNullOrWhiteSpace(settlementRef))
        {
            return RentalErrors.InvalidSnapshot;
        }

        SettlementRef = settlementRef.Trim();
        RefundRef = refundRef?.Trim();
        FinancialClosureConfirmedAt = confirmedAt;

        TryComplete(confirmedAt);
        return Result.Success();
    }

    internal bool CanComplete(DateTimeOffset currentOrConfirmedAt, out Error? error)
    {
        if (!AreAllUnitsReturned)
        {
            error = RentalErrors.IncompleteReturn;
            return false;
        }

        if (Status == RentalItemStatus.PendingSettlement)
        {
            if (DeductionProposal?.Dispute?.Status == DisputeStatus.Open || DeductionProposal?.Status == DeductionProposalStatus.Disputed)
            {
                error = RentalErrors.DisputeBlocksCompletion;
                return false;
            }

            if (DeductionProposal?.Status == DeductionProposalStatus.Submitted)
            {
                error = RentalErrors.ProposalPendingResponse;
                return false;
            }

            if (DeductionProposal is null && DeductionDeadline.HasValue && currentOrConfirmedAt < DeductionDeadline.Value)
            {
                error = RentalErrors.DeductionDeadlineNotExpired;
                return false;
            }

            if (!FinalDeductionAmount.HasValue)
            {
                error = RentalErrors.DeductionNotFinalized;
                return false;
            }
        }
        else if (Status == RentalItemStatus.InRental)
        {
            if (HasDamagedUnits)
            {
                error = RentalErrors.InvalidItemStatus;
                return false;
            }

            FinalDeductionAmount = 0m;
        }
        else if (Status != RentalItemStatus.Completed)
        {
            error = RentalErrors.InvalidItemStatus;
            return false;
        }

        if (!InventoryClosureConfirmedAt.HasValue || !FinancialClosureConfirmedAt.HasValue)
        {
            error = RentalErrors.ClosureReceiptMissing;
            return false;
        }

        error = null;
        return true;
    }

    internal Result TryComplete(DateTimeOffset utcNow)
    {
        if (Status == RentalItemStatus.Completed)
        {
            return Result.Success();
        }

        if (CanComplete(utcNow, out Error? error))
        {
            Status = RentalItemStatus.Completed;
            return Result.Success();
        }

        return error is not null ? Result.Failure(error) : Result.Failure(RentalErrors.InvalidItemStatus);
    }

    internal Result ConfirmClosuresAndComplete(
        DateTimeOffset inventoryClosureAt,
        DateTimeOffset financialClosureAt,
        string? settlementRef,
        string? refundRef,
        string? inventoryClosureRef = null)
    {
        if (!AreAllUnitsReturned)
        {
            return RentalErrors.IncompleteReturn;
        }

        if (Status == RentalItemStatus.PendingSettlement)
        {
            if (DeductionProposal?.Dispute?.Status == DisputeStatus.Open || DeductionProposal?.Status == DeductionProposalStatus.Disputed)
            {
                return RentalErrors.DisputeBlocksCompletion;
            }

            if (DeductionProposal?.Status == DeductionProposalStatus.Submitted)
            {
                return RentalErrors.ProposalPendingResponse;
            }

            if (DeductionProposal is null && DeductionDeadline.HasValue && inventoryClosureAt < DeductionDeadline.Value)
            {
                return RentalErrors.DeductionDeadlineNotExpired;
            }

            if (!FinalDeductionAmount.HasValue)
            {
                return RentalErrors.DeductionNotFinalized;
            }
        }
        else if (Status == RentalItemStatus.InRental)
        {
            if (HasDamagedUnits)
            {
                return RentalErrors.InvalidItemStatus;
            }

            FinalDeductionAmount = 0m;
        }

        string invRef = !string.IsNullOrWhiteSpace(inventoryClosureRef)
            ? inventoryClosureRef.Trim()
            : (InventoryClosureRef ?? $"inv-closure-{Id:N}");

        string finRef = !string.IsNullOrWhiteSpace(settlementRef)
            ? settlementRef.Trim()
            : (SettlementRef ?? $"settle-{Id:N}");

        InventoryClosureRef = invRef;
        InventoryClosureConfirmedAt = inventoryClosureAt;
        FinancialClosureConfirmedAt = financialClosureAt;
        SettlementRef = finRef;
        RefundRef = refundRef?.Trim();

        foreach (var u in _unitAssignments)
        {
            u.RecordAllocationClosed(inventoryClosureAt, null);
        }

        Status = RentalItemStatus.Completed;
        return Result.Success();
    }
}
