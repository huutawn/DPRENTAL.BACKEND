namespace Dprental.RentalService.Application.Policies;

using Dprental.RentalService.Common.Enums;

/// <summary>
/// Người thuê được hủy cả đơn khi đơn chưa thanh toán, hoặc đã thanh toán nhưng chưa nhà cung cấp nào xác nhận.
/// Dùng chung cho lệnh hủy và cờ CanCancel trả cho client để hai nơi không lệch nhau.
/// </summary>
/// <remarks>
/// Chỉ đánh giá điều kiện trạng thái để hủy; Application Service/executor vẫn kiểm tra caller, sở hữu, khóa và tác vụ đang chạy.
/// </remarks>
public static class RenterOrderCancellationPolicy
{
    public static bool CanCancel(RentalOrderStatus orderStatus, IEnumerable<RentalItemStatus> itemStatuses) =>
        orderStatus switch
        {
            RentalOrderStatus.PendingPayment => true,
            RentalOrderStatus.Paid => IsAwaitingEveryConfirmation(itemStatuses),
            _ => false
        };

    /// <summary>
    /// Mọi dòng còn hiệu lực đều đang chờ xác nhận. Dòng đã bị từ chối, hết hạn hoặc đã hủy không chặn việc hủy phần còn lại.
    /// </summary>
    private static bool IsAwaitingEveryConfirmation(IEnumerable<RentalItemStatus> itemStatuses)
    {
        List<RentalItemStatus> active = itemStatuses
            .Where(s => s is not (RentalItemStatus.Rejected or RentalItemStatus.Expired or RentalItemStatus.Cancelled))
            .ToList();

        return active.Count > 0 && active.All(s => s == RentalItemStatus.PendingSupplierConfirmation);
    }
}
