using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;

namespace Dprental.InventoryService.Domain.Entities;

public sealed partial class AssetAllocation
{
    /// <summary>
    /// Chuyển đổi một lượt giữ chỗ còn hạn sang trạng thái phân bổ chính thức (Held -> Allocated).
    /// </summary>
    public Result Allocate(DateTimeOffset now, string actor = "system", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
        {
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        }

        var replay = CheckReplay(operationKey, AllocationOperationType.Allocate, [actor.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("ALLOCATION_IN_TERMINAL_STATE", $"Không thể phân bổ lượt đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != AllocationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể phân bổ khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
        }

        if (HoldExpiresAt.HasValue && now >= HoldExpiresAt.Value)
        {
            return Error.Expired("HOLD_EXPIRED", "Lượt giữ chỗ đã hết hạn và không thể phân bổ.");
        }

        Status = AllocationStatus.Allocated;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: AllocationOperationType.Allocate,
            fromStatus: AllocationStatus.Held,
            toStatus: AllocationStatus.Allocated,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }

    /// <summary>
    /// Gia hạn thời gian giữ chỗ khi hold còn hiệu lực (Held -> Held).
    /// </summary>
    public Result ExtendHold(DateTimeOffset newHoldExpiresAt, DateTimeOffset now, string actor = "system", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
        {
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        }

        var replay = CheckReplay(operationKey, AllocationOperationType.ExtendHold, [actor.Trim(), BusinessDateTime.NormalizeUtc(newHoldExpiresAt)], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("ALLOCATION_IN_TERMINAL_STATE", $"Không thể gia hạn lượt đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != AllocationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể gia hạn khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
        }

        if (!HoldExpiresAt.HasValue || now >= HoldExpiresAt.Value)
        {
            return Error.Expired("HOLD_EXPIRED", "Lượt giữ chỗ đã hết hạn; không được hồi sinh hold đã hết hạn.");
        }

        if (newHoldExpiresAt <= HoldExpiresAt.Value)
        {
            return Error.Validation("NEW_HOLD_EXPIRY_NOT_INCREASED", "Thời hạn giữ chỗ mới phải lớn hơn thời hạn giữ chỗ hiện tại.");
        }

        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(RentalPeriod.StartDate);
        if (newHoldExpiresAt >= startOfDayUtc)
        {
            return Error.Validation("HOLD_EXPIRES_AFTER_START", "Thời hạn giữ chỗ mới phải trước 00:00 ngày bắt đầu theo giờ Việt Nam.");
        }

        DateTimeOffset previousHoldExpiresAt = HoldExpiresAt.Value;
        HoldExpiresAt = BusinessDateTime.NormalizeUtc(newHoldExpiresAt);
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: AllocationOperationType.ExtendHold,
            fromStatus: AllocationStatus.Held,
            toStatus: AllocationStatus.Held,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: previousHoldExpiresAt,
            newHoldExpiresAt: BusinessDateTime.NormalizeUtc(newHoldExpiresAt),
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }

    /// <summary>
    /// Nhả lượt giữ chỗ chủ động (Held -> Released).
    /// </summary>
    public Result ReleaseHold(string reason, DateTimeOffset now, string actor = "system", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
        {
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        }

        var replay = CheckReplay(operationKey, AllocationOperationType.ReleaseHold, [actor.Trim(), reason?.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("ALLOCATION_IN_TERMINAL_STATE", $"Không thể nhả lượt giữ chỗ đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != AllocationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể nhả lượt giữ chỗ khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("REASON_REQUIRED", "Cần cung cấp lý do khi nhả giữ chỗ.");
        }

        string trimmedReason = reason.Trim();
        if (trimmedReason.Length > MaxCancellationReasonLength)
        {
            return Error.Validation("REASON_TOO_LONG", $"Lý do nhả tối đa {MaxCancellationReasonLength} ký tự.");
        }

        Status = AllocationStatus.Released;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: AllocationOperationType.ReleaseHold,
            fromStatus: AllocationStatus.Held,
            toStatus: AllocationStatus.Released,
            now: now,
            actor: actor,
            reason: trimmedReason,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }

    /// <summary>
    /// Hết hạn thời gian giữ chỗ tự động (Held -> Released).
    /// </summary>
    public Result ExpireHold(DateTimeOffset now, string actor = "system", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
        {
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        }

        var replay = CheckReplay(operationKey, AllocationOperationType.ExpireHold, [actor.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("ALLOCATION_IN_TERMINAL_STATE", $"Không thể xử lý hết hạn cho lượt đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != AllocationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể hết hạn lượt giữ chỗ khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
        }

        if (HoldExpiresAt.HasValue && now < HoldExpiresAt.Value)
        {
            return Error.Validation("HOLD_NOT_YET_EXPIRED", "Lượt giữ chỗ chưa đến thời điểm hết hạn.");
        }

        const string reason = "Hết hạn thời gian giữ chỗ tự động";
        Status = AllocationStatus.Released;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: AllocationOperationType.ExpireHold,
            fromStatus: AllocationStatus.Held,
            toStatus: AllocationStatus.Released,
            now: now,
            actor: actor,
            reason: reason,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }

    /// <summary>
    /// Hủy một lượt phân bổ chính thức (Allocated -> Cancelled).
    /// </summary>
    public Result Cancel(string reason, DateTimeOffset now, string actor, string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
        {
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        }

        var replay = CheckReplay(operationKey, AllocationOperationType.Cancel, [actor.Trim(), reason?.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("ALLOCATION_IN_TERMINAL_STATE", $"Không thể hủy lượt phân bổ đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != AllocationStatus.Allocated)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể hủy lượt phân bổ khi đang ở trạng thái Allocated. Trạng thái hiện tại: {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("REASON_REQUIRED", "Cần cung cấp lý do khi hủy lượt phân bổ.");
        }

        string trimmedReason = reason.Trim();
        if (trimmedReason.Length > MaxCancellationReasonLength)
        {
            return Error.Validation("REASON_TOO_LONG", $"Lý do hủy không được vượt quá {MaxCancellationReasonLength} ký tự.");
        }

        Status = AllocationStatus.Cancelled;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: AllocationOperationType.Cancel,
            fromStatus: AllocationStatus.Allocated,
            toStatus: AllocationStatus.Cancelled,
            now: now,
            actor: actor,
            reason: trimmedReason,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }

    /// <summary>
    /// Giải phóng lượt phân bổ chính thức theo yêu cầu scheduling (Allocated -> Released).
    /// </summary>
    public Result Release(string reason, DateTimeOffset now, string actor, string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
        {
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        }

        var replay = CheckReplay(operationKey, AllocationOperationType.Release, [actor.Trim(), reason?.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("ALLOCATION_IN_TERMINAL_STATE", $"Không thể giải phóng lượt phân bổ đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != AllocationStatus.Allocated)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể giải phóng khi đang ở trạng thái Allocated. Trạng thái hiện tại: {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("REASON_REQUIRED", "Cần cung cấp lý do khi giải phóng lượt phân bổ.");
        }

        string trimmedReason = reason.Trim();
        if (trimmedReason.Length > MaxCancellationReasonLength)
        {
            return Error.Validation("REASON_TOO_LONG", $"Lý do giải phóng không được vượt quá {MaxCancellationReasonLength} ký tự.");
        }

        Status = AllocationStatus.Released;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: AllocationOperationType.Release,
            fromStatus: AllocationStatus.Allocated,
            toStatus: AllocationStatus.Released,
            now: now,
            actor: actor,
            reason: trimmedReason,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
}
