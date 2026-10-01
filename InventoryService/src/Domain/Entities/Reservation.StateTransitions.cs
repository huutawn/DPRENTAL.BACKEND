using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;

namespace Dprental.InventoryService.Domain.Entities;

public sealed partial class Reservation
{
    public Result ConfirmHold(DateTimeOffset now, string actor = "renter", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        var replay = CheckReplay(operationKey, ReservationOperationType.ConfirmHold, [actor.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("RESERVATION_IN_TERMINAL_STATE", $"Không thể xác nhận lượt đặt lịch đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != ReservationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể xác nhận lượt đặt lịch khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
        }

        if (HoldExpiresAt.HasValue && now >= HoldExpiresAt.Value)
        {
            return Error.Expired("HOLD_EXPIRED", "Lượt giữ chỗ đã hết hạn và không thể xác nhận.");
        }

        Status = ReservationStatus.Confirmed;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: ReservationOperationType.ConfirmHold,
            fromStatus: ReservationStatus.Held,
            toStatus: ReservationStatus.Confirmed,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
    public Result ExtendHold(DateTimeOffset newHoldExpiresAt, DateTimeOffset now, string actor = "renter", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        var replay = CheckReplay(operationKey, ReservationOperationType.ExtendHold, [actor.Trim(), BusinessDateTime.NormalizeUtc(newHoldExpiresAt)], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("RESERVATION_IN_TERMINAL_STATE", $"Không thể gia hạn lượt đặt lịch đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != ReservationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể gia hạn lượt đặt lịch khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
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
            return Error.Validation("HOLD_EXPIRES_AFTER_START", "Thời hạn giữ chỗ mới phải trước 00:00 ngày bắt đầu thuê theo giờ Việt Nam.");
        }

        DateTimeOffset previousHoldExpiresAt = HoldExpiresAt.Value;
        HoldExpiresAt = BusinessDateTime.NormalizeUtc(newHoldExpiresAt);
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: ReservationOperationType.ExtendHold,
            fromStatus: ReservationStatus.Held,
            toStatus: ReservationStatus.Held,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: previousHoldExpiresAt,
            newHoldExpiresAt: BusinessDateTime.NormalizeUtc(newHoldExpiresAt),
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
    public Result ReleaseHold(string reason, DateTimeOffset now, string actor = "renter", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        var replay = CheckReplay(operationKey, ReservationOperationType.ReleaseHold, [actor.Trim(), reason?.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("RESERVATION_IN_TERMINAL_STATE", $"Không thể nhả lượt giữ chỗ đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != ReservationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể nhả lượt giữ chỗ khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("REASON_REQUIRED", "Cần cung cấp lý do khi nhả giữ chỗ.");
        }

        if (reason.Trim().Length > MaxExternalCancellationReasonLength)
            return Error.Validation("REASON_TOO_LONG", "Lý do nhả tối đa 200 ký tự.");

        Status = ReservationStatus.Released;
        CancelledAt = BusinessDateTime.NormalizeUtc(now);
        CancellationReason = reason.Trim();
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: ReservationOperationType.ReleaseHold,
            fromStatus: ReservationStatus.Held,
            toStatus: ReservationStatus.Released,
            now: now,
            actor: actor,
            reason: reason.Trim(),
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
    public Result ExpireHold(DateTimeOffset now, string actor = "system", string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        var replay = CheckReplay(operationKey, ReservationOperationType.ExpireHold, [actor.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("RESERVATION_IN_TERMINAL_STATE", $"Không thể xử lý hết hạn cho lượt đặt lịch đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != ReservationStatus.Held)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể hết hạn lượt giữ chỗ khi đang ở trạng thái Held. Trạng thái hiện tại: {Status}.");
        }

        if (HoldExpiresAt.HasValue && now < HoldExpiresAt.Value)
        {
            return Error.Validation("HOLD_NOT_YET_EXPIRED", "Lượt giữ chỗ chưa đến thời điểm hết hạn.");
        }

        const string reason = "Hết hạn thời gian giữ chỗ tự động";
        Status = ReservationStatus.Released;
        CancelledAt = BusinessDateTime.NormalizeUtc(now);
        CancellationReason = reason;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: ReservationOperationType.ExpireHold,
            fromStatus: ReservationStatus.Held,
            toStatus: ReservationStatus.Released,
            now: now,
            actor: actor,
            reason: reason,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
    public Result Cancel(string reason, DateTimeOffset now, string actor, string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        var replay = CheckReplay(operationKey, ReservationOperationType.Cancel, [actor.Trim(), reason?.Trim()], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("RESERVATION_IN_TERMINAL_STATE", $"Không thể hủy lượt đặt lịch đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != ReservationStatus.Confirmed)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể hủy lượt đặt lịch khi đang ở trạng thái Confirmed (chưa bàn giao). Trạng thái hiện tại: {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("REASON_REQUIRED", "Cần cung cấp lý do khi hủy lượt đặt lịch.");
        }

        string trimmedReason = reason.Trim();
        if (SourceType == ReservationSourceType.Manual && trimmedReason.Length > MaxManualCancellationReasonLength)
        {
            return Error.Validation("REASON_TOO_LONG", $"Lý do hủy cho lượt đặt thủ công không được vượt quá {MaxManualCancellationReasonLength} ký tự.");
        }

        if (SourceType == ReservationSourceType.External && trimmedReason.Length > MaxExternalCancellationReasonLength)
        {
            return Error.Validation("REASON_TOO_LONG", $"Lý do hủy cho lượt đặt ngoài không được vượt quá {MaxExternalCancellationReasonLength} ký tự.");
        }

        Status = ReservationStatus.Cancelled;
        CancelledAt = BusinessDateTime.NormalizeUtc(now);
        CancellationReason = trimmedReason;
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: ReservationOperationType.Cancel,
            fromStatus: ReservationStatus.Confirmed,
            toStatus: ReservationStatus.Cancelled,
            now: now,
            actor: actor,
            reason: trimmedReason,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
    public Result Deliver(
        string actor,
        DateTimeOffset deliveredAt,
        string? conditionNotes,
        IReadOnlyList<Guid>? photoIds,
        DateTimeOffset now,
        string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        var replay = CheckReplay(operationKey, ReservationOperationType.Deliver, [actor.Trim(), BusinessDateTime.NormalizeUtc(deliveredAt), conditionNotes?.Trim(), photoIds?.ToArray() ?? []], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("RESERVATION_IN_TERMINAL_STATE", $"Không thể bàn giao lượt đặt lịch đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != ReservationStatus.Confirmed)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể bàn giao thiết bị khi lượt đặt lịch ở trạng thái Confirmed. Trạng thái hiện tại: {Status}.");
        }

        if (photoIds != null && photoIds.Count > MaxPhotosPerGroup)
        {
            return Error.Validation("TOO_MANY_PHOTOS", $"Tối đa {MaxPhotosPerGroup} ảnh minh chứng khi bàn giao thiết bị.");
        }

        // Bất biến: dữ liệu bàn giao đã ghi không được sửa lại
        if (DeliveredAt.HasValue)
        {
            return Error.Conflict("ALREADY_DELIVERED", "Thông tin bàn giao đã được ghi nhận trước đó và không thể sửa đổi.");
        }

        if (deliveredAt > now || deliveredAt < BusinessDateTime.StartOfDayUtc(RentalPeriod.StartDate.AddDays(-1)))
            return Error.Validation("INVALID_DELIVERY_TIME", "Ngày giao không hợp lệ.");
        if (conditionNotes?.Trim().Length > 500 || photoIds?.Any(id => id == Guid.Empty) == true)
            return Error.Validation("INVALID_DELIVERY_EVIDENCE", "Ghi chú hoặc ảnh giao không hợp lệ.");

        Status = ReservationStatus.InUse;
        DeliveredAt = BusinessDateTime.NormalizeUtc(deliveredAt);
        DeliveredBy = actor.Trim();
        DeliveryCondition = conditionNotes?.Trim();
        _deliveryPhotoIds = photoIds?.ToList() ?? [];
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: ReservationOperationType.Deliver,
            fromStatus: ReservationStatus.Confirmed,
            toStatus: ReservationStatus.InUse,
            now: now,
            actor: actor,
            reason: conditionNotes,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
    public Result Return(
        string actor,
        DateTimeOffset returnedAt,
        EquipmentReturnCondition condition,
        string? conditionNotes,
        IReadOnlyList<Guid>? photoIds,
        DateTimeOffset now,
        string? operationKey = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200)
            return Error.Validation("ACTOR_REQUIRED", "Actor không hợp lệ.");
        var replay = CheckReplay(operationKey, ReservationOperationType.Return, [actor.Trim(), BusinessDateTime.NormalizeUtc(returnedAt), condition, conditionNotes?.Trim(), photoIds?.ToArray() ?? []], out var fingerprint);
        if (replay is not null) return replay;

        if (IsTerminalState())
        {
            return Error.Conflict("RESERVATION_IN_TERMINAL_STATE", $"Không thể thu hồi lượt đặt lịch đã ở trạng thái kết thúc ({Status}).");
        }

        if (Status != ReservationStatus.InUse)
        {
            return Error.Validation("INVALID_STATUS_TRANSITION", $"Chỉ có thể thu hồi thiết bị khi đang ở trạng thái InUse. Trạng thái hiện tại: {Status}.");
        }

        if (photoIds != null && photoIds.Count > MaxPhotosPerGroup)
        {
            return Error.Validation("TOO_MANY_PHOTOS", $"Tối đa {MaxPhotosPerGroup} ảnh minh chứng khi thu hồi thiết bị.");
        }

        // Bất biến: dữ liệu thu hồi đã ghi không được sửa lại
        if (ReturnedAt.HasValue)
        {
            return Error.Conflict("ALREADY_RETURNED", "Thông tin thu hồi đã được ghi nhận trước đó và không thể sửa đổi.");
        }

        if (!DeliveredAt.HasValue || BusinessDateTime.NormalizeUtc(returnedAt) <= DeliveredAt.Value || returnedAt > now)
            return Error.Validation("INVALID_RETURN_TIME", "Thu hồi phải sau giao và không sau hiện tại.");
        if (!Enum.IsDefined(condition) || conditionNotes?.Trim().Length > 1000 || photoIds?.Any(id => id == Guid.Empty) == true ||
            condition == EquipmentReturnCondition.Damaged && (string.IsNullOrWhiteSpace(conditionNotes) || photoIds is null || photoIds.Count == 0))
            return Error.Validation("INVALID_RETURN_EVIDENCE", "Thu hồi có hư hỏng cần mô tả và ít nhất một ảnh.");

        Status = ReservationStatus.Completed;
        ReturnedAt = BusinessDateTime.NormalizeUtc(returnedAt);
        ReturnedBy = actor.Trim();
        ReturnCondition = condition;
        ReturnConditionNotes = conditionNotes?.Trim();
        _returnPhotoIds = photoIds?.ToList() ?? [];
        UpdatedAt = BusinessDateTime.NormalizeUtc(now);

        AddTransition(
            operationType: ReservationOperationType.Return,
            fromStatus: ReservationStatus.InUse,
            toStatus: ReservationStatus.Completed,
            now: now,
            actor: actor,
            reason: conditionNotes,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: fingerprint);

        return Result.Success();
    }
}
