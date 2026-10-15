using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    /// NCC còn được gửi khi chưa hết hạn. Job hết hạn dùng đúng phủ định của hàm này.
    public bool IsDeductionWindowOpen(DateTimeOffset now) =>
        DeductionDeadline.HasValue && now <= DeductionDeadline.Value;

    public bool IsDeductionWindowExpired(DateTimeOffset now) =>
        DeductionDeadline.HasValue && now > DeductionDeadline.Value;

    /// Job chỉ chốt một lần: chưa có đề nghị, chưa chốt số tiền cuối.
    public bool NeedsDeductionTimeout(DateTimeOffset now) =>
        Status == RentalItemStatus.PendingSettlement
        && AreAllUnitsReturned
        && HasDamagedUnits
        && DeductionProposal is null
        && FinalDeductionAmount is null
        && IsDeductionWindowExpired(now);

    public Result ValidateDeductionSubmission(
        Guid supplierId, decimal amount, string? description,
        IReadOnlyCollection<Guid> mediaIds, IReadOnlyCollection<Guid> affectedAssetIds, DateTimeOffset now)
    {
        if (supplierId == Guid.Empty || SupplierId != supplierId)
            return RentalErrors.UnauthorizedSupplier;

        if (DeductionProposal is not null || FinalDeductionAmount.HasValue)
            return Error.Conflict("DEDUCTION_ALREADY_FINALIZED", "Mục thuê đã có đề nghị khấu trừ hoặc đã chốt khấu trừ.");

        if (!AreAllUnitsReturned)
            return Error.Conflict("UNITS_NOT_ALL_RETURNED", "Chỉ được gửi đề nghị sau khi đã trả đủ máy.");

        if (!HasDamagedUnits)
            return Error.Conflict("NO_DAMAGED_UNITS", "Không có máy nào được ghi nhận hỏng.");

        if (Status != RentalItemStatus.PendingSettlement)
            return Error.Conflict("INVALID_DEDUCTION_STATUS", "Mục thuê chưa ở trạng thái chờ quyết toán.");

        if (!IsDeductionWindowOpen(now))
            return Error.Conflict("DEDUCTION_WINDOW_CLOSED", "Đã quá hạn gửi đề nghị khấu trừ.");

        if (amount <= 0 || amount > DepositAmount || decimal.Round(amount, 2) != amount)
            return Error.Validation("INVALID_DEDUCTION_AMOUNT",
                $"Số tiền khấu trừ phải lớn hơn 0 và không vượt quá tổng tiền cọc ({DepositAmount}).");

        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 2000)
            return Error.Validation("INVALID_DAMAGE_DESCRIPTION", "Mô tả hư hỏng là bắt buộc, tối đa 2000 ký tự.");

        if (mediaIds is null || mediaIds.Count == 0 || mediaIds.Count > 10 ||
            mediaIds.Distinct().Count() != mediaIds.Count || mediaIds.Contains(Guid.Empty))
            return Error.Validation("INVALID_EVIDENCE", "Cần 1 đến 10 bằng chứng, không trùng lặp.");

        if (affectedAssetIds is null || affectedAssetIds.Count == 0 || affectedAssetIds.Contains(Guid.Empty) ||
            affectedAssetIds.Distinct().Count() != affectedAssetIds.Count)
            return Error.Validation("INVALID_AFFECTED_ASSETS", "Cần ít nhất một máy bị ảnh hưởng, không trùng lặp.");

        var damagedByAsset = _unitAssignments
            .Where(u => u.Status == RentalUnitAssignmentStatus.ReturnedDamaged)
            .ToDictionary(u => u.AssetId);

        if (affectedAssetIds.Any(id => !damagedByAsset.ContainsKey(id)))
            return Error.Validation("ASSET_NOT_DAMAGED_IN_ITEM",
                "Có máy không thuộc mục thuê này hoặc chưa được ghi nhận hỏng.");

        return Result.Success();
    }

    public Result<DeductionProposal> SubmitDeduction(
        Guid supplierId, decimal amount, string description,
        IReadOnlyCollection<Guid> mediaIds, IReadOnlyCollection<Guid> affectedAssetIds,
        DateTimeOffset now)
    {
        var validation = ValidateDeductionSubmission(supplierId, amount, description, mediaIds, affectedAssetIds, now);
        if (validation.IsFailure) return validation.Error;

        var proposal = new DeductionProposal(Guid.NewGuid(), Id, supplierId, now.ToUniversalTime(),
            now.ToUniversalTime().AddHours(48), amount, description, mediaIds);
        foreach (var unit in _unitAssignments.Where(u => affectedAssetIds.Contains(u.AssetId)))
        {
            unit.RecordDeduction(proposal.Id, description, mediaIds);
            proposal.AddAssignment(unit);
        }
        DeductionProposal = proposal;
        return proposal;
    }

    /// Chốt khấu trừ 0 khi hết hạn. Trả false nếu đã chốt (job chạy lại là no-op).
    public bool TryFinalizeZeroDeduction(DateTimeOffset now, string operationId)
    {
        if (!NeedsDeductionTimeout(now)) return false;

        FinalDeductionAmount = 0m;

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            RentalItemStatus.PendingSettlement.ToString(),
            RentalItemStatus.PendingSettlement.ToString(),
            "DeductionTimeout",
            null,
            ActorRole.System,
            "Supplier did not submit a deduction proposal within 48 hours; deduction finalized at 0",
            now,
            operationId));
        return true;
    }
}
