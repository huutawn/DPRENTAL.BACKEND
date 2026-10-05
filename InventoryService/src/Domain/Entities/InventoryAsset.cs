using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Utils;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho tài sản trong kho Inventory, thuộc sở hữu của một cá nhân (OwnerUserId)
/// và liên kết với loại thiết bị từ Catalog (EquipmentTypeId) cùng thương hiệu chuẩn (BrandId).
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
    /// Định danh thương hiệu từ Catalog Service (BrandId, giai đoạn chuyển tiếp nullable).
    /// </summary>
    [Column("brand_id")]
    public Guid? BrandId { get; private set; }

    /// <summary>
    /// Tên model hoặc định danh thương mại của thiết bị.
    /// </summary>
    [Column("model")]
    [MaxLength(200)]
    public string Model { get; private set; } = string.Empty;

    /// <summary>
    /// Số serial của thiết bị (tùy chọn).
    /// </summary>
    [Column("serial_number")]
    [MaxLength(100)]
    public string? SerialNumber { get; private set; }

    /// <summary>
    /// Năm sản xuất của thiết bị (1900 đến năm hiện tại tại Việt Nam).
    /// </summary>
    [Column("manufacture_year")]
    public int? ManufactureYear { get; private set; }

    /// <summary>
    /// ID tham chiếu media ảnh đại diện đã được công bố của thiết bị.
    /// </summary>
    [Column("thumbnail_media_id")]
    public Guid? ThumbnailMediaId { get; private set; }

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
        Guid equipmentTypeId,
        Guid? brandId,
        string model,
        string? serialNumber,
        int? manufactureYear,
        Guid? thumbnailMediaId,
        int schemaVersion,
        string specValues,
        bool acceptingBookings,
        string? suspensionReason,
        DateTimeOffset now)
    {
        Id = id;
        OwnerUserId = ownerUserId;
        EquipmentTypeId = equipmentTypeId;
        BrandId = brandId;
        Model = model;
        SerialNumber = serialNumber;
        ManufactureYear = manufactureYear;
        ThumbnailMediaId = thumbnailMediaId;
        SchemaVersion = schemaVersion;
        SpecValues = specValues;
        AcceptingBookings = acceptingBookings;
        SuspensionReason = suspensionReason;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Tạo mới một thực thể InventoryAsset với đầy đủ kiểm tra tính hợp lệ dữ liệu.
    /// Create nhận Brand bắt buộc, từ chối missing/null/GUID rỗng.
    /// Không thêm overload hoặc default Brand để duy trì create không hợp lệ.
    /// </summary>
    public static Result<InventoryAsset> Create(
        Guid id,
        Guid ownerUserId,
        Guid equipmentTypeId,
        Guid brandId,
        string model,
        int schemaVersion,
        string specValues,
        DateTimeOffset now,
        string? serialNumber = null,
        int? manufactureYear = null,
        Guid? thumbnailMediaId = null)
    {
        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng.");
        }

        if (ownerUserId == Guid.Empty)
        {
            return Error.Validation("INVALID_OWNER_USER_ID", "Owner User ID không được rỗng.");
        }

        if (equipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }

        if (brandId == Guid.Empty)
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được rỗng.");
        }

        var validation = ValidateDetails(model, serialNumber, manufactureYear, thumbnailMediaId, schemaVersion, specValues, now);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var (validModel, validSerial, validYear, validThumb, validSchema, validSpecs) = validation.Value;

        return new InventoryAsset(
            id,
            ownerUserId,
            equipmentTypeId,
            brandId,
            validModel,
            validSerial,
            validYear,
            validThumb,
            validSchema,
            validSpecs,
            acceptingBookings: true,
            suspensionReason: null,
            now);
    }

    /// <summary>
    /// Cập nhật thông tin chi tiết và thông số kỹ thuật của tài sản.
    /// Domain ngăn chuyển Brand đã có thành null.
    /// </summary>
    public Result UpdateDetails(
        Guid? brandId,
        string model,
        string? serialNumber,
        int? manufactureYear,
        Guid? thumbnailMediaId,
        int schemaVersion,
        string specValues,
        DateTimeOffset now)
    {
        if (BrandId.HasValue && !brandId.HasValue)
        {
            return Error.Validation("CANNOT_REMOVE_BRAND", "Không thể xóa Brand của tài sản đã có Brand.");
        }

        if (brandId.HasValue && brandId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được rỗng.");
        }

        var validation = ValidateDetails(model, serialNumber, manufactureYear, thumbnailMediaId, schemaVersion, specValues, now);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var (validModel, validSerial, validYear, validThumb, validSchema, validSpecs) = validation.Value;

        if (brandId.HasValue)
        {
            BrandId = brandId.Value;
        }

        Model = validModel;
        SerialNumber = validSerial;
        ManufactureYear = validYear;
        ThumbnailMediaId = validThumb;
        SchemaVersion = validSchema;
        SpecValues = validSpecs;
        UpdatedAt = now;

        return Result.Success();
    }

    public static Result<(string Model, string? SerialNumber, int? ManufactureYear, Guid? ThumbnailMediaId, int SchemaVersion, string SpecValues)> ValidateDetails(
        string model,
        string? serialNumber,
        int? manufactureYear,
        Guid? thumbnailMediaId,
        int schemaVersion,
        string specValues,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Error.Validation("INVALID_MODEL", "Model không được để trống.");
        }

        var trimmedModel = model.Trim();
        if (trimmedModel.Length > 200)
        {
            return Error.Validation("INVALID_MODEL", "Model tối đa 200 ký tự.");
        }

        string? normalizedSerial = null;
        if (!string.IsNullOrWhiteSpace(serialNumber))
        {
            normalizedSerial = serialNumber.Trim();
            if (normalizedSerial.Length > 100)
            {
                return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial tối đa 100 ký tự.");
            }
        }

        if (manufactureYear.HasValue)
        {
            var maxYear = BusinessDateTime.ToVietnamDate(now).Year;
            if (manufactureYear.Value < 1900 || manufactureYear.Value > maxYear)
            {
                return Error.Validation("INVALID_MANUFACTURE_YEAR", $"Năm sản xuất phải từ 1900 đến {maxYear}.");
            }
        }

        if (thumbnailMediaId.HasValue && thumbnailMediaId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_THUMBNAIL_MEDIA_ID", "Thumbnail Media ID không được rỗng.");
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

        return Result.Success((trimmedModel, normalizedSerial, manufactureYear, thumbnailMediaId, schemaVersion, specValues));
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
            return Error.Validation("SUSPENSION_REASON_TOO_LONG", "Lý do tạm dừng tối đa 500 ký tự.");
        }

        AcceptingBookings = false;
        SuspensionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        UpdatedAt = now;
        return Result.Success();
    }
}
