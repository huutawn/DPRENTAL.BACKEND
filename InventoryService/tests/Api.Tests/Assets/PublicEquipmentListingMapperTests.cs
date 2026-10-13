using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PublicEquipmentListingMapperTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToResponse_ValidListing_MapsAllPublicPropertiesCorrectly()
    {
        var listingId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var thumbId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        var rawSpecs = """
            {
                "liftCapacityKg": 2500,
                "fuelType": "diesel",
                "ownerUserId": "private-user",
                "serial_number": "TOP-SECRET-SN",
                "assetCode": "AST-12345",
                "groupReference": "GROUP-999",
                "nested": {
                    "suspensionReason": "broken",
                    "safeField": "visible"
                }
            }
            """;

        var listing = EquipmentListing.Create(
            listingId,
            ownerId,
            eqTypeId,
            brandId,
            "Xe nâng dầu Toyota 2.5T",
            "Xe nâng hoạt động tốt",
            "Toyota 8FD25",
            2,
            rawSpecs,
            500_000m,
            3_000_000m,
            10_000_000m,
            "VND",
            FixedNow,
            2023,
            [videoId, thumbId],
            thumbId).Value;
        var gatewayUrl = "https://gateway.example.com/";
        var response = PublicEquipmentListingMapper.ToResponse(listing, totalQuantity: 10, availableQuantity: 7, gatewayUrl);

        Assert.Equal(listingId, response.Id);
        Assert.Equal(eqTypeId, response.EquipmentTypeId);
        Assert.Equal(brandId, response.BrandId);
        Assert.Equal("Xe nâng dầu Toyota 2.5T", response.Name);
        Assert.Equal("Xe nâng hoạt động tốt", response.Description);
        Assert.Equal("Toyota 8FD25", response.Model);
        Assert.Equal(2023, response.ManufactureYear);
        Assert.Equal(2, response.SchemaVersion);
        Assert.Equal(10, response.TotalQuantity);
        Assert.Equal(7, response.AvailableQuantity);

        Assert.NotNull(response.RentalRate);
        Assert.Equal(500_000m, response.RentalRate.DailyRate);
        Assert.Equal(3_000_000m, response.RentalRate.WeeklyRate);
        Assert.Equal(10_000_000m, response.RentalRate.MonthlyRate);
        Assert.Equal("VND", response.RentalRate.Currency);

        Assert.Equal(2, response.Media.Count);
        Assert.Equal(videoId, response.Media[0].Id);
        Assert.Equal($"https://gateway.example.com/api/v1/media/{videoId:D}/content", response.Media[0].Url);
        Assert.Equal(thumbId, response.Media[1].Id);
        Assert.Equal($"https://gateway.example.com/api/v1/media/{thumbId:D}/content", response.Media[1].Url);
        Assert.Equal($"https://gateway.example.com/api/v1/media/{thumbId:D}/content", response.ThumbnailUrl);
        var specs = response.SpecValues;
        Assert.Equal(2500, specs.GetProperty("liftCapacityKg").GetInt32());
        Assert.Equal("diesel", specs.GetProperty("fuelType").GetString());
        Assert.Equal("visible", specs.GetProperty("nested").GetProperty("safeField").GetString());

        // Reserved properties must be completely removed
        Assert.False(specs.TryGetProperty("ownerUserId", out _));
        Assert.False(specs.TryGetProperty("serial_number", out _));
        Assert.False(specs.TryGetProperty("assetCode", out _));
        Assert.False(specs.TryGetProperty("groupReference", out _));
        Assert.False(specs.GetProperty("nested").TryGetProperty("suspensionReason", out _));
    }
}
