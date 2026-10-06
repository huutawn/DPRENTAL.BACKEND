using System.Text.Json;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Bảng giá thuê niêm yết công khai của tài sản (không kèm logic tính giá thương mại).
/// </summary>
public sealed record PublicAssetRentalRateResponse(
    decimal DailyRate,
    decimal? WeeklyRate,
    decimal? MonthlyRate,
    string Currency);

/// <summary>
/// Hợp đồng DTO công khai đại diện cho một tài sản cho thuê (Public Inventory Asset).
/// Đã được làm sạch các trường nội bộ, bảo mật và chỉ xuất hiện khi AcceptingBookings = true.
/// </summary>
public sealed record PublicInventoryAssetResponse(
    Guid Id,
    Guid EquipmentTypeId,
    Guid? BrandId,
    string Model,
    int? ManufactureYear,
    int SchemaVersion,
    JsonElement SpecValues,
    PublicAssetRentalRateResponse RentalRate,
    string? ThumbnailUrl,
    bool AcceptingBookings);
