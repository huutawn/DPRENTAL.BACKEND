using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Utils;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho đơn vị thiết bị vật lý (Physical Inventory Asset) trong kho.
/// Thuộc về một EquipmentListing và chỉ lưu thông tin vật lý (AssetCode, SerialNumber, ManufactureYear, trạng thái booking).
/// </summary>
[Table("assets", Schema = "inventory")]
public sealed class InventoryAsset
{
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Column("equipment_listing_id")]
    public Guid EquipmentListingId { get; private set; }

    [Column("asset_code")]
    [MaxLength(50)]
    public string AssetCode { get; private set; } = string.Empty;

    [Column("serial_number")]
    [MaxLength(100)]
    public string SerialNumber { get; private set; } = string.Empty;

    [Column("manufacture_year")]
    public int? ManufactureYear { get; private set; }

    [Column("accepting_bookings")]
    public bool AcceptingBookings { get; private set; }

    [Column("suspension_reason")]
    [MaxLength(500)]
    public string? SuspensionReason { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    public EquipmentListing Listing { get; private set; } = null!;

    public ICollection<AssetAllocation> Allocations { get; private set; } = new List<AssetAllocation>();

    public ICollection<CalendarBlock> CalendarBlocks { get; private set; } = new List<CalendarBlock>();

    private InventoryAsset()
    {
    }

    private InventoryAsset(
        Guid id,
        Guid equipmentListingId,
        string assetCode,
        string serialNumber,
        int? manufactureYear,
        bool acceptingBookings,
        string? suspensionReason,
        DateTimeOffset now)
    {
        Id = id;
        EquipmentListingId = equipmentListingId;
        AssetCode = assetCode;
        SerialNumber = serialNumber;
        ManufactureYear = manufactureYear;
        AcceptingBookings = acceptingBookings;
        SuspensionReason = suspensionReason;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Tạo mới một Physical Inventory Asset thuộc về một EquipmentListing.
    /// AssetCode được sinh tự động theo định dạng AST-{AssetId:N} và bất biến.
    /// </summary>
    public static Result<InventoryAsset> Create(
        Guid id,
        Guid equipmentListingId,
        string serialNumber,
        DateTimeOffset now,
        int? manufactureYear = null)
    {
        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng.");
        }

        if (equipmentListingId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_LISTING_ID", "Equipment Listing ID không được rỗng.");
        }

        var validation = ValidatePhysicalDetails(serialNumber, manufactureYear, now);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var (validSerial, validYear) = validation.Value;
        string assetCode = $"AST-{id:N}";

        return new InventoryAsset(
            id,
            equipmentListingId,
            assetCode,
            validSerial,
            validYear,
            acceptingBookings: true,
            suspensionReason: null,
            now);
    }

    /// <summary>
    /// Cập nhật thông tin vật lý của đơn vị thiết bị (serial number, năm sản xuất).
    /// </summary>
    public Result UpdatePhysicalDetails(
        string serialNumber,
        int? manufactureYear,
        DateTimeOffset now)
    {
        var validation = ValidatePhysicalDetails(serialNumber, manufactureYear, now);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var (validSerial, validYear) = validation.Value;
        SerialNumber = validSerial;
        ManufactureYear = validYear;
        UpdatedAt = now;

        return Result.Success();
    }

    public static Result<(string SerialNumber, int? ManufactureYear)> ValidatePhysicalDetails(
        string serialNumber,
        int? manufactureYear,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial là bắt buộc và không được để trống.");
        }

        var normalizedSerial = serialNumber.Trim();
        if (normalizedSerial.Length > 100)
        {
            return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial tối đa 100 ký tự.");
        }

        if (manufactureYear.HasValue)
        {
            var maxYear = BusinessDateTime.ToVietnamDate(now).Year;
            if (manufactureYear.Value < 1900 || manufactureYear.Value > maxYear)
            {
                return Error.Validation("INVALID_MANUFACTURE_YEAR", $"Năm sản xuất phải từ 1900 đến {maxYear}.");
            }
        }

        return Result.Success((normalizedSerial, manufactureYear));
    }

    public bool CanAcceptBookings() => AcceptingBookings;

    public Result EnableBookings(DateTimeOffset now)
    {
        AcceptingBookings = true;
        SuspensionReason = null;
        UpdatedAt = now;
        return Result.Success();
    }

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
