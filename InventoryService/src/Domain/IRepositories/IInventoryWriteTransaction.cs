using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Giao diện thực thi giao dịch ghi nguyên tử có khóa mức thiết bị (Equipment-level advisory lock).
/// </summary>
public interface IInventoryWriteTransaction
{
    /// <summary>
    /// Thực thi thao tác ghi nguyên tử dưới sự bảo vệ của PostgreSQL pg_advisory_xact_lock.
    /// Mọi thay đổi dữ liệu và xuất bản Outbox được lưu và commit trong cùng một boundary.
    /// Nếu callback trả về thất bại hoặc phát sinh lỗi, toàn bộ giao dịch sẽ tự động rollback.
    /// </summary>
    /// <typeparam name="T">Kiểu dữ liệu kết quả trả về khi thành công.</typeparam>
    /// <param name="equipmentId">Định danh thiết bị cần khóa độc quyền.</param>
    /// <param name="operation">Callback chứa logic nghiệp vụ và các thay đổi dữ liệu.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Result chứa kết quả của callback nếu commit thành công, hoặc lỗi nếu rollback.</returns>
    Task<Result<T>> ExecuteAsync<T>(
        Guid equipmentId,
        Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken = default);
}
