namespace Dprental.NotificationService.Application.Email.Services;

/// <summary>
/// Dịch vụ Application điều phối việc gửi email cho các job đang chờ xử lý (Pending / RetryScheduled).
/// </summary>
public interface IEmailDeliveryService
{
    /// <summary>
    /// Claim và thực hiện gửi email cho một batch công việc theo đúng quy tắc lease fencing và error classification.
    /// </summary>
    /// <param name="workerId">Định danh worker thực hiện claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Số lượng job đã được xử lý trong batch.</returns>
    Task<int> DeliverPendingJobsAsync(string workerId, CancellationToken cancellationToken = default);
}
