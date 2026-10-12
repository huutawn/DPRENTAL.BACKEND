namespace Dprental.RentalService.Application.SupplierStatistics;

/// <summary>
/// DTO phản hồi thống kê xác nhận của supplier cho Admin.
/// </summary>
public sealed record SupplierConfirmationStatisticsResponse(
    Guid SupplierId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long AcceptedCount,
    long RejectedCount,
    long ConfirmationTimeoutCount,
    long CancelAfterAcceptCount);
