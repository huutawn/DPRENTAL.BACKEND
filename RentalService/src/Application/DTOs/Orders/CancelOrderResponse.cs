namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Kết quả renter hủy đơn trước thanh toán hoặc trước khi nhà cung cấp xác nhận. Được lưu trong <c>ApiResponseJson</c>
/// của receipt để gửi lại cùng Idempotency-Key trả đúng kết quả đã chốt.
/// </summary>
/// <param name="OrderId">Mã định danh đơn.</param>
/// <param name="OrderCode">Mã đơn hiển thị.</param>
/// <param name="Status">Trạng thái đơn sau khi hủy.</param>
/// <param name="CancelledAt">Thời điểm hủy.</param>
/// <param name="RefundAmount">Tổng tiền thuê và tiền cọc sẽ được hoàn; 0 khi đơn chưa thanh toán.</param>
public sealed record CancelOrderResponse(
    Guid OrderId,
    string OrderCode,
    string Status,
    DateTimeOffset CancelledAt,
    decimal RefundAmount = 0m);
