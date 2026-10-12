namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Projection record chứa 4 chỉ số thống kê xác nhận của supplier từ Rental history.
/// </summary>
public sealed record SupplierConfirmationStatisticsRecord(
    long AcceptedCount,
    long RejectedCount,
    long ConfirmationTimeoutCount,
    long CancelAfterAcceptCount);
