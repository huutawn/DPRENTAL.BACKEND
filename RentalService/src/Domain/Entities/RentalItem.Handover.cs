using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Domain.Entities;

public partial class RentalItem
{
    /// <summary>
    /// Người thuê xác nhận đã nhận thiết bị ngoài công trường: ghi nhận vào HandoverRecord và ghi 1 transition.
    /// Nếu đã xác nhận trước đó thì không ghi đè và không ghi thêm transition (idempotent).
    /// </summary>
    internal Result<HandoverRecord> AcknowledgeReceipt(
        Guid renterId,
        DateTimeOffset utcNow,
        string? note,
        string operationKey)
    {
        if (HandoverRecord is null)
        {
            return Error.Conflict("HANDOVER_NOT_RECORDED", "Mục thuê chưa có biên bản bàn giao từ nhà cung cấp.");
        }

        bool newlyAcknowledged = HandoverRecord.Acknowledge(renterId, utcNow, note);
        if (!newlyAcknowledged)
        {
            return HandoverRecord;
        }

        RentalOrder?.AddStateTransition(new RentalStateTransition(
            Guid.NewGuid(),
            RentalOrderId,
            Id,
            Status.ToString(),
            Status.ToString(),
            "RenterAcknowledgeReceipt",
            renterId,
            ActorRole.Renter,
            "Renter acknowledged receipt of equipment",
            utcNow,
            operationKey));

        return HandoverRecord;
    }
}
