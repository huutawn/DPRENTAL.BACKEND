using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho tài sản trong kho Inventory, thuộc sở hữu của một cá nhân (OwnerUserId)
/// và liên kết với loại thiết bị từ Catalog (EquipmentTypeId).
/// </summary>
[Table("assets", Schema = "inventory")]
public sealed class InventoryAsset
{
    /// <summary>
    /// Định danh duy nhất của tài sản trong kho.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Định danh của người dùng sở hữu tài sản (từ sub của JWT).
    /// </summary>
    [Column("owner_user_id")]
    public Guid OwnerUserId { get; private set; }

    /// <summary>
    /// Định danh của tổ chức mà tài sản được liên kết (metadata).
    /// </summary>
    [Column("organization_id")]
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Định danh loại thiết bị trong Catalog Service (EquipmentTypeId).
    /// </summary>
    [Column("equipment_type_id")]
    public Guid EquipmentTypeId { get; private set; }

    /// <summary>
    /// Phiên bản schema thuộc tính động từ Catalog Service (số nguyên dương).
    /// </summary>
    [Column("schema_version")]
    public int SchemaVersion { get; private set; }

    /// <summary>
    /// Giá trị các thông số kỹ thuật dạng JSON object lưu trữ kiểu JSONB trong PostgreSQL.
    /// </summary>
    [Column("spec_values", TypeName = "jsonb")]
    public string SpecValues { get; private set; } = "{}";

    /// <summary>
    /// Cờ xác định tài sản có đang cho phép nhận đặt lịch (bookings/allocations) hay không.
    /// </summary>
    [Column("accepting_bookings")]
    public bool AcceptingBookings { get; private set; }

    /// <summary>
    /// Ghi chú hoặc lý do tạm dừng nhận đặt lịch.
    /// </summary>
    [Column("suspension_reason")]
    [MaxLength(500)]
    public string? SuspensionReason { get; private set; }

    /// <summary>
    /// Thời điểm tạo bản ghi tài sản (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm cập nhật thông tin tài sản gần nhất (UTC).
    /// </summary>
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Concurrency token ánh xạ tới cột xmin của PostgreSQL để phát hiện cập nhật đồng thời.
    /// </summary>
    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    /// <summary>
    /// Danh sách các lượt phân bổ lịch trên tài sản này.
    /// </summary>
    public ICollection<AssetAllocation> Allocations { get; private set; } = new List<AssetAllocation>();

    /// <summary>
    /// Danh sách các khoảng chặn lịch trên tài sản này.
    /// </summary>
    public ICollection<CalendarBlock> CalendarBlocks { get; private set; } = new List<CalendarBlock>();

    /// <summary>
    /// Khởi tạo rỗng phục vụ EF Core.
    /// </summary>
    private InventoryAsset()
    {
    }

    private InventoryAsset(
        Guid id,
        Guid ownerUserId,
        Guid organizationId,
        Guid equipmentTypeId,
        int schemaVersion,
        string specValues,
        bool acceptingBookings,
        string? suspensionReason,
        DateTimeOffset now)
    {
        Id = id;
        OwnerUserId = ownerUserId;
        OrganizationId = organizationId;
        EquipmentTypeId = equipmentTypeId;
        SchemaVersion = schemaVersion;
        SpecValues = specValues;
        AcceptingBookings = acceptingBookings;
        SuspensionReason = suspensionReason;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Tạo mới một thực thể InventoryAsset với đầy đủ kiểm tra tính hợp lệ dữ liệu.
    /// </summary>
    public static Result<InventoryAsset> Create(
        Guid id,
        Guid ownerUserId,
        Guid organizationId,
        Guid equipmentTypeId,
        int schemaVersion,
        string specValues,
        bool acceptingBookings,
        DateTimeOffset now,
        string? suspensionReason = null)
    {
        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng.");
        }

        if (ownerUserId == Guid.Empty)
        {
            return Error.Validation("INVALID_OWNER_USER_ID", "Owner User ID không được rỗng.");
        }

        if (organizationId == Guid.Empty)
        {
            return Error.Validation("INVALID_ORGANIZATION_ID", "Organization ID không được rỗng.");
        }

        if (equipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }

        if (schemaVersion < 1)
        {
            return Error.Validation("INVALID_SCHEMA_VERSION", "Schema Version phải là số nguyên dương lớn hơn hoặc bằng 1.");
        }

        if (string.IsNullOrWhiteSpace(specValues))
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values không được để trống.");
        }

        try
        {
            using var doc = JsonDocument.Parse(specValues);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Error.Validation("INVALID_SPEC_VALUES", "Spec values phải là JSON object.");
            }
        }
        catch (JsonException)
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values không đúng định dạng JSON.");
        }

        if (suspensionReason?.Length > 500)
        {
            return Error.Validation("SUSPENSION_REASON_TOO_LONG", "Lý do tạm dừng tối đa 500 ký tự.");
        }

        return new InventoryAsset(
            id,
            ownerUserId,
            organizationId,
            equipmentTypeId,
            schemaVersion,
            specValues,
            acceptingBookings,
            suspensionReason?.Trim(),
            now);
    }

    /// <summary>
    /// Kiểm tra xem tài sản có đủ điều kiện nhận lịch phân bổ mới hay không.
    /// </summary>
    public bool CanAcceptBookings() => AcceptingBookings;

    /// <summary>
    /// Bật lại tính năng nhận đặt lịch cho tài sản.
    /// </summary>
    public Result EnableBookings(DateTimeOffset now)
    {
        AcceptingBookings = true;
        SuspensionReason = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Tắt tính năng nhận đặt lịch cho tài sản kèm lý do.
    /// </summary>
    public Result DisableBookings(string? reason, DateTimeOffset now)
    {
        if (reason?.Trim().Length > 500)
        {
            return Error.Validation("SUSPENSION_REASON_TOO_LONG", "Ghi chú tối đa 500 ký tự.");
        }

        AcceptingBookings = false;
        SuspensionReason = reason?.Trim();
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Cập nhật thông số kỹ thuật và phiên bản schema từ Catalog Service.
    /// </summary>
    public Result UpdateSpecs(int newSchemaVersion, string newSpecValues, DateTimeOffset now)
    {
        if (newSchemaVersion < 1)
        {
            return Error.Validation("INVALID_SCHEMA_VERSION", "Schema Version phải là số nguyên dương.");
        }

        if (string.IsNullOrWhiteSpace(newSpecValues))
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values không được để trống.");
        }

        try
        {
            using var doc = JsonDocument.Parse(newSpecValues);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Error.Validation("INVALID_SPEC_VALUES", "Spec values phải là JSON object.");
            }
        }
        catch (JsonException)
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values không đúng định dạng JSON.");
        }

        SchemaVersion = newSchemaVersion;
        SpecValues = newSpecValues;
        UpdatedAt = now;
        return Result.Success();
    }
}
