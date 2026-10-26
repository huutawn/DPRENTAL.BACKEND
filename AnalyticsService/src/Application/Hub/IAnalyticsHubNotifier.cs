using Dprental.AnalyticsService.Common;

namespace Dprental.AnalyticsService.Application.Hub;

/// <summary>
/// Báo dashboard "số liệu đã đổi". Chỉ được gọi SAU KHI dữ liệu đã lưu DB.
/// Gửi lỗi trả Result thất bại, không ném exception: không được ảnh hưởng việc đọc Kafka.
/// </summary>
public interface IAnalyticsHubNotifier
{
    Task<Result> NotifyDataChangedAsync(string scope, IReadOnlyCollection<Guid> supplierUserIds, CancellationToken cancellationToken = default);
}
