using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    /// Renter còn được phản hồi khi chưa quá hạn. Job dùng đúng phủ định chặt của vị từ này.
    public bool IsRenterResponseOpen(DateTimeOffset now)
    {
        var p = DeductionProposal;
        return Status == RentalItemStatus.PendingSettlement
            && FinalDeductionAmount is null
            && p is { Status: DeductionProposalStatus.Submitted }
            && now <= p.RenterResponseDeadline;
    }

    public bool NeedsRenterResponseTimeout(DateTimeOffset now)
    {
        var p = DeductionProposal;
        return Status == RentalItemStatus.PendingSettlement
            && FinalDeductionAmount is null
            && p is { Status: DeductionProposalStatus.Submitted }
            && now > p.RenterResponseDeadline;
    }

    private Result ValidateRenterResponse(Guid renterId, DateTimeOffset now)
    {
        if (renterId == Guid.Empty || RentalOrder is null || RentalOrder.RenterId != renterId)
            return Error.Forbidden("UNAUTHORIZED_RENTER",
                "Chỉ người thuê của đơn hàng mới được phản hồi.");

        var proposal = DeductionProposal;
        if (Status != RentalItemStatus.PendingSettlement || proposal is null)
            return Error.Conflict("NO_DEDUCTION_PROPOSAL", "Mục thuê không có đề nghị khấu trừ chờ phản hồi.");

        if (proposal.Status != DeductionProposalStatus.Submitted || FinalDeductionAmount.HasValue)
            return Error.Conflict("DEDUCTION_ALREADY_RESPONDED", "Đề nghị khấu trừ đã được phản hồi hoặc đã chốt.");

        if (now > proposal.RenterResponseDeadline)
            return Error.Conflict("RENTER_RESPONSE_WINDOW_CLOSED", "Đã quá hạn phản hồi đề nghị khấu trừ.");

        return Result.Success();
    }

    public Result AcceptDeductionByRenter(Guid renterId, DateTimeOffset now, string operationId,
        int disputeExtensionHours = 12)
    {
        if (Status == RentalItemStatus.Disputed)
        {
            var p = DeductionProposal;
            if (renterId == Guid.Empty || RentalOrder?.RenterId != renterId)
                return Error.Forbidden("UNAUTHORIZED_RENTER", "Only the order renter may accept an offer.");
            if (p?.Dispute is not { Status: DisputeStatus.Open } dispute ||
                p.Status != DeductionProposalStatus.Disputed || FinalDeductionAmount.HasValue)
                return Error.Conflict("NO_OPEN_DISPUTE", "There is no open deduction dispute.");
            if (ReturnsCompletedAt is null || disputeExtensionHours < 0 ||
                now > p.RenterResponseDeadline.AddHours(disputeExtensionHours))
                return Error.Conflict("DISPUTE_AGREEMENT_WINDOW_CLOSED", "The agreement window has closed.");

            decimal agreed = dispute.SupplierOfferAmount ?? p.TotalProposedAmount;
            dispute.Resolve(agreed, "Renter accepted supplier's deduction offer", renterId,
                now.ToUniversalTime(), []);
            p.ApplyResolution();
            FinalDeductionAmount = agreed;
            Status = RentalItemStatus.PendingSettlement;
            RentalOrder?.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), RentalOrderId, Id,
                RentalItemStatus.Disputed.ToString(), Status.ToString(), "RenterAcceptDisputeOffer",
                renterId, ActorRole.Renter, $"Agreed deduction {agreed}", now, operationId));
            return Result.Success();
        }

        var validation = ValidateRenterResponse(renterId, now);
        if (validation.IsFailure) return validation.Error;

        var proposal = DeductionProposal!;
        proposal.Accept(renterId, now.ToUniversalTime());
        FinalDeductionAmount = proposal.TotalProposedAmount;

        AddSettlementTransition("RenterAcceptDeduction", renterId, ActorRole.Renter,
            "Renter accepted the deduction proposal", now, operationId);
        return Result.Success();
    }

    public Result OfferDisputeAmountBySupplier(Guid supplierId, decimal amount, DateTimeOffset now,
        int disputeExtensionHours, string operationId)
    {
        var p = DeductionProposal;
        if (supplierId == Guid.Empty || supplierId != SupplierId)
            return Error.Forbidden("UNAUTHORIZED_SUPPLIER", "Only the item supplier may offer a deduction.");
        if (Status != RentalItemStatus.Disputed || p?.Dispute is not { Status: DisputeStatus.Open } dispute ||
            p.Status != DeductionProposalStatus.Disputed || FinalDeductionAmount.HasValue)
            return Error.Conflict("NO_OPEN_DISPUTE", "There is no open deduction dispute.");
        if (disputeExtensionHours < 0 || now > p.RenterResponseDeadline.AddHours(disputeExtensionHours))
            return Error.Conflict("DISPUTE_AGREEMENT_WINDOW_CLOSED", "The agreement window has closed.");
        if (amount < 0m || amount > p.TotalProposedAmount || amount > DepositAmount ||
            decimal.Round(amount, 2) != amount ||
            dispute.SupplierOfferAmount is decimal previous && amount > previous)
            return Error.Validation("INVALID_DISPUTE_OFFER", "The offer must not exceed the current supplier proposal.");

        dispute.Offer(amount, supplierId, now);
        AddSettlementTransition("SupplierDisputeOffer", supplierId, ActorRole.Supplier,
            $"Supplier offered deduction {amount}", now, operationId);
        return Result.Success();
    }

    public Result DisputeDeductionByRenter(
        Guid renterId, string? reason, IReadOnlyCollection<Guid> mediaIds, DateTimeOffset now, string operationId)
    {
        var validation = ValidateRenterResponse(renterId, now);
        if (validation.IsFailure) return validation.Error;

        string trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > 2000)
            return Error.Validation("INVALID_DISPUTE_REASON", "Lý do khiếu nại là bắt buộc, tối đa 2000 ký tự.");

        if (mediaIds.Count > 10 || mediaIds.Contains(Guid.Empty) || mediaIds.Distinct().Count() != mediaIds.Count)
            return Error.Validation("INVALID_EVIDENCE", "Tối đa 10 bằng chứng, không trùng lặp.");

        var proposal = DeductionProposal!;
        proposal.OpenDispute(Guid.NewGuid(), renterId, trimmed, mediaIds, now.ToUniversalTime());
        Status = RentalItemStatus.Disputed;
        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(), RentalOrderId, Id,
            RentalItemStatus.PendingSettlement.ToString(), Status.ToString(),
            "RenterDisputeDeduction", renterId, ActorRole.Renter, trimmed, now, operationId));
        return Result.Success();
    }

    /// Hết hạn mà renter chưa phản hồi thì tự chấp nhận. False nghĩa là no-op (đã phản hồi / chưa hết hạn).
    public bool TryAutoAcceptDeduction(DateTimeOffset now, string operationId)
    {
        if (!NeedsRenterResponseTimeout(now)) return false;

        var proposal = DeductionProposal!;
        proposal.AcceptByTimeout(now.ToUniversalTime());
        FinalDeductionAmount = proposal.TotalProposedAmount;

        AddSettlementTransition("RenterResponseTimeout", null, ActorRole.System,
            "Renter did not respond within 48 hours; deduction proposal auto-accepted", now, operationId);
        return true;
    }

    /// Giữ tranh chấp đến hết hạn, sau đó chốt tối đa đề nghị của NCC và hoàn phần cọc còn lại.
    /// Phần thiệt hại NCC khai vượt cọc không làm tăng số tiền được khấu trừ.
    public bool TryFinalizeDisputeAfterHold(DateTimeOffset now, int holdDays, string operationId)
    {
        if (holdDays < 1 || ReturnsCompletedAt is null ||
            now < ReturnsCompletedAt.Value.AddDays(holdDays) ||
            Status != RentalItemStatus.Disputed || FinalDeductionAmount.HasValue ||
            DeductionProposal is not { Status: DeductionProposalStatus.Disputed, Dispute: { Status: DisputeStatus.Open } } proposal)
            return false;

        decimal finalAmount = Math.Min(proposal.Dispute.SupplierOfferAmount ?? proposal.TotalProposedAmount,
            DepositAmount);
        proposal.Dispute.Resolve(finalAmount, "Dispute hold expired without agreement", null,
            now.ToUniversalTime(), []);
        proposal.ApplyResolution();
        FinalDeductionAmount = finalAmount;
        Status = RentalItemStatus.PendingSettlement;
        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(), RentalOrderId, Id, RentalItemStatus.Disputed.ToString(), Status.ToString(),
            "DisputeHoldTimeout", null, ActorRole.System,
            $"Hold expired; final deduction {finalAmount} of proposed {proposal.TotalProposedAmount}",
            now, operationId));
        return true;
    }

    public const string SettleRentalAction = "SettleRental";

    public bool IsReadyForSettlement =>
        Status == RentalItemStatus.PendingSettlement
        && FinalDeductionAmount.HasValue
        && InventoryClosureConfirmedAt.HasValue
        && string.IsNullOrWhiteSpace(SettlementRef);

    public decimal DepositRefundAmount => DepositAmount - (FinalDeductionAmount ?? 0m);

    /// Payment xác nhận quyết toán. Gọi lại với cùng SettlementRef là no-op.
    public Result RecordPaymentSettlementConfirmed(string settlementRef, DateTimeOffset now, string operationId)
    {
        if (string.IsNullOrWhiteSpace(settlementRef))
            return Error.Validation("INVALID_SETTLEMENT_REF", "SettlementRef là bắt buộc.");

        if (!string.IsNullOrWhiteSpace(SettlementRef))
            return string.Equals(SettlementRef, settlementRef, StringComparison.OrdinalIgnoreCase)
                ? Result.Success()
                : Error.Conflict("SETTLEMENT_REF_MISMATCH", "SettlementRef không khớp với giá trị đã ghi nhận.");

        if (Status != RentalItemStatus.PendingSettlement || !FinalDeductionAmount.HasValue)
            return Error.Conflict("ITEM_NOT_AWAITING_SETTLEMENT", "Mục thuê chưa ở trạng thái chờ quyết toán.");

        SettlementRef = settlementRef.Trim();
        FinancialClosureConfirmedAt = now.ToUniversalTime();

        AddSettlementTransition("RentalSettled", null, ActorRole.System,
            $"Payment confirmed settlement (SettlementRef: {SettlementRef})", now, operationId);

        TryCompleteSettlement(now, operationId);
        return Result.Success();
    }

    /// Hoàn tất khi đã có số khấu trừ cuối, Inventory đóng allocation và Payment xác nhận.
    public bool TryCompleteSettlement(DateTimeOffset now, string operationId)
    {
        if (Status != RentalItemStatus.PendingSettlement
            || !FinalDeductionAmount.HasValue
            || !InventoryClosureConfirmedAt.HasValue
            || !FinancialClosureConfirmedAt.HasValue)
            return false;

        string from = Status.ToString();
        Status = RentalItemStatus.Completed;
        CompletedAt ??= now.ToUniversalTime();

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(), RentalOrderId, Id,
            from, RentalItemStatus.Completed.ToString(), "SettlementCompleted",
            null, ActorRole.System, "Inventory and Payment confirmed; rental item completed", now, operationId));
        return true;
    }

    private void AddSettlementTransition(
        string action, Guid? actorId, ActorRole role, string reason, DateTimeOffset now, string operationId) =>
        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(), RentalOrderId, Id,
            Status.ToString(), Status.ToString(), action, actorId, role, reason, now, operationId));
}
