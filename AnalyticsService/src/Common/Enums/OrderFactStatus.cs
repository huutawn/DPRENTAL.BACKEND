namespace Dprental.AnalyticsService.Common.Enums;

/// <summary>
/// Trạng thái của đơn trong read model. Chỉ đổi theo event cấp đơn của Rental, không suy ra từ item.
/// </summary>
public enum OrderFactStatus
{
    PendingPayment, // Đã tạo, chờ thanh toán (RentalOrderCreated)
    Paid,           // Đã thanh toán, đang xử lý (RentalOrderPaid)
    Completed,      // Hoàn thành (RentalOrderCompleted)
    Cancelled,      // Bị hủy (RentalOrderCancelled)
    Expired,        // Hết hạn (RentalOrderExpired)
    Rejected        // Mọi item bị NCC từ chối (RentalOrderRejected, chờ R1)
}
