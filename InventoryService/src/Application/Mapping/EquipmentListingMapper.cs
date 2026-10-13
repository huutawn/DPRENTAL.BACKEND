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
        var media = MediaContentUrlBuilder.BuildList(publicGatewayUrl, listing.MediaIds);
        string? thumbnailUrl = MediaContentUrlBuilder.Build(publicGatewayUrl, listing.ThumbnailMediaId);
        return new EquipmentListingResponse(
            listing.Id,
            listing.OwnerUserId,
            listing.EquipmentTypeId,
            listing.BrandId,
            listing.Name,
            listing.Description,
            listing.Model,
            listing.ManufactureYear,
            listing.SchemaVersion,
            doc.RootElement.Clone(),
            media,
            listing.ThumbnailMediaId,
            thumbnailUrl,
            rentalRateDto,
            listing.Status.ToString(),
            totalQuantity,
            listing.CreatedAt,
            listing.UpdatedAt);
    }
}
