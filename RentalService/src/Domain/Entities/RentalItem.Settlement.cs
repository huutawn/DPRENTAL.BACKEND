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

    public Result AcceptDeductionByRenter(Guid renterId, DateTimeOffset now, string operationId)
    {
        var validation = ValidateRenterResponse(renterId, now);
        if (validation.IsFailure) return validation.Error;

        var proposal = DeductionProposal!;
        proposal.Accept(renterId, now.ToUniversalTime());
        FinalDeductionAmount = proposal.TotalProposedAmount;

        AddSettlementTransition("RenterAcceptDeduction", renterId, ActorRole.Renter,
            "Renter accepted the deduction proposal", now, operationId);
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

        AddSettlementTransition("RenterDisputeDeduction", renterId, ActorRole.Renter, trimmed, now, operationId);
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

    public Result ResolveDeductionDispute(
        Guid adminId, decimal finalAmount, string? note,
        IReadOnlyCollection<Guid> resolutionMediaIds, DateTimeOffset now, string operationId)
    {
        if (adminId == Guid.Empty)
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu danh tính Admin hợp lệ.");

        var proposal = DeductionProposal;
        if (Status != RentalItemStatus.PendingSettlement || proposal is null ||
            proposal.Status != DeductionProposalStatus.Disputed || proposal.Dispute is null)
            return Error.Conflict("NO_OPEN_DISPUTE", "Mục thuê không có khiếu nại đang chờ quyết định.");

        if (FinalDeductionAmount.HasValue)
            return Error.Conflict("DISPUTE_ALREADY_RESOLVED", "Khiếu nại đã có quyết định cuối cùng.");

        if (finalAmount < 0 || finalAmount > proposal.TotalProposedAmount ||
            finalAmount > DepositAmount || decimal.Round(finalAmount, 2) != finalAmount)
            return Error.Validation("INVALID_FINAL_DEDUCTION",
                $"Số tiền khấu trừ cuối phải từ 0 đến {proposal.TotalProposedAmount}, tối đa 2 chữ số thập phân.");

        string trimmedNote = note?.Trim() ?? string.Empty;
        if (trimmedNote.Length == 0 || trimmedNote.Length > 2000)
            return Error.Validation("INVALID_DECISION_NOTE", "Ghi chú quyết định là bắt buộc, tối đa 2000 ký tự.");

        if (resolutionMediaIds.Count > 10 || resolutionMediaIds.Contains(Guid.Empty) ||
            resolutionMediaIds.Distinct().Count() != resolutionMediaIds.Count)
            return Error.Validation("INVALID_EVIDENCE", "Tối đa 10 bằng chứng, không trùng lặp.");

        proposal.Dispute.Resolve(finalAmount, trimmedNote, adminId, now.ToUniversalTime(), resolutionMediaIds);
        proposal.ApplyResolution();
        FinalDeductionAmount = finalAmount;

        string decision = finalAmount == proposal.TotalProposedAmount ? "UpheldInFull"
                        : finalAmount == 0m ? "Rejected" : "Partial";
        AddSettlementTransition("AdminResolveDispute", adminId, ActorRole.Admin,
            $"{decision}: final deduction {finalAmount}. {trimmedNote}", now, operationId);
        return Result.Success();
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
