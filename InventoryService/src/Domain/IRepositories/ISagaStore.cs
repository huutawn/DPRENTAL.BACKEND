using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy xuất các phiên saga phân tán (Saga Pattern) trên PostgreSQL.
/// Identity duy nhất là (SagaType, OrganizationId, CorrelationId).
/// </summary>
public interface ISagaStore
{
    /// <summary>
    /// Tìm kiếm phiên saga theo SagaType, OrganizationId và CorrelationId.
    /// </summary>
    Task<SagaInstance?> GetAsync(
        string sagaType,
        Guid organizationId,
        string correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc và khóa bản ghi saga bằng SELECT FOR UPDATE để điều phối bước tiếp theo trong transaction.
    /// </summary>
    Task<SagaInstance?> GetForUpdateAsync(
        string sagaType,
        Guid organizationId,
        string correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quét và claim các saga đang chờ timeout đã quá hạn (timeout_deadline <= now) bằng FOR UPDATE SKIP LOCKED.
    /// Chỉ lấy các saga chưa ở trạng thái terminal và chưa cạn budget retry (retry_count < 5).
    /// </summary>
    Task<IReadOnlyList<SagaInstance>> ClaimOverdueTimeoutsAsync(
        int batchSize,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm phiên saga mới vào cơ sở dữ liệu.
    /// </summary>
    void Add(SagaInstance saga);

    /// <summary>
    /// Cập nhật trạng thái và dữ liệu của phiên saga.
    /// </summary>
    void Update(SagaInstance saga);
}
