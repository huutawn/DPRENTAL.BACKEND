using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

public sealed class WarehouseDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WarehouseDeliveryZone_Create_ValidInputs_Succeeds()
    {
        var ownerId = Guid.NewGuid();
        var result = WarehouseDeliveryZone.Create(ownerId, "79", "26734", 150000m, 120000m, FixedNow);

        Assert.True(result.IsSuccess);
        var zone = result.Value;
        Assert.NotEqual(Guid.Empty, zone.Id);
        Assert.Equal(ownerId, zone.OwnerUserId);
        Assert.Equal("79", zone.ProvinceCode);
        Assert.Equal("26734", zone.WardCode);
        Assert.Equal(150000m, zone.DeliveryFeeVnd);
        Assert.Equal(120000m, zone.ReturnFeeVnd);
    }

    [Fact]
    public void WarehouseDeliveryZone_Create_NullWard_SucceedsForWholeProvince()
    {
        var ownerId = Guid.NewGuid();
        var result = WarehouseDeliveryZone.Create(ownerId, "79", null, 0m, 0m, FixedNow);

        Assert.True(result.IsSuccess);
        var zone = result.Value;
        Assert.Null(zone.WardCode);
        Assert.Equal(0m, zone.DeliveryFeeVnd);
        Assert.Equal(0m, zone.ReturnFeeVnd);
    }

    [Fact]
    public void WarehouseDeliveryZone_Create_NegativeOrDecimalFee_Fails()
    {
        var ownerId = Guid.NewGuid();
        var resNegative = WarehouseDeliveryZone.Create(ownerId, "79", "26734", -1m, 1000m, FixedNow);
        Assert.True(resNegative.IsFailure);
        Assert.Equal("INVALID_DELIVERY_FEE", resNegative.Error.Code);

        var resFraction = WarehouseDeliveryZone.Create(ownerId, "79", "26734", 1000.5m, 1000m, FixedNow);
        Assert.True(resFraction.IsFailure);
        Assert.Equal("INVALID_DELIVERY_FEE", resFraction.Error.Code);
    }

    [Fact]
    public void Warehouse_SetDeliveryPolicy_WithoutAddress_FailsPickup()
    {
        var ownerId = Guid.NewGuid();
        var warehouse = Warehouse.CreateDraft(ownerId, FixedNow);

        var result = warehouse.SetDeliveryPolicy(true, "08:00", "18:00", [], FixedNow);

        Assert.True(result.IsFailure);
        Assert.Equal("PICKUP_REQUIRES_VALID_ADDRESS", result.Error.Code);
    }

    [Fact]
    public void Warehouse_SetDeliveryPolicy_ValidInputs_Succeeds()
    {
        var ownerId = Guid.NewGuid();
        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "26734", "Ben Nghe", 10.7, 106.7, FixedNow).Value;

        var result = warehouse.SetDeliveryPolicy(true, "08:30", "17:30", [0, 6], FixedNow);

        Assert.True(result.IsSuccess);
        Assert.True(warehouse.IsPickupEnabled);
        Assert.Equal("08:30", warehouse.OpenTime);
        Assert.Equal("17:30", warehouse.CloseTime);
        Assert.Equal([0, 6], warehouse.ClosedDays);
    }
}
