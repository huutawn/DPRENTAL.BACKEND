using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Application.Kafka.Outbox;

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
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!InventoryAccessEvaluator.CanCreateListing(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Người dùng không có quyền quản lý kho tài sản.");
        }
        // 2. Local validation
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error.Validation("INVALID_NAME", "Tên thiết bị không được để trống.");
        }

        var trimmedName = request.Name.Trim();
        if (trimmedName.Length > 200)
        {
            return Error.Validation("INVALID_NAME", "Tên thiết bị tối đa 200 ký tự.");
        }

        string? trimmedDescription = null;
        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            var trimmedDesc = request.Description.Trim();
            if (trimmedDesc.Length > 2000)
            {
                return Error.Validation("INVALID_DESCRIPTION", "Mô tả tối đa 2.000 ký tự.");
            }
            trimmedDescription = trimmedDesc;
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

        if (request.EquipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }

        if (request.BrandId == Guid.Empty)
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được để trống hoặc rỗng.");
        }

        var now = _timeProvider.GetUtcNow();
        var yearValidation = EquipmentListing.ValidateManufactureYear(request.ManufactureYear, now);
        if (yearValidation.IsFailure)
        {
            return yearValidation.Error;
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
            trimmedName,
            trimmedDescription,
            request.Model,
            request.MediaIds,
            request.ThumbnailMediaId,
            request.SchemaVersion,
            specValuesJson);

        if (detailsValidation.IsFailure)
        {
            return detailsValidation.Error;
        }

        var (validName, validDesc, validModel, validMediaIds, validThumb, validSchema, validSpecs) = detailsValidation.Value;

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

        // 6. Media preparation (xác minh danh sách media và thumbnail ngoài transaction)
        if (validMediaIds.Length > 0 || validThumb.HasValue)
        {
            var mediaResult = await _mediaService.PreparePublicAsync(
                validMediaIds,
                validThumb,
                caller,
                cancellationToken: cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        // 7. Atomic write transaction: tạo Listing Draft và đúng 1 physical asset ban đầu kế thừa năm sản xuất của Listing
        var listingId = Guid.NewGuid();
        now = _timeProvider.GetUtcNow();

        var listingResult = EquipmentListing.Create(
            listingId,
            caller.UserId!.Value,
            request.EquipmentTypeId,
            request.BrandId,
            validName,
            validDesc,
            validModel,
            validSchema,
            validSpecs,
            request.RentalRate.DailyRate!.Value,
            request.RentalRate.WeeklyRate,
            request.RentalRate.MonthlyRate,
            request.RentalRate.Currency!,
            now,
            yearValidation.Value,
            validMediaIds,
            validThumb);

        if (listingResult.IsFailure)
        {
            return listingResult.Error;
        }

        var listing = listingResult.Value;
        var assetId = Guid.NewGuid();
        var assetResult = InventoryAsset.Create(assetId, listingId, normalizedSerial, now, listing.ManufactureYear);
        if (assetResult.IsFailure)
        {
            return assetResult.Error;
        }
        var asset = assetResult.Value;
        var txResult = await _writeTransaction.ExecuteListingWriteAsync(listingId, async (ctx, ct) =>
        {
            ctx.Listings.Add(listing);
            ctx.Assets.Add(asset);
            var revListing = await ctx.GetNextSearchRevisionAsync(ct);
            var revAsset = await ctx.GetNextSearchRevisionAsync(ct);
            ctx.StageOutbox(SearchProjectionEvents.CreateListingProjectionOutboxMessage(listing.Id, "Upsert", revListing, false, now, ctx.SearchBackfillRunId));
            ctx.StageOutbox(SearchProjectionEvents.CreateAssetProjectionOutboxMessage(asset.Id, "Upsert", revAsset, now, ctx.SearchBackfillRunId));
            return Result.Success(listing);
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

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có quyền chỉnh sửa Listing.");
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

        if (!InventoryAccessEvaluator.CanViewOrEditListing(caller, snapshot.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền chỉnh sửa Listing của người dùng khác.");
        }
        if (request.IsNameSpecified)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Error.Validation("INVALID_NAME", "Tên thiết bị không được để trống.");
            }

            if (request.Name.Trim().Length > 200)
            {
                return Error.Validation("INVALID_NAME", "Tên thiết bị tối đa 200 ký tự.");
            }
        }

        if (request.IsDescriptionSpecified && request.Description is not null)
        {
            if (request.Description.Trim().Length > 2000)
            {
                return Error.Validation("INVALID_DESCRIPTION", "Mô tả tối đa 2.000 ký tự.");
            }
        }

        if (request.IsBrandIdSpecified && (!request.BrandId.HasValue || request.BrandId.Value == Guid.Empty))
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được để trống hoặc rỗng.");
        }

        if (request.IsModelSpecified && request.Model is null)
        {
            return Error.Validation("INVALID_MODEL", "Model không được để trống.");
        }

        var preflightNow = _timeProvider.GetUtcNow();
        if (request.IsManufactureYearSpecified)
        {
            var yearValidation = EquipmentListing.ValidateManufactureYear(request.ManufactureYear, preflightNow);
            if (yearValidation.IsFailure)
            {
                return yearValidation.Error;
            }
        }

        if (request.IsMediaIdsSpecified && request.MediaIds is null)
        {
            return Error.Validation("INVALID_MEDIA_IDS", "Danh sách media (mediaIds) không được là null.");
        }

        if (request.IsSchemaVersionSpecified && !request.SchemaVersion.HasValue)
        {
            return Error.Validation("INVALID_SCHEMA_VERSION", "Schema Version phải là số nguyên dương lớn hơn hoặc bằng 1.");
        }

        if (request.IsSpecValuesSpecified && !request.SpecValues.HasValue)
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values không được để trống.");
        }

        if (request.IsStatusSpecified && !request.Status.HasValue)
        {
            return Error.Validation("INVALID_STATUS", "Trạng thái không được để trống.");
        }

        if (request.IsThumbnailMediaIdSpecified && request.ThumbnailMediaId.HasValue && request.ThumbnailMediaId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_THUMBNAIL_MEDIA_ID", "Thumbnail Media ID không được rỗng.");
        }

        // 2. Merge snapshot với các field được truyền (omitted giữ nguyên)
        string targetName = request.IsNameSpecified ? request.Name!.Trim() : snapshot.Name;
        string? targetDescription = request.IsDescriptionSpecified
            ? (string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim())
            : snapshot.Description;
        string targetModel = request.IsModelSpecified ? request.Model! : snapshot.Model;
        int? targetManufactureYear = request.IsManufactureYearSpecified ? request.ManufactureYear : snapshot.ManufactureYear;
        IReadOnlyList<Guid> targetMediaIds = request.IsMediaIdsSpecified ? request.MediaIds! : snapshot.MediaIds;
        Guid? targetThumb = request.IsThumbnailMediaIdSpecified ? request.ThumbnailMediaId : snapshot.ThumbnailMediaId;
        int targetSchema = request.IsSchemaVersionSpecified ? request.SchemaVersion!.Value : snapshot.SchemaVersion;
        string targetSpecs = request.IsSpecValuesSpecified ? request.SpecValues!.Value.GetRawText() : snapshot.SpecValues;

        // 3. Domain validation cục bộ TRƯỚC KHI gọi Catalog / Media
        var detailsValidation = EquipmentListing.ValidateDetails(
            targetName,
            targetDescription,
            targetModel,
            targetMediaIds,
            targetThumb,
            targetSchema,
            targetSpecs);
        if (detailsValidation.IsFailure)
        {
            return detailsValidation.Error;
        }

        var (validName, validDesc, validModel, validMediaIds, validThumb, validSchema, validSpecs) = detailsValidation.Value;

        // 4. Downstream validation: Catalog Brand
        if (request.IsBrandIdSpecified)
        {
            var brandLookup = await _catalogClient.GetBrandAsync(request.BrandId!.Value, cancellationToken);
            if (brandLookup.IsFailure)
            {
                return brandLookup.Error;
            }

            if (!brandLookup.Value.IsActive)
            {
                return Error.Validation("Brand.Inactive", "Thương hiệu đang ở trạng thái không hoạt động.");
            }
        }

        // 5. Downstream validation: Catalog Specification
        if (request.IsSchemaVersionSpecified || request.IsSpecValuesSpecified)
        {
            var specResult = await _catalogClient.ValidateSpecificationAsync(snapshot.EquipmentTypeId, validSchema, validSpecs, cancellationToken);
            if (specResult.IsFailure)
            {
                return specResult.Error;
            }
        }

        // 6. Media preparation ngoài transaction (xác minh media mới và thumbnail được chọn)
        if (request.IsMediaIdsSpecified || request.IsThumbnailMediaIdSpecified)
        {
            var mediaResult = await _mediaService.PreparePublicAsync(
                validMediaIds,
                validThumb,
                caller,
                snapshot.MediaIds,
                snapshot.ThumbnailMediaId,
                cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        // 7. Thực thi transaction với advisory lock trên Listing
        var txResult = await _writeTransaction.ExecuteListingWriteAsync(id, async (ctx, ct) =>
        {
            var listing = await ctx.Listings.GetByIdAsync(id, ct);
            if (listing is null)
            {
                return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
            }

            if (!InventoryAccessEvaluator.CanViewOrEditListing(caller, listing.OwnerUserId))
            {
                return Error.Forbidden("FORBIDDEN", "Không có quyền chỉnh sửa Listing của người dùng khác.");
            }

            if (listing.Version != snapshot.Version)
            {
                return Error.Conflict("STALE_INVENTORY_WRITE", "Dữ liệu Listing đã được thay đổi; đọc lại trước khi thử lại.");
            }

            var now = _timeProvider.GetUtcNow();

            // Xử lý status transition nếu có
            if (request.IsStatusSpecified)
            {
                bool hasAcceptingBookingsUnit = false;
                if (request.Status!.Value == EquipmentListingStatus.Active)
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
            var detailsRes = listing.UpdateDetails(
                request.IsBrandIdSpecified ? request.BrandId : null,
                validName,
                validDesc,
                validModel,
                request.IsManufactureYearSpecified ? targetManufactureYear : null,
                validMediaIds,
                validThumb,
                validSchema,
                validSpecs,
                now);

            if (detailsRes.IsFailure)
            {
                return detailsRes.Error;
            }

            var revision = await ctx.GetNextSearchRevisionAsync(ct);
            bool needsFanout = request.IsBrandIdSpecified || request.IsModelSpecified || request.IsManufactureYearSpecified;
            ctx.StageOutbox(SearchProjectionEvents.CreateListingProjectionOutboxMessage(listing.Id, "Upsert", revision, needsFanout, now, ctx.SearchBackfillRunId));

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

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có quyền cập nhật giá thuê.");
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

        if (!InventoryAccessEvaluator.CanUpdateListingRentalRate(caller, snapshot.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ chủ sở hữu hoặc quản trị viên mới được cập nhật giá thuê.");
        }

        var txResult = await _writeTransaction.ExecuteListingWriteAsync(id, async (ctx, ct) =>
        {
            var listing = await ctx.Listings.GetByIdAsync(id, ct);
            if (listing is null)
            {
                return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
            }

            if (!InventoryAccessEvaluator.CanUpdateListingRentalRate(caller, listing.OwnerUserId))
            {
                return Error.Forbidden("FORBIDDEN", "Chỉ chủ sở hữu hoặc quản trị viên mới được cập nhật giá thuê.");
            }

            if (listing.Version != snapshot.Version)
            {
                return Error.Conflict("STALE_INVENTORY_WRITE", "Dữ liệu Listing đã được thay đổi; đọc lại trước khi thử lại.");
            }

            var now = _timeProvider.GetUtcNow();
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

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có quyền thêm thiết bị cho Listing.");
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

        if (!InventoryAccessEvaluator.CanAddListingAsset(caller, snapshot.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền thêm thiết bị cho Listing của người dùng khác.");
        }
        var txResult = await _writeTransaction.ExecuteListingWriteAsync(id, async (ctx, ct) =>
        {
            var listing = await ctx.Listings.GetByIdAsync(id, ct);
            if (listing is null)
            {
                return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
            }

            if (!InventoryAccessEvaluator.CanAddListingAsset(caller, listing.OwnerUserId))
            {
                return Error.Forbidden("FORBIDDEN", "Không có quyền thêm thiết bị cho Listing của người dùng khác.");
            }

            var now = _timeProvider.GetUtcNow();
            var yearValidation = EquipmentListing.ValidateManufactureYear(listing.ManufactureYear, now);
            if (yearValidation.IsFailure)
            {
                return yearValidation.Error;
            }

            bool serialExists = await ctx.Assets.ExistsSerialInListingAsync(id, normalizedSerial, null, ct);
            if (serialExists)
            {
                return Error.Conflict("SERIAL_ALREADY_EXISTS", "Số serial đã tồn tại trong danh mục thiết bị này.");
            }

            var assetId = Guid.NewGuid();
            var assetRes = InventoryAsset.Create(assetId, id, normalizedSerial, now, yearValidation.Value);
            if (assetRes.IsFailure)
            {
                return assetRes.Error;
            }

            var newAsset = assetRes.Value;
            ctx.Assets.Add(newAsset);
            var revAsset = await ctx.GetNextSearchRevisionAsync(ct);
            var revListing = await ctx.GetNextSearchRevisionAsync(ct);
            ctx.StageOutbox(SearchProjectionEvents.CreateAssetProjectionOutboxMessage(newAsset.Id, "Upsert", revAsset, now, ctx.SearchBackfillRunId));
            ctx.StageOutbox(SearchProjectionEvents.CreateListingProjectionOutboxMessage(listing.Id, "Upsert", revListing, false, now, ctx.SearchBackfillRunId));
            return Result.Success(newAsset);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return Result.Success(InventoryAssetMapper.ToResponse(txResult.Value));
    }
}
