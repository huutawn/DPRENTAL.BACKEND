using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý các khoảng thời gian chặn lịch của thiết bị (CalendarBlock).
/// </summary>
public interface ICalendarBlockRepository : IRepo<CalendarBlock, Guid>
{
    /// <summary>
    /// Lấy danh sách các khoảng chặn lịch bị trùng lặp với khoảng thời gian truy vấn trên thiết bị.
    /// </summary>
    /// <param name="equipmentId">Định danh thiết bị.</param>
    /// <param name="startDate">Ngày bắt đầu khoảng cần kiểm tra.</param>
    /// <param name="endDate">Ngày kết thúc khoảng cần kiểm tra.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các khoảng chặn lịch trùng lặp.</returns>
    Task<IReadOnlyList<CalendarBlock>> GetOverlappingBlocksAsync(
        Guid equipmentId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy toàn bộ danh sách khoảng chặn lịch của một thiết bị có phân trang.
    /// </summary>
    /// <param name="equipmentId">Định danh thiết bị.</param>
    /// <param name="skip">Số bản ghi bỏ qua.</param>
    /// <param name="take">Số bản ghi cần lấy.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các khoảng chặn lịch.</returns>
    Task<IReadOnlyList<CalendarBlock>> ListByEquipmentAsync(
        Guid equipmentId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa khoảng chặn lịch mới vào hàng đợi lưu trữ (stage changes).
    /// </summary>
    /// <param name="block">Khoảng chặn lịch cần thêm.</param>
    void Add(CalendarBlock block);

    /// <summary>
    /// Đưa khoảng chặn lịch vào danh sách xóa (gỡ chặn lịch cho phép xóa bản ghi).
    /// </summary>
    /// <param name="block">Khoảng chặn lịch cần gỡ bỏ.</param>
    void Remove(CalendarBlock block);
}
