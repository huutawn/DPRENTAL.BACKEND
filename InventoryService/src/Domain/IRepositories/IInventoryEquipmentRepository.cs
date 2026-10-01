using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý vòng đời và truy xuất thông tin thiết bị trong kho (InventoryEquipment).
/// </summary>
public interface IInventoryEquipmentRepository
{
    /// <summary>
    /// Lấy thông tin thiết bị theo Equipment ID.
    /// </summary>
    /// <param name="id">Định danh duy nhất của thiết bị.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Đối tượng thiết bị nếu tìm thấy, ngược lại null.</returns>
    Task<InventoryEquipment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy thông tin thiết bị theo ID và giới hạn trong phạm vi tổ chức sở hữu (tenant isolation).
    /// </summary>
    /// <param name="id">Định danh thiết bị.</param>
    /// <param name="organizationId">Định danh tổ chức sở hữu.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Đối tượng thiết bị nếu thuộc về tổ chức, ngược lại null.</returns>
    Task<InventoryEquipment?> GetByIdForOrganizationAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách thiết bị thuộc về một tổ chức có phân trang.
    /// </summary>
    /// <param name="organizationId">Định danh tổ chức.</param>
    /// <param name="skip">Số bản ghi bỏ qua.</param>
    /// <param name="take">Số bản ghi cần lấy.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách thiết bị tìm thấy.</returns>
    Task<IReadOnlyList<InventoryEquipment>> ListByOrganizationAsync(Guid organizationId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa thiết bị mới vào hàng đợi lưu trữ (stage changes).
    /// </summary>
    /// <param name="equipment">Đối tượng thiết bị cần thêm.</param>
    void Add(InventoryEquipment equipment);

    /// <summary>
    /// Đánh dấu cập nhật thông tin thiết bị (stage changes).
    /// </summary>
    /// <param name="equipment">Đối tượng thiết bị cần cập nhật.</param>
    void Update(InventoryEquipment equipment);
}
