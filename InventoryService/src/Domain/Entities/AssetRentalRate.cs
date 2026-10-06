using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể biểu diễn giá thuê niêm yết của tài sản trong kho (InventoryAsset).
/// Quan hệ 1:1 với InventoryAsset qua AssetId.
/// </summary>
[Table("asset_rental_rates", Schema = "inventory")]
public sealed class AssetRentalRate
{
    public const string DefaultCurrency = "VND";
    public const decimal MinRate = 1m;
    public const decimal MaxRate = 999_999_999_999_999_999m;

    /// <summary>
    /// Định danh tài sản trong kho, vừa là Primary Key vừa là Foreign Key tới InventoryAsset.
    /// </summary>
    [Key]
    [Column("asset_id")]
    public Guid AssetId { get; private set; }

    /// <summary>
    /// Giá thuê theo ngày (VND đồng nguyên, bắt buộc).
    /// </summary>
    [Column("daily_rate", TypeName = "numeric(18,0)")]
    public decimal DailyRate { get; private set; }

    /// <summary>
    /// Giá thuê theo tuần (VND đồng nguyên, tùy chọn).
    /// </summary>
    [Column("weekly_rate", TypeName = "numeric(18,0)")]
    public decimal? WeeklyRate { get; private set; }

    /// <summary>
    /// Giá thuê theo tháng (VND đồng nguyên, tùy chọn).
    /// </summary>
    [Column("monthly_rate", TypeName = "numeric(18,0)")]
    public decimal? MonthlyRate { get; private set; }

    /// <summary>
    /// Đơn vị tiền tệ (bắt buộc bằng "VND").
    /// </summary>
    [Column("currency")]
    [MaxLength(10)]
    public string Currency { get; private set; } = DefaultCurrency;

    /// <summary>
    /// Thời điểm tạo bản ghi giá thuê (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm cập nhật giá thuê gần nhất (UTC).
    /// </summary>
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Aggregate root sở hữu giá thuê này.
    /// </summary>
    public InventoryAsset Asset { get; private set; } = null!;

    /// <summary>
    /// Khởi tạo rỗng phục vụ EF Core.
    /// </summary>
    private AssetRentalRate()
    {
    }

    private AssetRentalRate(
        Guid assetId,
        decimal dailyRate,
        decimal? weeklyRate,
        decimal? monthlyRate,
        string currency,
        DateTimeOffset now)
    {
        AssetId = assetId;
        DailyRate = dailyRate;
        WeeklyRate = weeklyRate;
        MonthlyRate = monthlyRate;
        Currency = currency;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Tạo mới bản ghi giá thuê niêm yết cho tài sản với đầy đủ kiểm tra tính hợp lệ dữ liệu.
    /// </summary>
    public static Result<AssetRentalRate> Create(
        Guid assetId,
        decimal dailyRate,
        decimal? weeklyRate,
        decimal? monthlyRate,
        string currency,
        DateTimeOffset now)
    {
        if (assetId == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng.");
        }

        var validationResult = ValidateRateValues(dailyRate, weeklyRate, monthlyRate, currency);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        return new AssetRentalRate(assetId, dailyRate, weeklyRate, monthlyRate, currency, now);
    }

    /// <summary>
    /// Cập nhật toàn bộ thông tin giá thuê (thay thế toàn bộ, tuần/tháng thiếu hoặc null sẽ được xóa).
    /// Cập nhật UpdatedAt, giữ nguyên CreatedAt.
    /// </summary>
    public Result UpdateRates(
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
    /// Kiểm tra tính hợp lệ của các trường giá thuê theo domain invariants.
    /// </summary>
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
