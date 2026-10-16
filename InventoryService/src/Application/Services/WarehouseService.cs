using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Locations;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Services;

public sealed class WarehouseService : IWarehouseService
{
    private readonly IWarehouseRepository _warehouseRepository;
    private readonly IVietnamDivisionsCatalog _divisionsCatalog;
    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly Dprental.InventoryService.Application.Caching.IInventoryReadCache _cache;
    private readonly Dprental.InventoryService.Application.Caching.InventoryCacheOptions _cacheOptions;
    private readonly TimeProvider _timeProvider;

    public WarehouseService(
        IWarehouseRepository warehouseRepository,
        IVietnamDivisionsCatalog divisionsCatalog,
        IInventoryWriteTransaction writeTransaction,
        TimeProvider timeProvider)
        : this(warehouseRepository, divisionsCatalog, writeTransaction, null, null, timeProvider)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public WarehouseService(
        IWarehouseRepository warehouseRepository,
        IVietnamDivisionsCatalog divisionsCatalog,
        IInventoryWriteTransaction writeTransaction,
        Dprental.InventoryService.Application.Caching.IInventoryReadCache? cache = null,
        Microsoft.Extensions.Options.IOptions<Dprental.InventoryService.Application.Caching.InventoryCacheOptions>? cacheOptions = null,
        TimeProvider? timeProvider = null)
    {
        _warehouseRepository = warehouseRepository ?? throw new ArgumentNullException(nameof(warehouseRepository));
        _divisionsCatalog = divisionsCatalog ?? throw new ArgumentNullException(nameof(divisionsCatalog));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _cache = cache ?? Dprental.InventoryService.Application.Caching.NoOpInventoryReadCache.Instance;
        _cacheOptions = cacheOptions?.Value ?? new Dprental.InventoryService.Application.Caching.InventoryCacheOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Result<WarehouseResponse>> GetMyWarehouseAsync(
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có quyền truy cập kho.");
        }

        var ownerUserId = caller.UserId.Value;
        string scope = $"owner:{ownerUserId:D}";
        string queryType = "warehouse";
        string inputHash = "my";

        return await _cache.GetOrCreateAsync<WarehouseResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var warehouse = await _warehouseRepository.GetByOwnerUserIdAsync(ownerUserId, ct);
                if (warehouse is null)
                {
                    return Error.NotFound("WAREHOUSE_NOT_FOUND", "Người dùng chưa thiết lập kho.");
                }

                var response = ToResponse(warehouse);
                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<WarehouseResponse>(response, ttl));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<WarehouseResponse>> UpdateMyWarehouseAsync(
        UpdateWarehouseRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!InventoryAccessEvaluator.CanManageOwnInventory(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Người dùng không có quyền quản lý kho tài sản.");
        }

        if (request is null)
        {
            return Error.Validation("INVALID_REQUEST", "Yêu cầu không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(request.AddressDetail))
        {
            return Error.Validation("INVALID_ADDRESS_DETAIL", "Địa chỉ chi tiết không được để trống.");
        }

        var trimmedDetail = request.AddressDetail.Trim();
        if (trimmedDetail.Length > Warehouse.MaxAddressDetailLength)
        {
            return Error.Validation("INVALID_ADDRESS_DETAIL", $"Địa chỉ chi tiết không được vượt quá {Warehouse.MaxAddressDetailLength} ký tự.");
        }

        if (!_divisionsCatalog.IsValidPair(request.ProvinceCode, request.WardCode, out var canonicalProvince, out var canonicalWard))
        {
            return Error.Validation("INVALID_LOCATION", "Cặp tỉnh/thành phố và phường/xã không hợp lệ.");
        }

        if (!request.Latitude.HasValue || !request.Longitude.HasValue)
        {
            return Error.Validation("INVALID_COORDINATES", "Tọa độ (vĩ độ và kinh độ) là bắt buộc và không được để trống.");
        }

        double latitude = request.Latitude.Value;
        double longitude = request.Longitude.Value;

        if (double.IsNaN(latitude) || double.IsInfinity(latitude) ||
            latitude < Warehouse.MinLatitude || latitude > Warehouse.MaxLatitude)
        {
            return Error.Validation("INVALID_COORDINATES", $"Vĩ độ (latitude) phải nằm trong khoảng [{Warehouse.MinLatitude}, {Warehouse.MaxLatitude}].");
        }

        if (double.IsNaN(longitude) || double.IsInfinity(longitude) ||
            longitude < Warehouse.MinLongitude || longitude > Warehouse.MaxLongitude)
        {
            return Error.Validation("INVALID_COORDINATES", $"Kinh độ (longitude) phải nằm trong khoảng [{Warehouse.MinLongitude}, {Warehouse.MaxLongitude}].");
        }

        var ownerUserId = caller.UserId!.Value;
        var now = _timeProvider.GetUtcNow();

        var txResult = await _writeTransaction.ExecuteWarehouseWriteAsync(ownerUserId, async (ctx, ct) =>
        {
            var warehouse = await ctx.Warehouses.GetByOwnerUserIdAsync(ownerUserId, ct);

            if (warehouse is null)
            {
                var createResult = Warehouse.CreateWithAddress(
                    ownerUserId,
                    trimmedDetail,
                    request.ProvinceCode.Trim(),
                    canonicalProvince,
                    request.WardCode.Trim(),
                    canonicalWard,
                    latitude,
                    longitude,
                    now);

                if (createResult.IsFailure)
                {
                    return createResult.Error;
                }

                warehouse = createResult.Value;
                ctx.Warehouses.Add(warehouse);
            }
            else
            {
                if (request.ExpectedVersion.HasValue && warehouse.Version != request.ExpectedVersion.Value)
                {
                    return Error.Conflict("STALE_WAREHOUSE_WRITE", "Dữ liệu kho đã được thay đổi; đọc lại trước khi thử lại.");
                }

                var updateResult = warehouse.SetAddress(
                    trimmedDetail,
                    request.ProvinceCode.Trim(),
                    canonicalProvince,
                    request.WardCode.Trim(),
                    canonicalWard,
                    latitude,
                    longitude,
                    now);

                if (updateResult.IsFailure)
                {
                    return updateResult.Error;
                }
            }


            if (_cacheOptions.Enabled)
            {
                ctx.StageWarehouseFanoutInvalidation(
                    ownerUserId,
                    [$"owner:{ownerUserId:D}", "public-discovery"]);
            }
            return Result.Success(warehouse);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return Result.Success(ToResponse(txResult.Value));
    }

    private static WarehouseResponse ToResponse(Warehouse warehouse) => new(
        warehouse.OwnerUserId,
        warehouse.AddressDetail,
        warehouse.ProvinceCode,
        warehouse.ProvinceName,
        warehouse.WardCode,
        warehouse.WardName,
        warehouse.Latitude,
        warehouse.Longitude,
        warehouse.HasValidAddress,
        warehouse.UpdatedAt,
        warehouse.Version
    );
}
