using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể cấu hình vùng giao hàng và phí vận chuyển của kho.
/// Mỗi vùng được định danh bởi cặp (ProvinceCode, WardCode) thuộc một OwnerUserId.
/// </summary>
[Table("warehouse_delivery_zones", Schema = "inventory")]
public sealed class WarehouseDeliveryZone
{
    public const int MaxDivisionCodeLength = 20;

    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Column("owner_user_id")]
    public Guid OwnerUserId { get; private set; }

    [Column("province_code")]
    [MaxLength(MaxDivisionCodeLength)]
    public string ProvinceCode { get; private set; } = string.Empty;

    [Column("ward_code")]
    [MaxLength(MaxDivisionCodeLength)]
    public string? WardCode { get; private set; }

    [Column("delivery_fee_vnd")]
    public decimal DeliveryFeeVnd { get; private set; }

    [Column("return_fee_vnd")]
    public decimal ReturnFeeVnd { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    public Warehouse? Warehouse { get; private set; }

    private WarehouseDeliveryZone()
    {
    }

    private WarehouseDeliveryZone(
        Guid id,
        Guid ownerUserId,
        string provinceCode,
        string? wardCode,
        decimal deliveryFeeVnd,
        decimal returnFeeVnd,
        DateTimeOffset now)
    {
        Id = id;
        OwnerUserId = ownerUserId;
        ProvinceCode = provinceCode;
        WardCode = wardCode;
        DeliveryFeeVnd = deliveryFeeVnd;
        ReturnFeeVnd = returnFeeVnd;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Result<WarehouseDeliveryZone> Create(
        Guid ownerUserId,
        string provinceCode,
        string? wardCode,
        decimal deliveryFeeVnd,
        decimal returnFeeVnd,
        DateTimeOffset now)
    {
        if (ownerUserId == Guid.Empty)
        {
            return Error.Validation("INVALID_OWNER_ID", "OwnerUserId không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(provinceCode) || provinceCode.Trim().Length > MaxDivisionCodeLength)
        {
            return Error.Validation("INVALID_PROVINCE_CODE", "Mã tỉnh/thành phố không hợp lệ.");
        }

        string trimmedProvince = provinceCode.Trim();
        string? trimmedWard = string.IsNullOrWhiteSpace(wardCode) ? null : wardCode.Trim();
        if (trimmedWard is not null && trimmedWard.Length > MaxDivisionCodeLength)
        {
            return Error.Validation("INVALID_WARD_CODE", "Mã phường/xã không hợp lệ.");
        }

        if (deliveryFeeVnd < 0 || decimal.Truncate(deliveryFeeVnd) != deliveryFeeVnd)
        {
            return Error.Validation("INVALID_DELIVERY_FEE", "Phí giao hàng phải là số nguyên VND >= 0.");
        }

        if (returnFeeVnd < 0 || decimal.Truncate(returnFeeVnd) != returnFeeVnd)
        {
            return Error.Validation("INVALID_RETURN_FEE", "Phí thu hồi phải là số nguyên VND >= 0.");
        }

        return new WarehouseDeliveryZone(
            Guid.NewGuid(),
            ownerUserId,
            trimmedProvince,
            trimmedWard,
            deliveryFeeVnd,
            returnFeeVnd,
            now);
    }

    public Result UpdateFees(decimal deliveryFeeVnd, decimal returnFeeVnd, DateTimeOffset now)
    {
        if (deliveryFeeVnd < 0 || decimal.Truncate(deliveryFeeVnd) != deliveryFeeVnd)
        {
            return Error.Validation("INVALID_DELIVERY_FEE", "Phí giao hàng phải là số nguyên VND >= 0.");
        }

        if (returnFeeVnd < 0 || decimal.Truncate(returnFeeVnd) != returnFeeVnd)
        {
            return Error.Validation("INVALID_RETURN_FEE", "Phí thu hồi phải là số nguyên VND >= 0.");
        }

        DeliveryFeeVnd = deliveryFeeVnd;
        ReturnFeeVnd = returnFeeVnd;
        UpdatedAt = now;
        return Result.Success();
    }
}
