namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Kết quả renter hủy đơn trước thanh toán. Được lưu trong <c>ApiResponseJson</c> của receipt
/// để gửi lại cùng Idempotency-Key trả đúng kết quả đã chốt.
/// </summary>
/// 
public sealed record CancelOrderResponse(
    Guid OrderId,
    string OrderCode,
    string Status,
    DateTimeOffset CancelledAt);
