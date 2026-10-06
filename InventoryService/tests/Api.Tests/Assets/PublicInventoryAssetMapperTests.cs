using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class PublicInventoryAssetMapperTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToResponse_ValidAsset_MapsAllPublicPropertiesCorrectly()
    {
        var assetId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var thumbId = Guid.NewGuid();
        var rate = AssetRentalRate.Create(assetId, 500_000m, 3_000_000m, 10_000_000m, "VND", FixedNow).Value;

        var rawSpecs = """
            {
                "liftCapacityKg": 2500,
                "fuelType": "diesel",
                "ownerUserId": "private-user",
                "serial_number": "TOP-SECRET-SN",
                "nested": {
                    "suspensionReason": "broken",
                    "safeField": "visible"
                }
            }
            """;

        var asset = InventoryAsset.Create(
            assetId,
            ownerId,
            eqTypeId,
            brandId,
            "Toyota 8FD25",
            2,
            rawSpecs,
            FixedNow,
            rate,
            "SN-REAL",
            2023,
            thumbId).Value;

        var gatewayUrl = "https://gateway.example.com/";
        var response = PublicInventoryAssetMapper.ToResponse(asset, gatewayUrl);

        Assert.Equal(assetId, response.Id);
        Assert.Equal(eqTypeId, response.EquipmentTypeId);
        Assert.Equal(brandId, response.BrandId);
        Assert.Equal("Toyota 8FD25", response.Model);
        Assert.Equal(2023, response.ManufactureYear);
        Assert.Equal(2, response.SchemaVersion);
        Assert.True(response.AcceptingBookings);

        // Thumbnail URL
        Assert.Equal($"https://gateway.example.com/api/v1/media/{thumbId:D}/content", response.ThumbnailUrl);

        // Rental Rate
        Assert.NotNull(response.RentalRate);
        Assert.Equal(500_000m, response.RentalRate.DailyRate);
        Assert.Equal(3_000_000m, response.RentalRate.WeeklyRate);
        Assert.Equal(10_000_000m, response.RentalRate.MonthlyRate);
        Assert.Equal("VND", response.RentalRate.Currency);

        // Spec values sanitized
        var specs = response.SpecValues;
        Assert.True(specs.TryGetProperty("liftCapacityKg", out var cap));
        Assert.Equal(2500, cap.GetInt32());
        Assert.True(specs.TryGetProperty("fuelType", out var fuel));
        Assert.Equal("diesel", fuel.GetString());

        // Reserved keys removed
        Assert.False(specs.TryGetProperty("ownerUserId", out _));
        Assert.False(specs.TryGetProperty("serial_number", out _));

        // Nested reserved key removed
        Assert.True(specs.TryGetProperty("nested", out var nested));
        Assert.False(nested.TryGetProperty("suspensionReason", out _));
        Assert.True(nested.TryGetProperty("safeField", out var safe));
        Assert.Equal("visible", safe.GetString());
    }

    [Fact]
    public void ToResponse_NullRentalRate_ThrowsInvalidOperationException()
    {
        var assetId = Guid.NewGuid();
        var ctor = typeof(InventoryAsset).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(c => c.GetParameters().Length > 0);
        var asset = (InventoryAsset)ctor.Invoke([
            assetId, Guid.NewGuid(), Guid.NewGuid(), (Guid?)null, "M", (string?)null, (int?)null, (Guid?)null, 1, "{}", true, (string?)null, null, FixedNow
        ]);

        Assert.Throws<InvalidOperationException>(() => PublicInventoryAssetMapper.ToResponse(asset, "https://gateway.example"));
    }

    [Fact]
    public void ToResponse_NullThumbnail_ReturnsNullThumbnailUrl()
    {
        var assetId = Guid.NewGuid();
        var rate = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", FixedNow).Value;
        var asset = InventoryAsset.Create(
            assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "M", 1, "{}", FixedNow, rate, null, null, null).Value;
        var response = PublicInventoryAssetMapper.ToResponse(asset, "https://gateway.example");
        Assert.Null(response.ThumbnailUrl);
    }

    [Theory]
    [InlineData("ownerUserId")]
    [InlineData("owner_user_id")]
    [InlineData("organizationId")]
    [InlineData("organization_id")]
    [InlineData("serialNumber")]
    [InlineData("serial_number")]
    [InlineData("serialNo")]
    [InlineData("displayName")]
    [InlineData("display_name")]
    [InlineData("suspensionReason")]
    [InlineData("suspension_reason")]
    [InlineData("version")]
    [InlineData("xmin")]
    [InlineData("calendarBlocks")]
    [InlineData("allocations")]
    [InlineData("holdExpiresAt")]
    [InlineData("hold_expires_at")]
    [InlineData("allocationId")]
    [InlineData("externalReference")]
    [InlineData("sourceSystem")]
    [InlineData("renterId")]
    [InlineData("renter_id")]
    [InlineData("renterUserId")]
    [InlineData("renter_user_id")]
    [InlineData("orderId")]
    [InlineData("order_id")]
    [InlineData("orderReference")]
    [InlineData("order_reference")]
    [InlineData("rentalOrderId")]
    [InlineData("rental_order_id")]
    [InlineData("rentalOrderReference")]
    [InlineData("rental_order_reference")]
    [InlineData("reservationId")]
    [InlineData("reservation_id")]
    [InlineData("reservationReference")]
    [InlineData("reservation_reference")]
    [InlineData("createdAt")]
    [InlineData("updatedAt")]
    [InlineData("permissions")]
    [InlineData("inventory.asset.manage")]
    public void ToResponse_SanitizesEveryReservedProperty_AtRootAndNested(string reservedPropertyName)
    {
        var assetId = Guid.NewGuid();
        var rate = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", FixedNow).Value;
        var rawSpecs = $$"""
            {
                "{{reservedPropertyName}}": "forbidden-value",
                "nested": {
                    "{{reservedPropertyName}}": "nested-forbidden-value",
                    "safe": 123
                },
                "normal": "value"
            }
            """;

        var asset = InventoryAsset.Create(
            assetId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "M", 1, rawSpecs, FixedNow, rate, null, null, null).Value;

        var response = PublicInventoryAssetMapper.ToResponse(asset, "https://gateway.example");
        var specs = response.SpecValues;

        Assert.False(specs.TryGetProperty(reservedPropertyName, out _));
        Assert.True(specs.TryGetProperty("nested", out var nested));
        Assert.False(nested.TryGetProperty(reservedPropertyName, out _));
        Assert.True(nested.TryGetProperty("safe", out var safe));
        Assert.Equal(123, safe.GetInt32());
        Assert.True(specs.TryGetProperty("normal", out var normal));
        Assert.Equal("value", normal.GetString());
    }
}
