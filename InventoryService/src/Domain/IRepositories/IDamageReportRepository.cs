using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý các báo cáo sự cố hư hỏng thiết bị (DamageReport).
/// </summary>
public interface IDamageReportRepository
{
    /// <summary>
    /// Lấy báo cáo hư hỏng theo ID.
    /// </summary>
    /// <param name="id">Định danh bản ghi báo cáo.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Đối tượng DamageReport nếu tìm thấy, ngược lại null.</returns>
    Task<DamageReport?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách toàn bộ báo cáo hư hỏng thuộc về một lượt đặt lịch cụ thể.
    /// </summary>
    /// <param name="reservationId">Định danh lượt đặt lịch.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các báo cáo hư hỏng.</returns>
    Task<IReadOnlyList<DamageReport>> ListByReservationAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách báo cáo hư hỏng của một thiết bị cụ thể qua các lượt thuê.
    /// </summary>
    /// <param name="equipmentId">Định danh thiết bị.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các báo cáo hư hỏng của thiết bị.</returns>
    Task<IReadOnlyList<DamageReport>> ListByEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách báo cáo hư hỏng do một khách thuê gửi có phân trang.
    /// </summary>
    /// <param name="reporterAccountId">Định danh tài khoản khách thuê.</param>
    /// <param name="skip">Số bản ghi bỏ qua.</param>
    /// <param name="take">Số bản ghi cần lấy.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các báo cáo do khách thuê gửi.</returns>
    Task<IReadOnlyList<DamageReport>> ListByReporterAsync(
        Guid reporterAccountId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa báo cáo hư hỏng mới vào hàng đợi lưu trữ (stage changes).
    /// </summary>
    /// <param name="report">Báo cáo hư hỏng cần thêm.</param>
    void Add(DamageReport report);

    /// <summary>
    /// Đánh dấu cập nhật báo cáo hư hỏng (khi xem xét / review).
    /// </summary>
    /// <param name="report">Báo cáo hư hỏng cần cập nhật.</param>
    void Update(DamageReport report);
}
