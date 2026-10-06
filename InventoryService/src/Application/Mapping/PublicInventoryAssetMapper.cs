using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Mapping;

/// <summary>
/// Bộ ánh xạ thực thể InventoryAsset sang hợp đồng công khai PublicInventoryAssetResponse.
/// Đảm bảo an toàn bảo mật, làm sạch specification và gán URL thumbnail ổn định.
/// </summary>
public static class PublicInventoryAssetMapper
{
    public static PublicInventoryAssetResponse ToResponse(InventoryAsset asset, string? gatewayBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.RentalRate is null)
        {
            throw new InvalidOperationException($"Tài sản ID '{asset.Id}' thiếu cấu hình bảng giá niêm yết (RentalRate invariant violation).");
        }

        JsonElement sanitizedSpecs;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(asset.SpecValues) ? "{}" : asset.SpecValues);
            sanitizedSpecs = PublicAssetDataPolicy.SanitizeSpecValues(doc.RootElement);
        }
        catch (JsonException)
        {
            using var emptyDoc = JsonDocument.Parse("{}");
            sanitizedSpecs = emptyDoc.RootElement.Clone();
        }

        var rentalRateResponse = new PublicAssetRentalRateResponse(
            asset.RentalRate.DailyRate,
            asset.RentalRate.WeeklyRate,
            asset.RentalRate.MonthlyRate,
            asset.RentalRate.Currency);

        var thumbnailUrl = MediaContentUrlBuilder.Build(gatewayBaseUrl, asset.ThumbnailMediaId);

        return new PublicInventoryAssetResponse(
            Id: asset.Id,
            EquipmentTypeId: asset.EquipmentTypeId,
            BrandId: asset.BrandId,
            Model: asset.Model,
            ManufactureYear: asset.ManufactureYear,
            SchemaVersion: asset.SchemaVersion,
            SpecValues: sanitizedSpecs,
            RentalRate: rentalRateResponse,
            ThumbnailUrl: thumbnailUrl,
            AcceptingBookings: true);
    }
}
