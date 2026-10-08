using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho một danh mục niêm yết thiết bị (EquipmentListing) trong kho Inventory.
/// Sở hữu dữ liệu chung (Model, Brand, SpecValues, Thumbnail), giá niêm yết và trạng thái vòng đời.
/// Quản lý danh sách các đơn vị vật lý (InventoryAsset).
/// </summary>
[Table("equipment_listings", Schema = "inventory")]
public sealed class EquipmentListing
{
    public const string DefaultCurrency = "VND";
    public const decimal MinRate = 1m;
    public const decimal MaxRate = 999_999_999_999_999_999m;

    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Column("owner_user_id")]
    public Guid OwnerUserId { get; private set; }

    [Column("equipment_type_id")]
    public Guid EquipmentTypeId { get; private set; }

    [Column("brand_id")]
    public Guid BrandId { get; private set; }

    [Column("model")]
    [MaxLength(200)]
    public string Model { get; private set; } = string.Empty;

    [Column("schema_version")]
    public int SchemaVersion { get; private set; }

    [Column("spec_values", TypeName = "jsonb")]
    public string SpecValues { get; private set; } = "{}";

    [Column("thumbnail_media_id")]
    public Guid? ThumbnailMediaId { get; private set; }

    [Column("daily_rate", TypeName = "numeric(18,0)")]
    public decimal DailyRate { get; private set; }

    [Column("weekly_rate", TypeName = "numeric(18,0)")]
    public decimal? WeeklyRate { get; private set; }

    [Column("monthly_rate", TypeName = "numeric(18,0)")]
    public decimal? MonthlyRate { get; private set; }

    [Column("currency")]
    [MaxLength(10)]
    public string Currency { get; private set; } = DefaultCurrency;

    [Column("status")]
    public EquipmentListingStatus Status { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    public ICollection<InventoryAsset> Assets { get; private set; } = new List<InventoryAsset>();

    private EquipmentListing()
    {
    }

    private EquipmentListing(
        Guid id,
        Guid ownerUserId,
        Guid equipmentTypeId,
        Guid brandId,
        string model,
        int schemaVersion,
        string specValues,
        Guid? thumbnailMediaId,
        decimal dailyRate,
        decimal? weeklyRate,
        decimal? monthlyRate,
        string currency,
        EquipmentListingStatus status,
        DateTimeOffset now)
    {
        Id = id;
        OwnerUserId = ownerUserId;
        EquipmentTypeId = equipmentTypeId;
        BrandId = brandId;
        Model = model;
        SchemaVersion = schemaVersion;
        SpecValues = specValues;
        ThumbnailMediaId = thumbnailMediaId;
        DailyRate = dailyRate;
        WeeklyRate = weeklyRate;
        MonthlyRate = monthlyRate;
        Currency = currency;
        Status = status;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Tạo mới một EquipmentListing với trạng thái ban đầu luôn là Draft.
    /// Brand là bắt buộc.
    /// </summary>
    public static Result<EquipmentListing> Create(
        Guid id,
        Guid ownerUserId,
        Guid equipmentTypeId,
        Guid brandId,
        string model,
        int schemaVersion,
        string specValues,
        decimal dailyRate,
        decimal? weeklyRate,
        decimal? monthlyRate,
        string currency,
        DateTimeOffset now,
        Guid? thumbnailMediaId = null)
    {
        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_LISTING_ID", "Listing ID không được rỗng.");
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

        var rateValidation = ValidateRateValues(dailyRate, weeklyRate, monthlyRate, currency);
        if (rateValidation.IsFailure)
        {
            return rateValidation.Error;
        }

        var detailsValidation = ValidateDetails(model, thumbnailMediaId, schemaVersion, specValues);
        if (detailsValidation.IsFailure)
        {
            return detailsValidation.Error;
        }

        var (validModel, validThumb, validSchema, validSpecs) = detailsValidation.Value;

        return new EquipmentListing(
            id,
            ownerUserId,
            equipmentTypeId,
            brandId,
            validModel,
            validSchema,
            validSpecs,
            validThumb,
            dailyRate,
            weeklyRate,
            monthlyRate,
            currency,
            EquipmentListingStatus.Draft,
            now);
    }

    /// <summary>
    /// Cập nhật thông tin chi tiết và thông số kỹ thuật của Listing.
    /// </summary>
    public Result UpdateDetails(
        Guid? brandId,
        string model,
        Guid? thumbnailMediaId,
        int schemaVersion,
        string specValues,
        DateTimeOffset now)
    {
        if (brandId.HasValue && brandId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được rỗng.");
        }

        var validation = ValidateDetails(model, thumbnailMediaId, schemaVersion, specValues);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var (validModel, validThumb, validSchema, validSpecs) = validation.Value;

        if (brandId.HasValue)
        {
            BrandId = brandId.Value;
        }

        Model = validModel;
        ThumbnailMediaId = validThumb;
        SchemaVersion = validSchema;
        SpecValues = validSpecs;
        UpdatedAt = now;

        return Result.Success();
    }

    /// <summary>
    /// Cập nhật giá thuê niêm yết của Listing (thay thế toàn bộ).
    /// </summary>
    public Result UpdateRentalRate(
        decimal dailyRate,
        decimal? weeklyRate,
        decimal? monthlyRate,
        string currency,
        DateTimeOffset now)
    {
        var validationResult = ValidateRateValues(dailyRate, weeklyRate, monthlyRate, currency);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        DailyRate = dailyRate;
        WeeklyRate = weeklyRate;
        MonthlyRate = monthlyRate;
        Currency = currency;
        UpdatedAt = now;

        return Result.Success();
    }

    /// <summary>
    /// Cập nhật trạng thái của Listing.
    /// Chuyển sang Active yêu cầu Listing có ít nhất 1 unit đang nhận booking (AcceptingBookings == true).
    /// </summary>
    public Result UpdateStatus(
        EquipmentListingStatus newStatus,
        bool hasUnitAcceptingBookings,
        DateTimeOffset now)
    {
        if (Status == newStatus)
        {
            return Result.Success();
        }

        if (newStatus == EquipmentListingStatus.Active && !hasUnitAcceptingBookings)
        {
            return Error.Validation("LISTING_NEEDS_ACTIVE_UNIT", "Listing cần ít nhất một thiết bị nhận đặt lịch để chuyển sang trạng thái Active.");
        }

        Status = newStatus;
        UpdatedAt = now;
        return Result.Success();
    }

    public static Result<(string Model, Guid? ThumbnailMediaId, int SchemaVersion, string SpecValues)> ValidateDetails(
        string model,
        Guid? thumbnailMediaId,
        int schemaVersion,
        string specValues)
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

        return Result.Success((trimmedModel, thumbnailMediaId, schemaVersion, specValues));
    }

    public static Result ValidateRateValues(
        decimal dailyRate,
        decimal? weeklyRate,
        decimal? monthlyRate,
        string currency)
    {
        if (currency != DefaultCurrency)
        {
            return Error.Validation("INVALID_CURRENCY", $"Đơn vị tiền tệ bắt buộc phải là '{DefaultCurrency}'.");
        }

        if (decimal.Truncate(dailyRate) != dailyRate || dailyRate < MinRate || dailyRate > MaxRate)
        {
            return Error.Validation("INVALID_DAILY_RATE", $"Giá thuê theo ngày phải là số nguyên từ {MinRate} đến {MaxRate}.");
        }

        if (weeklyRate.HasValue)
        {
            if (decimal.Truncate(weeklyRate.Value) != weeklyRate.Value || weeklyRate.Value < MinRate || weeklyRate.Value > MaxRate)
            {
                return Error.Validation("INVALID_WEEKLY_RATE", $"Giá thuê theo tuần phải là số nguyên từ {MinRate} đến {MaxRate}.");
            }
        }

        if (monthlyRate.HasValue)
        {
            if (decimal.Truncate(monthlyRate.Value) != monthlyRate.Value || monthlyRate.Value < MinRate || monthlyRate.Value > MaxRate)
            {
                return Error.Validation("INVALID_MONTHLY_RATE", $"Giá thuê theo tháng phải là số nguyên từ {MinRate} đến {MaxRate}.");
            }
        }

        return Result.Success();
    }
}
