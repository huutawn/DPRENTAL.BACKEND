using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho một khoảng thời gian chặn lịch sử dụng tài sản (Calendar Block).
/// Phục vụ các mục đích bảo trì, sử dụng nội bộ hoặc kiểm định chất lượng, ngăn không cho đặt lịch.
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
    /// Định danh tài sản trong kho bị chặn lịch.
    /// </summary>
    [Column("asset_id")]
    public Guid AssetId { get; private set; }

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
    /// Bản ghi tài sản liên kết với khoảng chặn lịch này.
    /// </summary>
    [ForeignKey(nameof(AssetId))]
    public InventoryAsset Asset { get; private set; } = null!;

    private CalendarBlock()
    {
    }

    private CalendarBlock(
        Guid id,
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CalendarBlockReason reason,
        string createdBy,
        DateTimeOffset now,
        string? notes)
    {
        Id = id;
        AssetId = assetId;
        StartDate = startDate;
        EndDate = endDate;
        Reason = reason;
        CreatedBy = createdBy;
        CreatedAt = now;
        Notes = notes;
    }

    /// <summary>
    /// Tạo mới một khoảng chặn lịch cho tài sản với kiểm tra tính hợp lệ về ngày và chân trời nghiệp vụ.
    /// Lưu ý: Không áp dụng giới hạn thời lượng thuê lên khoảng chặn lịch, nhưng tối đa không quá chân trời 6 tháng.
    /// </summary>
    public static Result<CalendarBlock> Create(
        Guid id,
        Guid assetId,
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

        if (assetId == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng.");
        }

        if (string.IsNullOrWhiteSpace(createdBy))
        {
            return Error.Validation("CREATED_BY_REQUIRED", "Cần cung cấp thông tin người tạo chặn lịch.");
        }

        if (!Enum.IsDefined(reason))
        {
            return Error.Validation("INVALID_BLOCK_REASON", "Lý do chặn lịch không hợp lệ.");
        }

        if (notes is not null && notes.Length > 500)
        {
            return Error.Validation("INVALID_BLOCK_NOTES", "Ghi chú chặn lịch không được vượt quá 500 ký tự.");
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
            assetId: assetId,
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
    public bool Overlaps(DateOnly start, DateOnly end)
    {
        return StartDate <= end && EndDate >= start;
    }
}
