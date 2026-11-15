using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.Locations;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class WarehouseDeliveryPolicyTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeWarehouseRepository : IWarehouseRepository
    {
        public readonly Dictionary<Guid, Warehouse> Store = new();

        public Task<Warehouse?> GetByOwnerUserIdAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
        {
            Store.TryGetValue(ownerUserId, out var warehouse);
            return Task.FromResult(warehouse);
        }

        public Task<bool> ExistsAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Store.ContainsKey(ownerUserId));
        }

        public void Add(Warehouse warehouse)
        {
            Store[warehouse.OwnerUserId] = warehouse;
        }
    }

    private sealed class FakeWriteContext : IInventoryWriteContext
    {
        public Guid AssetId => Guid.Empty;
        public Guid? ListingId => null;
        public IEquipmentListingRepository Listings => throw new NotImplementedException();
        public IInventoryAssetRepository Assets => throw new NotImplementedException();
        public IAssetAllocationRepository Allocations => throw new NotImplementedException();
        public ICalendarBlockRepository CalendarBlocks => throw new NotImplementedException();
        public IWarehouseRepository Warehouses { get; }
        public Guid? SearchBackfillRunId => null;

        public FakeWriteContext(IWarehouseRepository warehouses)
        {
            Warehouses = warehouses;
        }

        public Task AcquireAssetLocksAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StageOutbox(OutboxMessage message) { }
        public Task<long> GetNextSearchRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult(1L);
        public void StageCacheInvalidation(IEnumerable<string> scopes) { }
        public void StageWarehouseFanoutInvalidation(Guid ownerUserId, IEnumerable<string>? initialScopes = null) { }
    }

    private sealed class FakeWriteTransaction : IInventoryWriteTransaction
    {
        private readonly FakeWarehouseRepository _warehouses;

        public FakeWriteTransaction(FakeWarehouseRepository warehouses)
        {
            _warehouses = warehouses;
        }

        public Task<Result<T>> ExecuteAsync<T>(Guid assetId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            operation(new FakeWriteContext(_warehouses), cancellationToken);

        public Task<Result<T>> ExecuteListingWriteAsync<T>(Guid listingId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            operation(new FakeWriteContext(_warehouses), cancellationToken);

        public Task<Result<T>> ExecuteWarehouseWriteAsync<T>(Guid ownerUserId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            operation(new FakeWriteContext(_warehouses), cancellationToken);

        public Task<Result<T>> ExecuteGroupHoldAsync<T>(string sourceSystem, string groupReference, Guid listingId, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result<T>> ExecuteAllocationCommandAsync<T>(string sourceSystem, string operationKey, string groupReference, Func<IInventoryWriteContext, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class TestCaller : IInventoryCallerContext
    {
        public Guid? UserId { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsService { get; set; }
        public bool IsAuthenticated { get; set; } = true;
        public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string> { InventoryAccessEvaluator.PermissionAssetManage };
        public IReadOnlySet<string> Scopes { get; set; } = new HashSet<string>();
        public IReadOnlySet<string> Roles { get; set; } = new HashSet<string>();
        public Guid? OrganizationId { get; set; }
    }

    private readonly FakeWarehouseRepository _warehouseRepo = new();
    private readonly IVietnamDivisionsCatalog _divisionsCatalog = new VietnamDivisionsCatalog();
    private readonly TestTimeProvider _clock = new(FixedNow);

    private WarehouseService CreateService()
    {
        var writeTx = new FakeWriteTransaction(_warehouseRepo);
        return new WarehouseService(_warehouseRepo, _divisionsCatalog, writeTx, _clock);
    }

    [Fact]
    public async Task GetDeliveryPolicy_Unauthenticated_ReturnsUnauthorized()
    {
        var service = CreateService();
        var caller = new TestCaller { IsAuthenticated = false };

        var result = await service.GetDeliveryPolicyAsync(Guid.NewGuid(), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
    }

    [Fact]
    public async Task GetDeliveryPolicy_EmptySupplierId_ReturnsInvalidSupplierId()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid() };

        var result = await service.GetDeliveryPolicyAsync(Guid.Empty, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SUPPLIER_ID", result.Error.Code);
    }

    [Fact]
    public async Task GetDeliveryPolicy_WarehouseNotFound_ReturnsNotFound()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid() };

        var result = await service.GetDeliveryPolicyAsync(Guid.NewGuid(), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("WAREHOUSE_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetDeliveryPolicy_ConfiguredWarehouse_ReturnsPolicyMatchingContract()
    {
        var service = CreateService();
        var supplierId = Guid.NewGuid();
        var caller = new TestCaller { UserId = Guid.NewGuid() };

        var warehouse = Warehouse.CreateWithAddress(
            supplierId,
            "123 Nguyễn Huệ",
            "79",
            "Thành phố Hồ Chí Minh",
            "25747",
            "Phường Bến Nghé",
            10.776,
            106.700,
            FixedNow).Value;

        warehouse.SetDeliveryPolicy(true, "08:30", "17:30", [0], FixedNow);

        var zone = WarehouseDeliveryZone.Create(
            supplierId,
            "79",
            "25747",
            150000m,
            120000m,
            FixedNow).Value;
        warehouse.DeliveryZones.Add(zone);

        _warehouseRepo.Add(warehouse);

        var result = await service.GetDeliveryPolicyAsync(supplierId, caller);

        Assert.True(result.IsSuccess);
        var policy = result.Value;
        Assert.Equal(supplierId, policy.SupplierId);
        Assert.True(policy.IsPickupEnabled);
        Assert.NotNull(policy.WarehouseAddress);
        Assert.Equal("123 Nguyễn Huệ", policy.WarehouseAddress.AddressDetail);
        Assert.Equal("79", policy.WarehouseAddress.ProvinceCode);
        Assert.Equal("25747", policy.WarehouseAddress.WardCode);

        Assert.Equal("08:30", policy.OperatingHours.OpenTime);
        Assert.Equal("17:30", policy.OperatingHours.CloseTime);
        Assert.Contains(0, policy.OperatingHours.ClosedDays);

        Assert.Single(policy.DeliveryZones);
        Assert.Equal("79", policy.DeliveryZones[0].ProvinceCode);
        Assert.Equal("25747", policy.DeliveryZones[0].WardCode);
        Assert.Equal(150000m, policy.DeliveryZones[0].DeliveryFeeVnd);
        Assert.Equal(120000m, policy.DeliveryZones[0].ReturnFeeVnd);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_Unauthenticated_ReturnsUnauthorized()
    {
        var service = CreateService();
        var caller = new TestCaller { IsAuthenticated = false };
        var req = new UpdateWarehouseDeliveryPolicyRequest(
            true,
            new OperatingHoursDto("08:00", "18:00", []),
            []);

        var result = await service.UpdateDeliveryPolicyAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_MissingPermission_ReturnsForbidden()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid(), Permissions = new HashSet<string>() };
        var req = new UpdateWarehouseDeliveryPolicyRequest(
            true,
            new OperatingHoursDto("08:00", "18:00", []),
            []);

        var result = await service.UpdateDeliveryPolicyAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_WarehouseNotFound_ReturnsNotFound()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };
        var req = new UpdateWarehouseDeliveryPolicyRequest(
            true,
            new OperatingHoursDto("08:00", "18:00", []),
            []);

        var result = await service.UpdateDeliveryPolicyAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("WAREHOUSE_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_PickupWithoutValidAddress_FailsValidation()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        // Draft warehouse without address
        var warehouse = Warehouse.CreateDraft(ownerId, FixedNow);
        _warehouseRepo.Add(warehouse);

        var req = new UpdateWarehouseDeliveryPolicyRequest(
            true, // Pickup requested
            new OperatingHoursDto("08:00", "18:00", []),
            []);

        var result = await service.UpdateDeliveryPolicyAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("PICKUP_REQUIRES_VALID_ADDRESS", result.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_InvalidOperatingHours_FailsValidation()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "25747", "Ben Nghe", 10.7, 106.7, FixedNow).Value;
        _warehouseRepo.Add(warehouse);

        // Open >= Close
        var req1 = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("18:00", "08:00", []),
            []);
        var res1 = await service.UpdateDeliveryPolicyAsync(req1, caller);
        Assert.True(res1.IsFailure);
        Assert.Equal("INVALID_OPERATING_HOURS", res1.Error.Code);

        // Bad format
        var req2 = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("8am", "18:00", []),
            []);
        var res2 = await service.UpdateDeliveryPolicyAsync(req2, caller);
        Assert.True(res2.IsFailure);
        Assert.Equal("INVALID_OPEN_TIME", res2.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_InvalidClosedDays_FailsValidation()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "25747", "Ben Nghe", 10.7, 106.7, FixedNow).Value;
        _warehouseRepo.Add(warehouse);

        // Out of range day (7)
        var req1 = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", [7]),
            []);
        var res1 = await service.UpdateDeliveryPolicyAsync(req1, caller);
        Assert.True(res1.IsFailure);
        Assert.Equal("INVALID_CLOSED_DAYS", res1.Error.Code);

        // Duplicate day
        var req2 = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", [0, 0]),
            []);
        var res2 = await service.UpdateDeliveryPolicyAsync(req2, caller);
        Assert.True(res2.IsFailure);
        Assert.Equal("INVALID_CLOSED_DAYS", res2.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_NegativeFees_FailsValidation()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "25747", "Ben Nghe", 10.7, 106.7, FixedNow).Value;
        _warehouseRepo.Add(warehouse);

        var reqDelivery = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", []),
            [new DeliveryZoneDto("79", "25747", -100m, 50000m)]);
        var resDelivery = await service.UpdateDeliveryPolicyAsync(reqDelivery, caller);
        Assert.True(resDelivery.IsFailure);
        Assert.Equal("INVALID_DELIVERY_FEE", resDelivery.Error.Code);

        var reqReturn = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", []),
            [new DeliveryZoneDto("79", "25747", 50000m, -100m)]);
        var resReturn = await service.UpdateDeliveryPolicyAsync(reqReturn, caller);
        Assert.True(resReturn.IsFailure);
        Assert.Equal("INVALID_RETURN_FEE", resReturn.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_InvalidLocationZone_FailsValidation()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "25747", "Ben Nghe", 10.7, 106.7, FixedNow).Value;
        _warehouseRepo.Add(warehouse);

        // Non-existent province 999
        var reqBadProv = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", []),
            [new DeliveryZoneDto("999", null, 50000m, 50000m)]);
        var resBadProv = await service.UpdateDeliveryPolicyAsync(reqBadProv, caller);
        Assert.True(resBadProv.IsFailure);
        Assert.Equal("INVALID_LOCATION_ZONE", resBadProv.Error.Code);

        // Mismatched ward in province (Ward 00070 is in Hanoi 01, not HCM 79)
        var reqBadPair = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", []),
            [new DeliveryZoneDto("79", "00070", 50000m, 50000m)]);
        var resBadPair = await service.UpdateDeliveryPolicyAsync(reqBadPair, caller);
        Assert.True(resBadPair.IsFailure);
        Assert.Equal("INVALID_LOCATION_ZONE", resBadPair.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_DuplicateZonesInRequest_FailsValidation()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "25747", "Ben Nghe", 10.7, 106.7, FixedNow).Value;
        _warehouseRepo.Add(warehouse);

        var reqDup = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", []),
            [
                new DeliveryZoneDto("79", "25747", 50000m, 50000m),
                new DeliveryZoneDto("79", "25747", 60000m, 60000m)
            ]);
        var resDup = await service.UpdateDeliveryPolicyAsync(reqDup, caller);
        Assert.True(resDup.IsFailure);
        Assert.Equal("DUPLICATE_DELIVERY_ZONE", resDup.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_StaleVersion_ReturnsConflict()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "25747", "Ben Nghe", 10.7, 106.7, FixedNow).Value;
        warehouse.Version = 10;
        _warehouseRepo.Add(warehouse);

        var req = new UpdateWarehouseDeliveryPolicyRequest(
            false,
            new OperatingHoursDto("08:00", "18:00", []),
            [],
            ExpectedVersion: 99);

        var result = await service.UpdateDeliveryPolicyAsync(req, caller);
        Assert.True(result.IsFailure);
        Assert.Equal("STALE_WAREHOUSE_WRITE", result.Error.Code);
    }

    [Fact]
    public async Task UpdateDeliveryPolicy_ValidRequest_SucceedsAndSyncsZones()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "123 Le Loi", "79", "TP HCM", "25747", "Ben Nghe", 10.7, 106.7, FixedNow).Value;

        // Existing zone that will be updated
        var existingZone = WarehouseDeliveryZone.Create(ownerId, "79", "25747", 100000m, 100000m, FixedNow).Value;
        // Existing zone that will be removed
        var removedZone = WarehouseDeliveryZone.Create(ownerId, "79", "25750", 200000m, 200000m, FixedNow).Value;
        warehouse.DeliveryZones.Add(existingZone);
        warehouse.DeliveryZones.Add(removedZone);
        _warehouseRepo.Add(warehouse);

        // New request: updates 25747, adds province-level 01 (Hanoi), removes 25750
        var req = new UpdateWarehouseDeliveryPolicyRequest(
            true,
            new OperatingHoursDto("09:00", "19:00", [0, 6]),
            [
                new DeliveryZoneDto("79", "25747", 150000m, 120000m),
                new DeliveryZoneDto("01", null, 0m, 0m) // 0 VND free delivery test
            ]);

        var result = await service.UpdateDeliveryPolicyAsync(req, caller);

        Assert.True(result.IsSuccess);
        var policy = result.Value;
        Assert.True(policy.IsPickupEnabled);
        Assert.Equal("09:00", policy.OperatingHours.OpenTime);
        Assert.Equal("19:00", policy.OperatingHours.CloseTime);
        Assert.Equal([0, 6], policy.OperatingHours.ClosedDays);

        Assert.Equal(2, policy.DeliveryZones.Count);
        // Sorted by ProvinceCode then WardCode
        var hanoiZone = policy.DeliveryZones.First(z => z.ProvinceCode == "01");
        Assert.Null(hanoiZone.WardCode);
        Assert.Equal(0m, hanoiZone.DeliveryFeeVnd);
        Assert.Equal(0m, hanoiZone.ReturnFeeVnd);

        var hcmZone = policy.DeliveryZones.First(z => z.ProvinceCode == "79");
        Assert.Equal("25747", hcmZone.WardCode);
        Assert.Equal(150000m, hcmZone.DeliveryFeeVnd);
        Assert.Equal(120000m, hcmZone.ReturnFeeVnd);

        // Verify underlying warehouse entity was updated
        Assert.Equal(2, warehouse.DeliveryZones.Count);
        Assert.DoesNotContain(warehouse.DeliveryZones, z => z.WardCode == "25750");
    }
}
