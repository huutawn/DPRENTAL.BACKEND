using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy xuất các phiên saga phân tán (Saga Pattern).
/// </summary>
public interface ISagaStore
{
    /// <summary>
    /// Tìm kiếm phiên saga theo SagaType và CorrelationId.
    /// </summary>
    /// <param name="sagaType">Loại saga nghiệp vụ.</param>
    /// <param name="correlationId">Mã tương quan chuỗi xử lý.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Phiên SagaInstance nếu tìm thấy, ngược lại null.</returns>
    Task<SagaInstance?> GetAsync(
        string sagaType,
        string correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm phiên saga mới vào cơ sở dữ liệu.
    /// </summary>
    /// <param name="saga">Phiên saga cần lưu.</param>
    void Add(SagaInstance saga);

    /// <summary>
    /// Cập nhật trạng thái và dữ liệu của phiên saga.
    /// </summary>
    /// <param name="saga">Phiên saga cần cập nhật.</param>
    void Update(SagaInstance saga);
}
