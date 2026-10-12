using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Mapping;

/// <summary>
/// Chuyển đổi thực thể EquipmentListing sang DTO PublicEquipmentListingResponse.
/// Đảm bảo làm sạch toàn bộ dữ liệu nội bộ và nhạy cảm.
/// </summary>
public static class PublicEquipmentListingMapper
{
    public static PublicEquipmentListingResponse ToResponse(
        EquipmentListing listing,
        int totalQuantity,
        int availableQuantity,
        string? publicGatewayUrl)
    {
        ArgumentNullException.ThrowIfNull(listing);

        using var doc = JsonDocument.Parse(listing.SpecValues);
        var sanitizedSpecs = PublicAssetDataPolicy.SanitizeSpecValues(doc.RootElement);

        var rentalRateDto = new RentalRateDto(
            listing.DailyRate,
            listing.WeeklyRate,
            listing.MonthlyRate,
            listing.Currency);

        string? thumbnailUrl = MediaContentUrlBuilder.Build(publicGatewayUrl, listing.ThumbnailMediaId);

        return new PublicEquipmentListingResponse(
            listing.Id,
            listing.EquipmentTypeId,
            listing.BrandId,
            listing.Name,
            listing.Description,
            listing.Model,
            listing.SchemaVersion,
            sanitizedSpecs,
            rentalRateDto,
            thumbnailUrl,
            totalQuantity,
            availableQuantity);
    }
}
