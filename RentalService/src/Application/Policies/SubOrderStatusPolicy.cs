using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.Policies;

/// <summary>
/// Trạng thái hiển thị của đơn con, tính từ trạng thái các dòng (đơn con không lưu trạng thái riêng).
/// </summary>
public static class SubOrderStatusPolicy
{
    // Dòng chưa xong ở bước sớm nhất quyết định trạng thái: còn dòng chờ xác nhận thì đơn con là chờ xác nhận.
    private static readonly RentalItemStatus[] ActiveInWorkflowOrder =
    [
        RentalItemStatus.PendingPayment,
        RentalItemStatus.PendingSupplierConfirmation,
        RentalItemStatus.AwaitingHandover,
        RentalItemStatus.InRental,
        RentalItemStatus.PendingSettlement
    ];

    public static RentalItemStatus Summarize(IReadOnlyCollection<RentalItemStatus> itemStatuses)
    {
        ArgumentNullException.ThrowIfNull(itemStatuses);
        if (itemStatuses.Count == 0)
        {
            throw new ArgumentException("Đơn con phải có ít nhất một dòng.", nameof(itemStatuses));
        }

        foreach (RentalItemStatus status in ActiveInWorkflowOrder)
        {
            if (itemStatuses.Contains(status))
            {
                return status;
            }
        }

        // Mọi dòng đã kết thúc.
        if (itemStatuses.Contains(RentalItemStatus.Completed))
        {
            return RentalItemStatus.Completed;
        }

        if (itemStatuses.All(s => s == RentalItemStatus.Rejected))
        {
            return RentalItemStatus.Rejected;
        }

        return itemStatuses.Contains(RentalItemStatus.Expired)
            ? RentalItemStatus.Expired
            : RentalItemStatus.Cancelled;
    }
}
