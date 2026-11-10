using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Media;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thông tin phản hồi chi tiết của một EquipmentListing cho chủ sở hữu hoặc quản trị viên.
/// Bao gồm năm sản xuất, danh sách media theo thứ tự, ảnh đại diện và derived totalQuantity.
/// </summary>
public sealed record EquipmentListingResponse(
    Guid Id,
    Guid OwnerUserId,
    Guid EquipmentTypeId,
    Guid BrandId,
    string Name,
    string? Description,
    string Model,
    int? ManufactureYear,
    int SchemaVersion,
    JsonElement SpecValues,
    IReadOnlyList<MediaReferenceResponse> Media,
    Guid? ThumbnailMediaId,
    string? ThumbnailUrl,
    RentalRateDto RentalRate,
    decimal DepositAmount,
    string Status,
    int TotalQuantity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
