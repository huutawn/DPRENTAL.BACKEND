namespace Dprental.RentalService.Application.SupplierStatistics;

/// <summary>
/// Yêu cầu lấy thống kê xác nhận của supplier từ query parameters.
/// </summary>
public sealed record SupplierConfirmationStatisticsRequest(
    Guid SupplierId,
    string? FromUtc,
    string? ToUtc);
