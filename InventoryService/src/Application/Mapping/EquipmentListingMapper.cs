using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Mapping;

/// <summary>
/// Chuyển đổi thực thể EquipmentListing sang DTO EquipmentListingResponse.
/// </summary>
public static class EquipmentListingMapper
{
    public static EquipmentListingResponse ToResponse(
        EquipmentListing listing,
        int totalQuantity,
        string? publicGatewayUrl)
    {
        ArgumentNullException.ThrowIfNull(listing);

        using var doc = JsonDocument.Parse(listing.SpecValues);
        var rentalRateDto = new RentalRateDto(
            listing.DailyRate,
            listing.WeeklyRate,
            listing.MonthlyRate,
            listing.Currency);
        string? thumbnailUrl = MediaContentUrlBuilder.Build(publicGatewayUrl, listing.ThumbnailMediaId);
        return new EquipmentListingResponse(
            listing.Id,
            listing.OwnerUserId,
            listing.EquipmentTypeId,
            listing.BrandId,
            listing.Model,
            listing.SchemaVersion,
            doc.RootElement.Clone(),
            listing.ThumbnailMediaId,
            thumbnailUrl,
            rentalRateDto,
            listing.Status.ToString(),
            totalQuantity,
            listing.CreatedAt,
            listing.UpdatedAt);
    }
}
