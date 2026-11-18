using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho kho của bên cho thuê (Warehouse).
/// Mỗi người dùng bên cho thuê (OwnerUserId) sở hữu duy nhất một kho lưu trữ thiết bị.
/// </summary>
[Table("warehouses", Schema = "inventory")]
public sealed class Warehouse
{
    public const int MaxAddressDetailLength = 500;
    public const int MaxDivisionCodeLength = 20;
    public const int MaxDivisionNameLength = 200;
    public const double MinLatitude = -90.0;
    public const double MaxLatitude = 90.0;
    public const double MinLongitude = -180.0;
    public const double MaxLongitude = 180.0;

    [Key]
    [Column("owner_user_id")]
    public Guid OwnerUserId { get; private set; }

    [Column("address_detail")]
    [MaxLength(MaxAddressDetailLength)]
    public string? AddressDetail { get; private set; }

    [Column("province_code")]
    [MaxLength(MaxDivisionCodeLength)]
    public string? ProvinceCode { get; private set; }

    [Column("province_name")]
    [MaxLength(MaxDivisionNameLength)]
    public string? ProvinceName { get; private set; }

    [Column("ward_code")]
    [MaxLength(MaxDivisionCodeLength)]
    public string? WardCode { get; private set; }

    [Column("ward_name")]
    [MaxLength(MaxDivisionNameLength)]
    public string? WardName { get; private set; }

    [Column("latitude")]
    public double? Latitude { get; private set; }

    [Column("longitude")]
    public double? Longitude { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    [Column("is_pickup_enabled")]
    public bool IsPickupEnabled { get; private set; } = true;

    [Column("open_time")]
    [MaxLength(10)]
    public string? OpenTime { get; private set; } = "08:00";

    [Column("close_time")]
    [MaxLength(10)]
    public string? CloseTime { get; private set; } = "18:00";

    [Column("closed_days")]
    public int[] ClosedDays { get; private set; } = [];

    public ICollection<EquipmentListing> Listings { get; private set; } = new List<EquipmentListing>();

    public ICollection<WarehouseDeliveryZone> DeliveryZones { get; private set; } = new List<WarehouseDeliveryZone>();

    public bool HasValidAddress =>
        !string.IsNullOrWhiteSpace(AddressDetail) &&
        !string.IsNullOrWhiteSpace(ProvinceCode) &&
        !string.IsNullOrWhiteSpace(ProvinceName) &&
        !string.IsNullOrWhiteSpace(WardCode) &&
        !string.IsNullOrWhiteSpace(WardName);

    private Warehouse()
    {
    }

    private Warehouse(Guid ownerUserId, DateTimeOffset now)
    {
        OwnerUserId = ownerUserId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    private Warehouse(
        Guid ownerUserId,
        string addressDetail,
        string provinceCode,
        string provinceName,
        string wardCode,
        string wardName,
        double? latitude,
        double? longitude,
        DateTimeOffset now)
    {
        OwnerUserId = ownerUserId;
        AddressDetail = addressDetail;
        ProvinceCode = provinceCode;
        ProvinceName = provinceName;
        WardCode = wardCode;
        WardName = wardName;
        Latitude = latitude;
        Longitude = longitude;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Warehouse CreateDraft(Guid ownerUserId, DateTimeOffset now)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống.", nameof(ownerUserId));
        }

        return new Warehouse(ownerUserId, now);
    }

    public static Result<Warehouse> CreateWithAddress(
        Guid ownerUserId,
        string addressDetail,
        string provinceCode,
        string provinceName,
        string wardCode,
        string wardName,
        double? latitude,
        double? longitude,
        DateTimeOffset now)
    {
        if (ownerUserId == Guid.Empty)
        {
            return Error.Validation("INVALID_OWNER_ID", "OwnerUserId không được để trống.");
        }

        var validationResult = ValidateAddressInputs(addressDetail, provinceCode, provinceName, wardCode, wardName, latitude, longitude);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        return new Warehouse(
            ownerUserId,
            addressDetail.Trim(),
            provinceCode.Trim(),
            provinceName.Trim(),
            wardCode.Trim(),
            wardName.Trim(),
            latitude,
            longitude,
            now);
    }

    public Result SetAddress(
        string addressDetail,
        string provinceCode,
        string provinceName,
        string wardCode,
        string wardName,
        double? latitude,
        double? longitude,
        DateTimeOffset now)
    {
        var validationResult = ValidateAddressInputs(addressDetail, provinceCode, provinceName, wardCode, wardName, latitude, longitude);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        AddressDetail = addressDetail.Trim();
        ProvinceCode = provinceCode.Trim();
        ProvinceName = provinceName.Trim();
        WardCode = wardCode.Trim();
        WardName = wardName.Trim();
        Latitude = latitude;
        Longitude = longitude;
        UpdatedAt = now;

        return Result.Success();
    }

    private static Result ValidateAddressInputs(
        string addressDetail,
        string provinceCode,
        string provinceName,
        string wardCode,
        string wardName,
        double? latitude,
        double? longitude)
    {
        if (string.IsNullOrWhiteSpace(addressDetail))
        {
            return Error.Validation("INVALID_ADDRESS_DETAIL", "Địa chỉ chi tiết không được để trống.");
        }

        var trimmedDetail = addressDetail.Trim();
        if (trimmedDetail.Length > MaxAddressDetailLength)
        {
            return Error.Validation("INVALID_ADDRESS_DETAIL", $"Địa chỉ chi tiết không được vượt quá {MaxAddressDetailLength} ký tự.");
        }

        if (string.IsNullOrWhiteSpace(provinceCode) || provinceCode.Trim().Length > MaxDivisionCodeLength)
        {
            return Error.Validation("INVALID_PROVINCE_CODE", "Mã tỉnh/thành phố không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(provinceName) || provinceName.Trim().Length > MaxDivisionNameLength)
        {
            return Error.Validation("INVALID_PROVINCE_NAME", "Tên tỉnh/thành phố không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(wardCode) || wardCode.Trim().Length > MaxDivisionCodeLength)
        {
            return Error.Validation("INVALID_WARD_CODE", "Mã phường/xã không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(wardName) || wardName.Trim().Length > MaxDivisionNameLength)
        {
            return Error.Validation("INVALID_WARD_NAME", "Tên phường/xã không hợp lệ.");
        }

        if (latitude.HasValue != longitude.HasValue)
        {
            return Error.Validation("INVALID_COORDINATES", "Vui lòng nhập đủ cặp tọa độ.");
        }

        if (latitude is double lat && (!double.IsFinite(lat) || lat < MinLatitude || lat > MaxLatitude))
        {
            return Error.Validation("INVALID_COORDINATES", $"Vĩ độ (latitude) phải nằm trong khoảng [{MinLatitude}, {MaxLatitude}].");
        }

        if (longitude is double lon && (!double.IsFinite(lon) || lon < MinLongitude || lon > MaxLongitude))
        {
            return Error.Validation("INVALID_COORDINATES", $"Kinh độ (longitude) phải nằm trong khoảng [{MinLongitude}, {MaxLongitude}].");
        }

        return Result.Success();
    }

    public Result SetDeliveryPolicy(
        bool isPickupEnabled,
        string? openTime,
        string? closeTime,
        int[]? closedDays,
        DateTimeOffset now)
    {
        if (isPickupEnabled && !HasValidAddress)
        {
            return Error.Validation("PICKUP_REQUIRES_VALID_ADDRESS", "Cần cấu hình địa chỉ kho hợp lệ trước khi bật nhận tại kho.");
        }

        string normalizedOpen = "08:00";
        string normalizedClose = "18:00";

        if (!string.IsNullOrWhiteSpace(openTime) || !string.IsNullOrWhiteSpace(closeTime))
        {
            if (string.IsNullOrWhiteSpace(openTime) || !TimeOnly.TryParseExact(openTime.Trim(), "HH:mm", out var parsedOpen))
            {
                return Error.Validation("INVALID_OPEN_TIME", "Giờ mở cửa phải theo định dạng HH:mm (ví dụ 08:00).");
            }

            if (string.IsNullOrWhiteSpace(closeTime) || !TimeOnly.TryParseExact(closeTime.Trim(), "HH:mm", out var parsedClose))
            {
                return Error.Validation("INVALID_CLOSE_TIME", "Giờ đóng cửa phải theo định dạng HH:mm (ví dụ 18:00).");
            }

            if (parsedOpen >= parsedClose)
            {
                return Error.Validation("INVALID_OPERATING_HOURS", "Giờ mở cửa phải trước giờ đóng cửa.");
            }

            normalizedOpen = openTime.Trim();
            normalizedClose = closeTime.Trim();
        }

        int[] normalizedClosedDays = [];
        if (closedDays is not null && closedDays.Length > 0)
        {
            if (closedDays.Any(d => d < 0 || d > 6))
            {
                return Error.Validation("INVALID_CLOSED_DAYS", "Ngày nghỉ phải nằm trong khoảng 0 đến 6 (0: Chủ nhật, 6: Thứ bảy).");
            }

            if (closedDays.Distinct().Count() != closedDays.Length)
            {
                return Error.Validation("INVALID_CLOSED_DAYS", "Ngày nghỉ không được trùng lặp.");
            }

            normalizedClosedDays = closedDays.OrderBy(d => d).ToArray();
        }

        IsPickupEnabled = isPickupEnabled;
        OpenTime = normalizedOpen;
        CloseTime = normalizedClose;
        ClosedDays = normalizedClosedDays;
        UpdatedAt = now;

        return Result.Success();
    }
}
