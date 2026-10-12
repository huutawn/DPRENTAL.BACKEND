namespace Dprental.RentalService.Application.SupplierStatistics;

/// <summary>
/// DTO phản hồi thống kê xác nhận của Supplier cho Quản trị viên (Admin).
/// </summary>
/// <param name="SupplierId">Mã định danh duy nhất của Supplier.</param>
/// <param name="FromUtc">Thời điểm bắt đầu khoảng thời gian thống kê thực tế.</param>
/// <param name="ToUtc">Thời điểm kết thúc khoảng thời gian thống kê thực tế.</param>
/// <param name="AcceptedCount">Số lượng mục thuê đã được Supplier chấp nhận.</param>
/// <param name="RejectedCount">Số lượng mục thuê Supplier đã chủ động từ chối.</param>
/// <param name="ConfirmationTimeoutCount">Số lượng mục thuê bị hủy do Supplier quá hạn phản hồi.</param>
/// <param name="CancelAfterAcceptCount">Số lượng mục thuê bị hủy sau khi đã được Supplier chấp nhận.</param>
public sealed record SupplierConfirmationStatisticsResponse(
    Guid SupplierId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long AcceptedCount,
    long RejectedCount,
    long ConfirmationTimeoutCount,
    long CancelAfterAcceptCount);
