namespace Dprental.AuthService.Application.IServices;

/// <summary>Dịch vụ quản lý ghi và xử lý các tác vụ outbox đồng bộ visibility cho Media.</summary>
public interface IMediaOutboxService
{
    /// <summary>Ghi tác vụ cập nhật visibility thành public cho MediaId trong cùng transaction hiện tại.</summary>
    Task QueuePublicVisibilityAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>Xử lý một đợt các tác vụ outbox đang chờ, gọi MediaService sau commit và cập nhật trạng thái.</summary>
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default);
}
