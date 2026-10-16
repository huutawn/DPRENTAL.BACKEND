using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.Locations;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Api.Tests.Security;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class WarehouseServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

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
    public async Task GetMyWarehouse_Unauthenticated_ReturnsUnauthorized()
    {
        var service = CreateService();
        var caller = new TestCaller { IsAuthenticated = false };

        var result = await service.GetMyWarehouseAsync(caller);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
    }

    [Fact]
    public async Task GetMyWarehouse_ServiceCaller_ReturnsForbidden()
    {
        var service = CreateService();
        var caller = new TestCaller { IsService = true, UserId = Guid.NewGuid() };

        var result = await service.GetMyWarehouseAsync(caller);

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Error.Code);
    }

    [Fact]
    public async Task GetMyWarehouse_NotFound_ReturnsWarehouseNotFound()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid() };

        var result = await service.GetMyWarehouseAsync(caller);

        Assert.True(result.IsFailure);
        Assert.Equal("WAREHOUSE_NOT_FOUND", result.Error.Code);
    }

    [Fact]
    public async Task GetMyWarehouse_Configured_ReturnsWarehouseDetails()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId,
            "123 Nguyễn Huệ, Phường Bến Nghé",
            "79",
            "Thành phố Hồ Chí Minh",
            "26734",
            "Phường Bến Nghé",
            10.7769,
            106.7009,
            FixedNow).Value;
        _warehouseRepo.Add(warehouse);

        var result = await service.GetMyWarehouseAsync(caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(ownerId, result.Value.OwnerUserId);
        Assert.Equal("123 Nguyễn Huệ, Phường Bến Nghé", result.Value.AddressDetail);
        Assert.Equal("79", result.Value.ProvinceCode);
        Assert.Equal("Thành phố Hồ Chí Minh", result.Value.ProvinceName);
        Assert.Equal("26734", result.Value.WardCode);
        Assert.Equal("Phường Bến Nghé", result.Value.WardName);
        Assert.Equal(10.7769, result.Value.Latitude);
        Assert.Equal(106.7009, result.Value.Longitude);
        Assert.True(result.Value.HasValidAddress);
    }

    [Fact]
    public async Task UpdateMyWarehouse_Unauthenticated_ReturnsUnauthorized()
    {
        var service = CreateService();
        var caller = new TestCaller { IsAuthenticated = false };
        var req = new UpdateWarehouseRequest("123 Le Loi", "79", "26734", 10.7, 106.7);

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
    }

    [Fact]
    public async Task UpdateMyWarehouse_InvalidLocationPair_ReturnsInvalidLocation()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid() };
        // Province 79 is HCM, ward 00004 is Ba Dinh in Ha Noi -> mismatched
        var req = new UpdateWarehouseRequest("123 Le Loi", "79", "00004", 10.7, 106.7);

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_LOCATION", result.Error.Code);
    }

    [Fact]
    public async Task UpdateMyWarehouse_InvalidCoordinates_ReturnsInvalidCoordinates()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid() };
        var req = new UpdateWarehouseRequest("123 Le Loi", "01", "00070", 95.0, 106.7); // lat > 90

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_COORDINATES", result.Error.Code);
    }

    [Fact]
    public async Task UpdateMyWarehouse_Valid_CreatesWarehouseWhenNotExists()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };
        var req = new UpdateWarehouseRequest("Số 1 Tràng Tiền", "01", "00070", 21.025, 105.856);

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(ownerId, result.Value.OwnerUserId);
        Assert.Equal("Số 1 Tràng Tiền", result.Value.AddressDetail);
        Assert.Equal("01", result.Value.ProvinceCode);
        Assert.Equal("Thành phố Hà Nội", result.Value.ProvinceName);
        Assert.Equal("00070", result.Value.WardCode);
        Assert.Equal("Phường Hoàn Kiếm", result.Value.WardName);
        Assert.True(result.Value.HasValidAddress);

        // Verify persisted in repo
        Assert.True(_warehouseRepo.Store.ContainsKey(ownerId));
    }

    [Fact]
    public async Task UpdateMyWarehouse_ExpectedVersionMismatch_ReturnsConflictStale()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };

        var warehouse = Warehouse.CreateWithAddress(
            ownerId, "Số 1 Tràng Tiền", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, FixedNow).Value;
        warehouse.Version = 100;
        _warehouseRepo.Add(warehouse);

        var req = new UpdateWarehouseRequest("Số 2 Tràng Tiền", "01", "00070", 21.0, 105.8, ExpectedVersion: 99);

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("STALE_WAREHOUSE_WRITE", result.Error.Code);
    }

    [Fact]
    public async Task UpdateMyWarehouse_MissingPermission_ReturnsForbidden()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid(), Permissions = new HashSet<string>() };
        var req = new UpdateWarehouseRequest("Số 1 Tràng Tiền", "01", "00070", 21.0, 105.8);

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Error.Code);
    }

    [Fact]
    public async Task UpdateMyWarehouse_AdminWithoutPermission_Succeeds()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId, IsAdmin = true, Permissions = new HashSet<string>() };
        var req = new UpdateWarehouseRequest("Số 1 Tràng Tiền", "01", "00070", 21.025, 105.856);

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(ownerId, result.Value.OwnerUserId);
    }

    [Fact]
    public async Task UpdateMyWarehouse_NullCoordinates_ReturnsInvalidCoordinates()
    {
        var service = CreateService();
        var caller = new TestCaller { UserId = Guid.NewGuid() };
        var reqNullLat = new UpdateWarehouseRequest("123 Le Loi", "01", "00070", null, 106.7);
        var reqNullLng = new UpdateWarehouseRequest("123 Le Loi", "01", "00070", 21.0, null);

        var resLat = await service.UpdateMyWarehouseAsync(reqNullLat, caller);
        var resLng = await service.UpdateMyWarehouseAsync(reqNullLng, caller);

        Assert.True(resLat.IsFailure);
        Assert.Equal("INVALID_COORDINATES", resLat.Error.Code);
        Assert.True(resLng.IsFailure);
        Assert.Equal("INVALID_COORDINATES", resLng.Error.Code);
    }

    [Fact]
    public async Task UpdateMyWarehouse_ZeroCoordinates_Succeeds()
    {
        var service = CreateService();
        var ownerId = Guid.NewGuid();
        var caller = new TestCaller { UserId = ownerId };
        // Coordinates (0.0, 0.0) are valid coordinates
        var req = new UpdateWarehouseRequest("Số 1 Tràng Tiền", "01", "00070", 0.0, 0.0);

        var result = await service.UpdateMyWarehouseAsync(req, caller);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.0, result.Value.Latitude);
        Assert.Equal(0.0, result.Value.Longitude);
    }
}
