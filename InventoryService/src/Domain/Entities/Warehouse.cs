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

    public ICollection<EquipmentListing> Listings { get; private set; } = new List<EquipmentListing>();

    public bool HasValidAddress =>
        !string.IsNullOrWhiteSpace(AddressDetail) &&
        !string.IsNullOrWhiteSpace(ProvinceCode) &&
        !string.IsNullOrWhiteSpace(ProvinceName) &&
        !string.IsNullOrWhiteSpace(WardCode) &&
        !string.IsNullOrWhiteSpace(WardName) &&
        Latitude.HasValue &&
        Longitude.HasValue &&
        Latitude.Value >= MinLatitude && Latitude.Value <= MaxLatitude &&
        Longitude.Value >= MinLongitude && Longitude.Value <= MaxLongitude;

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
        double latitude,
        double longitude,
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
        double latitude,
        double longitude,
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
        double latitude,
        double longitude,
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
        double latitude,
        double longitude)
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

        if (double.IsNaN(latitude) || double.IsInfinity(latitude) || latitude < MinLatitude || latitude > MaxLatitude)
        {
            return Error.Validation("INVALID_COORDINATES", $"Vĩ độ (latitude) phải nằm trong khoảng [{MinLatitude}, {MaxLatitude}].");
        }

        if (double.IsNaN(longitude) || double.IsInfinity(longitude) || longitude < MinLongitude || longitude > MaxLongitude)
        {
            return Error.Validation("INVALID_COORDINATES", $"Kinh độ (longitude) phải nằm trong khoảng [{MinLongitude}, {MaxLongitude}].");
        }

        return Result.Success();
    }
}
