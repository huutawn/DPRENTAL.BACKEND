using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Services;

public sealed partial class EquipmentListingService
{
    /// <inheritdoc />
    public async Task<Result<EquipmentListingResponse>> CreateListingAsync(
        CreateEquipmentListingRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        // 1. Authentication và permission check
        if (!caller.IsAuthenticated || !caller.UserId.HasValue)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsAdmin && !caller.Permissions.Contains(InventoryAccessEvaluator.PermissionAssetManage))
        {
            return Error.Forbidden("FORBIDDEN", "Người dùng không có quyền quản lý kho tài sản.");
        }

        // 2. Local validation
        if (string.IsNullOrWhiteSpace(request.SerialNumber))
        {
            return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial là bắt buộc và không được để trống.");
        }

        var normalizedSerial = request.SerialNumber.Trim();
        if (normalizedSerial.Length > 100)
        {
            return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial tối đa 100 ký tự.");
        }

        if (request.EquipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }

        if (request.BrandId == Guid.Empty)
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được để trống hoặc rỗng.");
        }

        if (request.RentalRate is null)
        {
            return Error.Validation("RENTAL_RATE_REQUIRED", "Thông tin giá thuê (rentalRate) là bắt buộc.");
        }

        var rateValidation = request.RentalRate.Validate();
        if (rateValidation.IsFailure)
        {
            return rateValidation.Error;
        }

        if (request.SpecValues.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values phải là JSON object.");
        }

        var specValuesJson = request.SpecValues.GetRawText();
        var detailsValidation = EquipmentListing.ValidateDetails(
            request.Model,
            request.ThumbnailMediaId,
            request.SchemaVersion,
            specValuesJson);

        if (detailsValidation.IsFailure)
        {
            return detailsValidation.Error;
        }

        var (validModel, validThumb, validSchema, validSpecs) = detailsValidation.Value;

        // 3. Published EquipmentType check
        var catLookup = await _catalogClient.GetPublishedEquipmentTypeAsync(request.EquipmentTypeId, cancellationToken);
        if (catLookup.IsFailure)
        {
            return catLookup.Error;
        }

        // 4. Active Brand check
        var brandLookup = await _catalogClient.GetBrandAsync(request.BrandId, cancellationToken);
        if (brandLookup.IsFailure)
        {
            return brandLookup.Error;
        }

        if (!brandLookup.Value.IsActive)
        {
            return Error.Validation("Brand.Inactive", "Thương hiệu đang ở trạng thái không hoạt động.");
        }

        // 5. Catalog spec validation
        var specResult = await _catalogClient.ValidateSpecificationAsync(request.EquipmentTypeId, validSchema, validSpecs, cancellationToken);
        if (specResult.IsFailure)
        {
            return specResult.Error;
        }

        // 6. Media preparation (nếu có thumbnail)
        if (validThumb.HasValue)
        {
            var mediaResult = await _mediaService.PreparePublicAsync(validThumb.Value, caller, cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        // 7. Atomic write transaction: tạo Listing Draft và đúng 1 physical asset ban đầu
        var listingId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();

        var listingResult = EquipmentListing.Create(
            listingId,
            caller.UserId.Value,
            request.EquipmentTypeId,
            request.BrandId,
            validModel,
            validSchema,
            validSpecs,
            request.RentalRate.DailyRate!.Value,
            request.RentalRate.WeeklyRate,
            request.RentalRate.MonthlyRate,
            request.RentalRate.Currency!,
            now,
            validThumb);

        if (listingResult.IsFailure)
        {
            return listingResult.Error;
        }

        var listing = listingResult.Value;
        var assetId = Guid.NewGuid();
        var assetResult = InventoryAsset.Create(assetId, listingId, normalizedSerial, now);
        if (assetResult.IsFailure)
        {
            return assetResult.Error;
        }

        var asset = assetResult.Value;
        var txResult = await _writeTransaction.ExecuteListingWriteAsync(listingId, (ctx, _) =>
        {
            ctx.Listings.Add(listing);
            ctx.Assets.Add(asset);
            return Task.FromResult(Result.Success(listing));
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return EquipmentListingMapper.ToResponse(listing, 1, _mediaOptions.PublicGatewayUrl);
    }

    /// <inheritdoc />
    public async Task<Result<EquipmentListingResponse>> PatchListingAsync(
        Guid id,
        PatchEquipmentListingRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!caller.IsAuthenticated || !caller.UserId.HasValue)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (request.IsEmpty)
        {
            return Error.Validation("EMPTY_PATCH_REQUEST", "Yêu cầu PATCH phải chứa ít nhất một trường cần cập nhật.");
        }

        var snapshot = await _listingRepository.GetByIdAsync(id, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (snapshot.OwnerUserId != caller.UserId.Value && !caller.IsAdmin)
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền chỉnh sửa Listing của người dùng khác.");
        }

        // Validate Brand nếu gửi
        if (request.IsBrandIdSpecified)
        {
            if (!request.BrandId.HasValue || request.BrandId.Value == Guid.Empty)
            {
                return Error.Validation("INVALID_BRAND_ID", "Brand ID không được để trống hoặc rỗng.");
            }

            var brandLookup = await _catalogClient.GetBrandAsync(request.BrandId.Value, cancellationToken);
            if (brandLookup.IsFailure)
            {
                return brandLookup.Error;
            }

            if (!brandLookup.Value.IsActive)
            {
                return Error.Validation("Brand.Inactive", "Thương hiệu đang ở trạng thái không hoạt động.");
            }
        }

        // Validate Schema / Spec nếu có thay đổi
        int targetSchema = request.IsSchemaVersionSpecified ? (request.SchemaVersion ?? snapshot.SchemaVersion) : snapshot.SchemaVersion;
        string targetSpecs = request.IsSpecValuesSpecified ? (request.SpecValues.HasValue ? request.SpecValues.Value.GetRawText() : snapshot.SpecValues) : snapshot.SpecValues;

        if (request.IsSchemaVersionSpecified || request.IsSpecValuesSpecified)
        {
            var specResult = await _catalogClient.ValidateSpecificationAsync(snapshot.EquipmentTypeId, targetSchema, targetSpecs, cancellationToken);
            if (specResult.IsFailure)
            {
                return specResult.Error;
            }
        }

        // Validate Media nếu thumbnail đổi
        if (request.IsThumbnailMediaIdSpecified && request.ThumbnailMediaId.HasValue && request.ThumbnailMediaId != snapshot.ThumbnailMediaId)
        {
            var mediaResult = await _mediaService.PreparePublicAsync(request.ThumbnailMediaId.Value, caller, cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        // Thực thi transaction với advisory lock trên Listing
        var now = _timeProvider.GetUtcNow();
        var txResult = await _writeTransaction.ExecuteListingWriteAsync(id, async (ctx, ct) =>
        {
            var listing = await ctx.Listings.GetByIdAsync(id, ct);
            if (listing is null)
            {
                return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
            }

            if (listing.OwnerUserId != caller.UserId.Value && !caller.IsAdmin)
            {
                return Error.Forbidden("FORBIDDEN", "Không có quyền chỉnh sửa Listing của người dùng khác.");
            }

            if (listing.Version != snapshot.Version)
            {
                return Error.Conflict("STALE_INVENTORY_WRITE", "Dữ liệu Listing đã được thay đổi; đọc lại trước khi thử lại.");
            }

            // Xử lý status transition nếu có
            if (request.IsStatusSpecified && request.Status.HasValue)
            {
                bool hasAcceptingBookingsUnit = false;
                if (request.Status.Value == EquipmentListingStatus.Active)
                {
                    int totalActiveUnits = await ctx.Listings.GetTotalQuantityAsync(id, ct);
                    hasAcceptingBookingsUnit = totalActiveUnits > 0;
                }

                var statusRes = listing.UpdateStatus(request.Status.Value, hasAcceptingBookingsUnit, now);
                if (statusRes.IsFailure)
                {
                    return statusRes.Error;
                }
            }

            // Xử lý details update
            string targetModel = request.IsModelSpecified ? (request.Model ?? snapshot.Model) : listing.Model;
            Guid? targetThumb = request.IsThumbnailMediaIdSpecified ? request.ThumbnailMediaId : listing.ThumbnailMediaId;

            var detailsRes = listing.UpdateDetails(
                request.IsBrandIdSpecified ? request.BrandId : null,
                targetModel,
                targetThumb,
                targetSchema,
                targetSpecs,
                now);

            if (detailsRes.IsFailure)
            {
                return detailsRes.Error;
            }

            return Result.Success(listing);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        int totalQty = await _listingRepository.GetTotalQuantityAsync(id, cancellationToken);
        return EquipmentListingMapper.ToResponse(txResult.Value, totalQty, _mediaOptions.PublicGatewayUrl);
    }

    /// <inheritdoc />
    public async Task<Result<RentalRateDto>> UpdateRentalRateAsync(
        Guid id,
        RentalRateDto request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!caller.IsAuthenticated || !caller.UserId.HasValue)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        var rateValidation = request.Validate();
        if (rateValidation.IsFailure)
        {
            return rateValidation.Error;
        }

        var snapshot = await _listingRepository.GetByIdAsync(id, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (snapshot.OwnerUserId != caller.UserId.Value && !caller.IsAdmin)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ chủ sở hữu hoặc quản trị viên mới được cập nhật giá thuê.");
        }

        var now = _timeProvider.GetUtcNow();
        var txResult = await _writeTransaction.ExecuteListingWriteAsync(id, async (ctx, ct) =>
        {
            var listing = await ctx.Listings.GetByIdAsync(id, ct);
            if (listing is null)
            {
                return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
            }

            if (listing.OwnerUserId != caller.UserId.Value && !caller.IsAdmin)
            {
                return Error.Forbidden("FORBIDDEN", "Chỉ chủ sở hữu hoặc quản trị viên mới được cập nhật giá thuê.");
            }

            var updateRes = listing.UpdateRentalRate(
                request.DailyRate!.Value,
                request.WeeklyRate,
                request.MonthlyRate,
                request.Currency!,
                now);

            if (updateRes.IsFailure)
            {
                return updateRes.Error;
            }

            return Result.Success(listing);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        var updated = txResult.Value;
        return Result.Success(new RentalRateDto(
            updated.DailyRate,
            updated.WeeklyRate,
            updated.MonthlyRate,
            updated.Currency));
    }

    /// <inheritdoc />
    public async Task<Result<InventoryAssetResponse>> AddAssetAsync(
        Guid id,
        AddListingAssetRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (id == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!caller.IsAuthenticated || !caller.UserId.HasValue)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsAdmin && !caller.Permissions.Contains(InventoryAccessEvaluator.PermissionAssetManage))
        {
            return Error.Forbidden("FORBIDDEN", "Người dùng không có quyền quản lý kho tài sản.");
        }

        if (string.IsNullOrWhiteSpace(request.SerialNumber))
        {
            return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial là bắt buộc và không được để trống.");
        }

        var normalizedSerial = request.SerialNumber.Trim();
        if (normalizedSerial.Length > 100)
        {
            return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial tối đa 100 ký tự.");
        }

        var snapshot = await _listingRepository.GetByIdAsync(id, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (snapshot.OwnerUserId != caller.UserId.Value && !caller.IsAdmin)
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền thêm thiết bị cho Listing của người dùng khác.");
        }

        var now = _timeProvider.GetUtcNow();
        var txResult = await _writeTransaction.ExecuteListingWriteAsync(id, async (ctx, ct) =>
        {
            var listing = await ctx.Listings.GetByIdAsync(id, ct);
            if (listing is null)
            {
                return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
            }

            if (listing.OwnerUserId != caller.UserId.Value && !caller.IsAdmin)
            {
                return Error.Forbidden("FORBIDDEN", "Không có quyền thêm thiết bị cho Listing của người dùng khác.");
            }

            // Kiểm tra serial trùng trong listing (kể cả unit disable)
            bool serialExists = await ctx.Assets.ExistsSerialInListingAsync(id, normalizedSerial, null, ct);
            if (serialExists)
            {
                return Error.Conflict("SERIAL_ALREADY_EXISTS", "Số serial đã tồn tại trong danh mục thiết bị này.");
            }

            var assetId = Guid.NewGuid();
            var assetRes = InventoryAsset.Create(assetId, id, normalizedSerial, now);
            if (assetRes.IsFailure)
            {
                return assetRes.Error;
            }

            var newAsset = assetRes.Value;
            ctx.Assets.Add(newAsset);
            return Result.Success(newAsset);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return Result.Success(InventoryAssetMapper.ToResponse(txResult.Value));
    }
}
