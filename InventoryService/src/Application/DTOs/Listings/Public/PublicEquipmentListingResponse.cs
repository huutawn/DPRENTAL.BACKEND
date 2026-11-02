using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Media;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Hợp đồng DTO công khai đại diện cho một EquipmentListing (Public Equipment Listing).
/// Đã được làm sạch các trường nội bộ, bảo mật và chỉ xuất hiện khi Status = Active.
/// </summary>
public sealed record PublicEquipmentListingResponse(
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
    RentalRateDto RentalRate,
    decimal DepositAmount,
    IReadOnlyList<MediaReferenceResponse> Media,
    string? ThumbnailUrl,
    int TotalQuantity,
    int AvailableQuantity,
    WarehouseAreaDto? WarehouseArea = null,
    Guid? OrganizationId = null,
    IReadOnlyList<Guid>? CategoryIds = null
);
