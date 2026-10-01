using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và kiểm tra tính duy nhất của thông điệp tiếp nhận (Transactional Inbox).
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Kiểm tra xem thông điệp có messageId đã từng được xử lý bởi consumer này hay chưa.
    /// </summary>
    /// <param name="consumer">Tên consumer tiếp nhận.</param>
    /// <param name="messageId">Định danh thông điệp từ Message Broker.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>True nếu thông điệp đã tồn tại trong inbox, ngược lại False.</returns>
    Task<bool> HasBeenReceivedAsync(
        string consumer,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy thông tin bản ghi inbox theo consumer và messageId.
    /// </summary>
    /// <param name="consumer">Tên consumer.</param>
    /// <param name="messageId">Định danh thông điệp.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Bản ghi InboxMessage nếu tìm thấy, ngược lại null.</returns>
    Task<InboxMessage?> GetAsync(
        string consumer,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm bản ghi thông điệp mới vào Inbox.
    /// </summary>
    /// <param name="message">Bản ghi cần thêm.</param>
    void Add(InboxMessage message);

    /// <summary>
    /// Cập nhật trạng thái xử lý của bản ghi Inbox.
    /// </summary>
    /// <param name="message">Bản ghi cần cập nhật.</param>
    void Update(InboxMessage message);
}
