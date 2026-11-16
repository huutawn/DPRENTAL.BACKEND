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

    /// <inheritdoc />
    public async Task<Result<WarehouseDeliveryPolicyResponse>> GetMyDeliveryPolicyAsync(
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated || !caller.UserId.HasValue)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!InventoryAccessEvaluator.CanManageOwnInventory(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Người dùng không có quyền quản lý kho tài sản.");
        }

        return await GetDeliveryPolicyAsync(caller.UserId.Value, caller, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<WarehouseDeliveryPolicyResponse>> GetDeliveryPolicyAsync(
        Guid supplierId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (supplierId == Guid.Empty)
        {
            return Error.Validation("INVALID_SUPPLIER_ID", "SupplierId không được để trống.");
        }

        string scope = $"owner:{supplierId:D}";
        string queryType = "delivery-policy";
        string inputHash = supplierId.ToString("N");

        return await _cache.GetOrCreateAsync<WarehouseDeliveryPolicyResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var warehouse = await _warehouseRepository.GetByOwnerUserIdAsync(supplierId, ct);
                if (warehouse is null)
                {
                    return Error.NotFound("WAREHOUSE_NOT_FOUND", "Không tìm thấy kho của nhà cung cấp.");
                }

                var response = ToDeliveryPolicyResponse(warehouse);
                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<WarehouseDeliveryPolicyResponse>(response, ttl));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<WarehouseDeliveryPolicyResponse>> UpdateDeliveryPolicyAsync(
        UpdateWarehouseDeliveryPolicyRequest request,
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

        if (request.OperatingHours is null)
        {
            return Error.Validation("INVALID_OPERATING_HOURS", "Giờ phục vụ không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(request.OperatingHours.OpenTime) ||
            !TimeOnly.TryParseExact(request.OperatingHours.OpenTime.Trim(), "HH:mm", out var parsedOpen))
        {
            return Error.Validation("INVALID_OPEN_TIME", "Giờ mở cửa phải theo định dạng HH:mm (ví dụ 08:00).");
        }

        if (string.IsNullOrWhiteSpace(request.OperatingHours.CloseTime) ||
            !TimeOnly.TryParseExact(request.OperatingHours.CloseTime.Trim(), "HH:mm", out var parsedClose))
        {
            return Error.Validation("INVALID_CLOSE_TIME", "Giờ đóng cửa phải theo định dạng HH:mm (ví dụ 18:00).");
        }

        if (parsedOpen >= parsedClose)
        {
            return Error.Validation("INVALID_OPERATING_HOURS", "Giờ mở cửa phải trước giờ đóng cửa.");
        }

        if (request.OperatingHours.ClosedDays is not null && request.OperatingHours.ClosedDays.Count > 0)
        {
            if (request.OperatingHours.ClosedDays.Any(d => d < 0 || d > 6))
            {
                return Error.Validation("INVALID_CLOSED_DAYS", "Ngày nghỉ phải nằm trong khoảng 0 đến 6 (0: Chủ nhật, 6: Thứ bảy).");
            }

            if (request.OperatingHours.ClosedDays.Distinct().Count() != request.OperatingHours.ClosedDays.Count)
            {
                return Error.Validation("INVALID_CLOSED_DAYS", "Ngày nghỉ không được trùng lặp.");
            }
        }

        var zones = request.DeliveryZones ?? [];
        var seenZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var zone in zones)
        {
            if (zone is null)
            {
                return Error.Validation("INVALID_DELIVERY_ZONE", "Thông tin vùng giao hàng không được null.");
            }

            if (zone.DeliveryFeeVnd < 0 || decimal.Truncate(zone.DeliveryFeeVnd) != zone.DeliveryFeeVnd)
            {
                return Error.Validation("INVALID_DELIVERY_FEE", "Phí giao hàng phải là số nguyên VND >= 0.");
            }

            if (zone.ReturnFeeVnd < 0 || decimal.Truncate(zone.ReturnFeeVnd) != zone.ReturnFeeVnd)
            {
                return Error.Validation("INVALID_RETURN_FEE", "Phí thu hồi phải là số nguyên VND >= 0.");
            }

            if (string.IsNullOrWhiteSpace(zone.ProvinceCode))
            {
                return Error.Validation("INVALID_PROVINCE_CODE", "Mã tỉnh/thành phố không được để trống.");
            }

            string provinceCode = zone.ProvinceCode.Trim();
            string? wardCode = string.IsNullOrWhiteSpace(zone.WardCode) ? null : zone.WardCode.Trim();

            if (wardCode is not null)
            {
                if (!_divisionsCatalog.IsValidPair(provinceCode, wardCode, out _, out _))
                {
                    return Error.Validation("INVALID_LOCATION_ZONE", $"Cặp tỉnh ({provinceCode}) và phường/xã ({wardCode}) không hợp lệ.");
                }
            }
            else
            {
                if (!_divisionsCatalog.IsValidProvince(provinceCode, out _))
                {
                    return Error.Validation("INVALID_LOCATION_ZONE", $"Mã tỉnh/thành phố ({provinceCode}) không hợp lệ.");
                }
            }

            string zoneKey = $"{provinceCode}:{wardCode ?? ""}";
            if (!seenZones.Add(zoneKey))
            {
                return Error.Validation("DUPLICATE_DELIVERY_ZONE", $"Vùng giao hàng ({provinceCode}, {wardCode ?? "tất cả"}) bị trùng lặp.");
            }
        }

        var ownerUserId = caller.UserId!.Value;
        var now = _timeProvider.GetUtcNow();

        var txResult = await _writeTransaction.ExecuteWarehouseWriteAsync(ownerUserId, async (ctx, ct) =>
        {
            var warehouse = await ctx.Warehouses.GetByOwnerUserIdAsync(ownerUserId, ct);
            if (warehouse is null)
            {
                return Error.NotFound("WAREHOUSE_NOT_FOUND", "Người dùng chưa thiết lập kho. Vui lòng thiết lập địa chỉ kho trước.");
            }

            if (request.ExpectedVersion.HasValue && warehouse.Version != request.ExpectedVersion.Value)
            {
                return Error.Conflict("STALE_WAREHOUSE_WRITE", "Dữ liệu kho đã được thay đổi; đọc lại trước khi thử lại.");
            }

            var policyResult = warehouse.SetDeliveryPolicy(
                request.IsPickupEnabled,
                request.OperatingHours.OpenTime.Trim(),
                request.OperatingHours.CloseTime.Trim(),
                request.OperatingHours.ClosedDays?.ToArray(),
                now);

            if (policyResult.IsFailure)
            {
                return policyResult.Error;
            }

            var existingZones = warehouse.DeliveryZones.ToList();
            var incomingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var zone in zones)
            {
                string provinceCode = zone.ProvinceCode.Trim();
                string? wardCode = string.IsNullOrWhiteSpace(zone.WardCode) ? null : zone.WardCode.Trim();
                string key = $"{provinceCode}:{wardCode ?? ""}";
                incomingKeys.Add(key);

                var existing = existingZones.FirstOrDefault(ez =>
                    string.Equals(ez.ProvinceCode, provinceCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ez.WardCode ?? "", wardCode ?? "", StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    existing.UpdateFees(zone.DeliveryFeeVnd, zone.ReturnFeeVnd, now);
                }
                else
                {
                    var newZoneResult = WarehouseDeliveryZone.Create(
                        ownerUserId,
                        provinceCode,
                        wardCode,
                        zone.DeliveryFeeVnd,
                        zone.ReturnFeeVnd,
                        now);

                    if (newZoneResult.IsFailure)
                    {
                        return newZoneResult.Error;
                    }

                    warehouse.DeliveryZones.Add(newZoneResult.Value);
                }
            }

            foreach (var existing in existingZones)
            {
                string key = $"{existing.ProvinceCode}:{existing.WardCode ?? ""}";
                if (!incomingKeys.Contains(key))
                {
                    warehouse.DeliveryZones.Remove(existing);
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

        return Result.Success(ToDeliveryPolicyResponse(txResult.Value));
    }

    private WarehouseDeliveryPolicyResponse ToDeliveryPolicyResponse(Warehouse warehouse)
    {
        WarehouseAddressDto? addressDto = null;
        if (!string.IsNullOrWhiteSpace(warehouse.AddressDetail))
        {
            addressDto = new WarehouseAddressDto(
                warehouse.AddressDetail,
                warehouse.ProvinceCode,
                warehouse.WardCode,
                warehouse.ProvinceName,
                warehouse.WardName);
        }

        var operatingHours = new OperatingHoursDto(
            warehouse.OpenTime ?? "08:00",
            warehouse.CloseTime ?? "18:00",
            warehouse.ClosedDays ?? []);

        var deliveryZones = (warehouse.DeliveryZones ?? [])
            .OrderBy(z => z.ProvinceCode)
            .ThenBy(z => z.WardCode)
            .Select(z =>
            {
                string? pName = null;
                string? wName = null;
                if (!string.IsNullOrWhiteSpace(z.WardCode))
                {
                    _divisionsCatalog.IsValidPair(z.ProvinceCode, z.WardCode, out pName, out wName);
                }
                else
                {
                    _divisionsCatalog.IsValidProvince(z.ProvinceCode, out pName);
                }

                return new DeliveryZoneDto(
                    z.ProvinceCode,
                    z.WardCode,
                    z.DeliveryFeeVnd,
                    z.ReturnFeeVnd,
                    pName,
                    wName);
            })
            .ToList();

        return new WarehouseDeliveryPolicyResponse(
            warehouse.OwnerUserId,
            warehouse.IsPickupEnabled,
            addressDto,
            operatingHours,
            deliveryZones);
    }

    private WarehouseResponse ToResponse(Warehouse warehouse)
    {
        var operatingHours = new OperatingHoursDto(
            warehouse.OpenTime ?? "08:00",
            warehouse.CloseTime ?? "18:00",
            warehouse.ClosedDays ?? []);

        var deliveryZones = (warehouse.DeliveryZones ?? [])
            .OrderBy(z => z.ProvinceCode)
            .ThenBy(z => z.WardCode)
            .Select(z =>
            {
                string? pName = null;
                string? wName = null;
                if (!string.IsNullOrWhiteSpace(z.WardCode))
                {
                    _divisionsCatalog.IsValidPair(z.ProvinceCode, z.WardCode, out pName, out wName);
                }
                else
                {
                    _divisionsCatalog.IsValidProvince(z.ProvinceCode, out pName);
                }

                return new DeliveryZoneDto(
                    z.ProvinceCode,
                    z.WardCode,
                    z.DeliveryFeeVnd,
                    z.ReturnFeeVnd,
                    pName,
                    wName);
            })
            .ToList();

        return new(
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
            warehouse.Version,
            warehouse.IsPickupEnabled,
            operatingHours,
            deliveryZones
        );
    }
}
