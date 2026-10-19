using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    public const string CancelNotDeliveredAction = "CancelNotDelivered";

    /// <summary>
    /// Hủy dòng thuê do thiết bị không được bàn giao đến công trình:
    /// Chỉ cho phép khi dòng đang ở trạng thái AWAITING_HANDOVER.
    /// Nếu dòng đã là Cancelled từ trước thì trả Result.Success() ngay mà không thêm lịch sử (idempotent).
    /// </summary>
    public Result ApplyNotDeliveredCancellation(
        Guid actorId,
        ActorRole role,
        DateTimeOffset utcNow,
        string reason,
        string operationKey)
    {
        if (Status == RentalItemStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status != RentalItemStatus.AwaitingHandover)
        {
            return RentalErrors.InvalidItemStatus;
        }

        string from = Status.ToString();
        Status = RentalItemStatus.Cancelled;

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            from,
            RentalItemStatus.Cancelled.ToString(),
            CancelNotDeliveredAction,
            actorId,
            role,
            reason,
            utcNow,
            operationKey));

        return Result.Success();
    }
}
