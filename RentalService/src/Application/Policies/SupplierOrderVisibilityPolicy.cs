using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Policies;

/// <summary>
/// Policy đánh giá khả năng hiển thị đơn thuê/đơn con đối với nhà cung cấp (Supplier Visibility).
/// Khớp 1:1 với ranh giới phân quyền đọc của REST (RentalOrderRepository.PaidSubOrdersOf):
/// Nhà cung cấp chỉ xem được đơn sau khi người thuê đã thanh toán đúng hạn (PaymentConfirmedAt != null)
/// và đơn không rơi vào trường hợp hoàn tiền do thanh toán trễ (!LatePaymentRefundRequired).
/// </summary>
public static class SupplierOrderVisibilityPolicy
{
    public static bool CanSupplierView(DateTimeOffset? paymentConfirmedAt, bool latePaymentRefundRequired)
    {
        return paymentConfirmedAt.HasValue && !latePaymentRefundRequired;
    }

    public static bool CanSupplierView(RentalOrder? order)
    {
        if (order is null) return false;
        return CanSupplierView(order.PaymentConfirmedAt, order.LatePaymentRefundRequired);
    }
}
