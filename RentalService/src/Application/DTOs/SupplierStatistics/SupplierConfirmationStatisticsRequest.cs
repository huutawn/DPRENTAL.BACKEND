namespace Dprental.RentalService.Application.DTOs.SupplierStatistics;

/// <summary>
/// Yêu cầu lấy thống kê xác nhận của Supplier từ query parameters.
/// </summary>
/// <param name="SupplierId">Mã định danh duy nhất của Supplier.</param>
/// <param name="FromUtc">Thời điểm bắt đầu khoảng thống kê theo chuẩn ISO-8601 UTC (tùy chọn).</param>
/// <param name="ToUtc">Thời điểm kết thúc khoảng thống kê theo chuẩn ISO-8601 UTC (tùy chọn).</param>
public sealed record SupplierConfirmationStatisticsRequest(
    Guid SupplierId,
    string? FromUtc,
    string? ToUtc);
