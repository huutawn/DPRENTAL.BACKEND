using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý các thông báo vận hành của tổ chức (Notification).
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Lấy thông báo theo ID.
    /// </summary>
    /// <param name="id">Định danh thông báo.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Đối tượng Notification nếu tìm thấy, ngược lại null.</returns>
    Task<Notification?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm thông báo theo khóa chống trùng lặp (DeduplicationKey) trong phạm vi tổ chức.
    /// </summary>
    /// <param name="organizationId">Định danh tổ chức.</param>
    /// <param name="deduplicationKey">Khóa chống trùng lặp duy nhất theo sự kiện.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Notification nếu đã tồn tại, ngược lại null.</returns>
    Task<Notification?> GetByDeduplicationKeyAsync(
        Guid organizationId,
        string deduplicationKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách thông báo chưa xem của một tổ chức (chưa xem và chưa hết hạn 90 ngày) có phân trang.
    /// </summary>
    /// <param name="organizationId">Định danh tổ chức.</param>
    /// <param name="now">Thời điểm hiện tại (UTC) để lọc hết hạn.</param>
    /// <param name="skip">Số bản ghi bỏ qua.</param>
    /// <param name="take">Số bản ghi cần lấy.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các thông báo chưa xem còn hiệu lực.</returns>
    Task<IReadOnlyList<Notification>> ListUnreadByOrganizationAsync(
        Guid organizationId,
        DateTimeOffset now,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy toàn bộ danh sách thông báo của một tổ chức có phân trang.
    /// </summary>
    /// <param name="organizationId">Định danh tổ chức.</param>
    /// <param name="skip">Số bản ghi bỏ qua.</param>
    /// <param name="take">Số bản ghi cần lấy.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách thông báo của tổ chức.</returns>
    Task<IReadOnlyList<Notification>> ListAllByOrganizationAsync(
        Guid organizationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa thông báo mới vào hàng đợi lưu trữ (stage changes).
    /// </summary>
    /// <param name="notification">Thông báo cần thêm.</param>
    void Add(Notification notification);

    /// <summary>
    /// Đánh dấu cập nhật thông báo (khi đánh dấu đã xem).
    /// </summary>
    /// <param name="notification">Thông báo cần cập nhật.</param>
    void Update(Notification notification);
}
