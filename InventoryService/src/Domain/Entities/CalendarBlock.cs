using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho một khoảng thời gian chặn lịch sử dụng thiết bị (Calendar Block).
/// Phục vụ các mục đích bảo trì, sử dụng nội bộ hoặc kiểm định chất lượng, ngăn không cho khách thuê đặt lịch.
/// </summary>
[Table("calendar_blocks", Schema = "inventory")]
public sealed class CalendarBlock
{
    /// <summary>
    /// Định danh duy nhất toàn cục của bản ghi chặn lịch.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Định danh thiết bị trong kho bị chặn lịch.
    /// </summary>
    [Column("equipment_id")]
    public Guid EquipmentId { get; private set; }

    /// <summary>
    /// Ngày bắt đầu chặn lịch (tính theo ngày dương lịch tại Việt Nam, inclusive).
    /// </summary>
    [Column("start_date")]
    public DateOnly StartDate { get; private set; }

    /// <summary>
    /// Ngày kết thúc chặn lịch (tính theo ngày dương lịch tại Việt Nam, inclusive).
    /// </summary>
    [Column("end_date")]
    public DateOnly EndDate { get; private set; }

    /// <summary>
    /// Phân loại lý do chặn lịch (Maintenance, OwnerReserved, Inspection, Other).
    /// </summary>
    [Column("reason")]
    public CalendarBlockReason Reason { get; private set; }

    /// <summary>
    /// Ghi chú chi tiết về mục đích hoặc nội dung của đợt chặn lịch.
    /// </summary>
    [Column("notes")]
    [MaxLength(500)]
    public string? Notes { get; private set; }

    /// <summary>
    /// Định danh hoặc tên người dùng/quản trị viên thực hiện thao tác chặn lịch.
    /// </summary>
    [Column("created_by")]
    [MaxLength(200)]
    public string CreatedBy { get; private set; } = string.Empty;

    /// <summary>
    /// Thời điểm tạo bản ghi chặn lịch (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Concurrency token ánh xạ tới xmin của PostgreSQL phục vụ phát hiện stale updates.
    /// </summary>
    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    /// <summary>
    /// Bản ghi thiết bị liên kết với khoảng chặn lịch này.
    /// </summary>
    [ForeignKey(nameof(EquipmentId))]
    public InventoryEquipment Equipment { get; private set; } = null!;

    /// <summary>
    /// Khởi tạo private phục vụ EF Core.
    /// </summary>
    private CalendarBlock()
    {
    }

    /// <summary>
    /// Khởi tạo bản ghi chặn lịch mới.
    /// </summary>
    private CalendarBlock(
        Guid id,
        Guid equipmentId,
        DateOnly startDate,
        DateOnly endDate,
        CalendarBlockReason reason,
        string createdBy,
        DateTimeOffset now,
        string? notes)
    {
        Id = id;
        EquipmentId = equipmentId;
        StartDate = startDate;
        EndDate = endDate;
        Reason = reason;
        CreatedBy = createdBy;
        CreatedAt = now;
        Notes = notes;
    }

    /// <summary>
    /// Tạo mới một khoảng chặn lịch cho thiết bị với kiểm tra tính hợp lệ về ngày và chân trời nghiệp vụ.
    /// Lưu ý: Không áp dụng giới hạn 30 ngày của lượt thuê lên khoảng chặn lịch, nhưng tối đa không quá chân trời 6 tháng.
    /// </summary>
    /// <param name="id">ID duy nhất của block.</param>
    /// <param name="equipmentId">ID thiết bị cần chặn lịch.</param>
    /// <param name="startDate">Ngày bắt đầu chặn lịch.</param>
    /// <param name="endDate">Ngày kết thúc chặn lịch.</param>
    /// <param name="reason">Lý do chặn lịch.</param>
    /// <param name="createdBy">Người thực hiện chặn lịch.</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <param name="today">Ngày hôm nay tại Việt Nam.</param>
    /// <param name="notes">Ghi chú bổ sung.</param>
    /// <param name="maxMonthsAhead">Chân trời tối đa cho phép chặn lịch (mặc định 6 tháng).</param>
    /// <returns>Result chứa CalendarBlock nếu hợp lệ hoặc lỗi validation.</returns>
    public static Result<CalendarBlock> Create(
        Guid id,
        Guid equipmentId,
        DateOnly startDate,
        DateOnly endDate,
        CalendarBlockReason reason,
        string createdBy,
        DateTimeOffset now,
        DateOnly today,
        string? notes = null,
        int maxMonthsAhead = 6)
    {
        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_BLOCK_ID", "Calendar Block ID không được rỗng.");
        }

        if (equipmentId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_ID", "Equipment ID không được rỗng.");
        }

        if (string.IsNullOrWhiteSpace(createdBy))
        {
            return Error.Validation("CREATED_BY_REQUIRED", "Cần cung cấp thông tin người tạo chặn lịch.");
        }

        if (startDate > endDate)
        {
            return Error.Validation("INVALID_DATE_ORDER", "Ngày bắt đầu chặn lịch không thể sau ngày kết thúc.");
        }

        if (startDate < today)
        {
            return Error.Validation("START_DATE_IN_PAST", "Ngày bắt đầu chặn lịch không thể trước ngày hôm nay.");
        }

        DateOnly maxHorizon = today.AddMonths(maxMonthsAhead);
        if (endDate > maxHorizon)
        {
            return Error.Validation("EXCEEDS_BLOCK_HORIZON", $"Ngày kết thúc chặn lịch không thể vượt quá chân trời {maxMonthsAhead} tháng ({maxHorizon:yyyy-MM-dd}).");
        }

        return new CalendarBlock(
            id: id,
            equipmentId: equipmentId,
            startDate: startDate,
            endDate: endDate,
            reason: reason,
            createdBy: createdBy.Trim(),
            now: now,
            notes: notes?.Trim());
    }

    /// <summary>
    /// Kiểm tra khoảng chặn lịch có bị trùng lặp với một khoảng thời gian khác hay không.
    /// </summary>
    /// <param name="start">Ngày bắt đầu cần so sánh.</param>
    /// <param name="end">Ngày kết thúc cần so sánh.</param>
    /// <returns>True nếu trùng lặp, ngược lại False.</returns>
    public bool Overlaps(DateOnly start, DateOnly end)
    {
        return StartDate <= end && EndDate >= start;
    }
}
