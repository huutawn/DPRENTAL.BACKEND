using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho thiết bị trong kho Inventory, liên kết với Catalog Equipment và thuộc về một tổ chức.
/// </summary>
[Table("equipments", Schema = "inventory")]
public sealed class InventoryEquipment
{
    /// <summary>
    /// Định danh duy nhất của thiết bị trong kho, trùng khớp với Equipment ID từ Catalog Service.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Định danh của tổ chức sở hữu thiết bị này.
    /// </summary>
    [Column("organization_id")]
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Trạng thái vòng đời thiết bị theo Catalog Service (Active, Inactive, Archived).
    /// </summary>
    [Column("catalog_lifecycle_status")]
    public CatalogLifecycleStatus CatalogLifecycleStatus { get; private set; }

    /// <summary>
    /// Cờ xác định tổ chức có đang cho phép nhận đặt lịch (bookings) cho thiết bị này hay không.
    /// </summary>
    [Column("accepting_bookings")]
    public bool AcceptingBookings { get; private set; }

    /// <summary>
    /// Ghi chú hoặc lý do của tổ chức khi tắt tính năng nhận đặt lịch.
    /// </summary>
    [Column("suspension_reason")]
    [MaxLength(500)]
    public string? SuspensionReason { get; private set; }

    /// <summary>
    /// Thời điểm khởi tạo bản ghi thiết bị trong kho (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm cập nhật thông tin thiết bị gần nhất (UTC).
    /// </summary>
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Concurrency token ánh xạ tới cột hệ thống xmin của PostgreSQL để phát hiện cập nhật đồng thời (stale updates).
    /// </summary>
    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    /// <summary>
    /// Danh sách các lượt đặt lịch gắn liền với thiết bị này.
    /// </summary>
    public ICollection<Reservation> Reservations { get; private set; } = new List<Reservation>();

    /// <summary>
    /// Danh sách các khoảng chặn lịch trên thiết bị này.
    /// </summary>
    public ICollection<CalendarBlock> CalendarBlocks { get; private set; } = new List<CalendarBlock>();

    /// <summary>
    /// Khởi tạo rỗng phục vụ EF Core.
    /// </summary>
    private InventoryEquipment()
    {
    }

    /// <summary>
    /// Khởi tạo bản ghi thiết bị kho mới.
    /// </summary>
    private InventoryEquipment(
        Guid id,
        Guid organizationId,
        CatalogLifecycleStatus catalogLifecycleStatus,
        bool acceptingBookings,
        string? suspensionReason,
        DateTimeOffset now)
    {
        Id = id;
        OrganizationId = organizationId;
        CatalogLifecycleStatus = catalogLifecycleStatus;
        AcceptingBookings = acceptingBookings;
        SuspensionReason = suspensionReason;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Tạo mới một thực thể InventoryEquipment với đầy đủ kiểm tra dữ liệu đầu vào.
    /// </summary>
    /// <param name="id">Equipment ID trùng với Catalog.</param>
    /// <param name="organizationId">ID của tổ chức sở hữu.</param>
    /// <param name="lifecycleStatus">Trạng thái vòng đời từ Catalog.</param>
    /// <param name="acceptingBookings">Có nhận đặt lịch ngay không.</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <param name="suspensionReason">Lý do nếu tắt nhận đặt.</param>
    /// <returns>Result chứa thực thể thiết bị hoặc lỗi validation.</returns>
    public static Result<InventoryEquipment> Create(
        Guid id,
        Guid organizationId,
        CatalogLifecycleStatus lifecycleStatus,
        bool acceptingBookings,
        DateTimeOffset now,
        string? suspensionReason = null)
    {
        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_ID", "Equipment ID không được rỗng.");
        }

        if (organizationId == Guid.Empty)
        {
            return Error.Validation("INVALID_ORGANIZATION_ID", "Organization ID không được rỗng.");
        }

        if (!Enum.IsDefined(lifecycleStatus) || suspensionReason?.Length > 500)
            return Error.Validation("INVALID_EQUIPMENT_METADATA", "Trạng thái hoặc ghi chú không hợp lệ.");

        return new InventoryEquipment(id, organizationId, lifecycleStatus, acceptingBookings, suspensionReason, now);
    }

    /// <summary>
    /// Kiểm tra xem thiết bị có đủ điều kiện nhận lượt đặt lịch mới hay không.
    /// Thiết bị chỉ nhận đặt lịch khi công tắc AcceptingBookings bật và trạng thái Catalog là Active.
    /// </summary>
    /// <returns>True nếu sẵn sàng nhận đặt lịch, ngược lại False.</returns>
    public bool CanAcceptBookings()
    {
        return AcceptingBookings && CatalogLifecycleStatus == CatalogLifecycleStatus.Active;
    }

    /// <summary>
    /// Bật lại tính năng nhận đặt lịch cho thiết bị.
    /// </summary>
    /// <param name="now">Thời điểm thực hiện (UTC).</param>
    /// <returns>Result thành công hoặc lỗi.</returns>
    public Result EnableBookings(DateTimeOffset now)
    {

        AcceptingBookings = true;
        SuspensionReason = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Tắt tính năng nhận đặt lịch cho thiết bị kèm lý do.
    /// </summary>
    /// <param name="reason">Lý do tạm dừng nhận đặt lịch.</param>
    /// <param name="now">Thời điểm thực hiện (UTC).</param>
    /// <returns>Result thành công hoặc lỗi validation nếu thiếu lý do.</returns>
    public Result DisableBookings(string? reason, DateTimeOffset now)
    {
        if (reason?.Trim().Length > 500)
            return Error.Validation("SUSPENSION_REASON_TOO_LONG", "Ghi chú tối đa 500 ký tự.");

        AcceptingBookings = false;
        SuspensionReason = reason?.Trim();
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Đồng bộ trạng thái vòng đời từ Catalog Service.
    /// </summary>
    /// <param name="newStatus">Trạng thái mới từ Catalog.</param>
    /// <param name="now">Thời điểm cập nhật (UTC).</param>
    /// <returns>Result thành công.</returns>
    public Result UpdateCatalogLifecycle(CatalogLifecycleStatus newStatus, DateTimeOffset now)
    {
        if (!Enum.IsDefined(newStatus))
            return Error.Validation("INVALID_CATALOG_STATUS", "Trạng thái Catalog không hợp lệ.");
        CatalogLifecycleStatus = newStatus;

        UpdatedAt = now;
        return Result.Success();
    }
}
