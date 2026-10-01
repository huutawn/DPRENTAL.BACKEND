using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy xuất các thông điệp Transactional Outbox phục vụ hạ tầng phát thông điệp.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Thêm thông điệp Outbox mới vào cơ sở dữ liệu.
    /// </summary>
    /// <param name="message">Thông điệp cần lưu.</param>
    void Add(OutboxMessage message);

    /// <summary>
    /// Lấy danh sách các thông điệp chưa được xuất bản hoặc cần retry để xử lý.
    /// </summary>
    /// <param name="batchSize">Số lượng thông điệp tối đa lấy ra trong một đợt.</param>
    /// <param name="now">Thời điểm hiện tại (UTC) để kiểm tra hạn khóa lease.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các thông điệp Outbox cần phát.</returns>
    Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đánh dấu cập nhật trạng thái thông điệp Outbox.
    /// </summary>
    /// <param name="message">Thông điệp cần cập nhật.</param>
    void Update(OutboxMessage message);
}
