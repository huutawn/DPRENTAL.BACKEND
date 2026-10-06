using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Domain.Kafka.Outbox;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Services;

public sealed partial class InventoryAssetService
{
    /// <summary>
    /// Tạo mới tài sản trong kho thuộc sở hữu của người dùng hiện tại (JWT sub).
    /// </summary>
    public async Task<Result<InventoryAssetResponse>> CreateAssetAsync(
        CreateInventoryAssetRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        // 1. Authentication và permission check
        var authResult = AuthorizeManager(caller);
        if (authResult.IsFailure)
        {
            return authResult.Error;
        }

        // 2. Local validation
        if (request.EquipmentTypeId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", "Equipment Type ID không được rỗng.");
        }

        if (!request.BrandId.HasValue || request.BrandId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_BRAND_ID", "Brand ID không được để trống hoặc rỗng.");
        }

        if (request.SpecValues.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation("INVALID_SPEC_VALUES", "Spec values phải là JSON object.");
        }

        var specValuesJson = request.SpecValues.GetRawText();
        var now = _timeProvider.GetUtcNow();

        var validation = InventoryAsset.ValidateDetails(
            request.Model,
            request.SerialNumber,
            request.ManufactureYear,
            request.ThumbnailMediaId,
            request.SchemaVersion,
            specValuesJson,
            now);

        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var (validModel, validSerial, validYear, validThumb, validSchema, validSpecs) = validation.Value;

        // 3. Published EquipmentType check
        var catLookup = await _catalogClient.GetPublishedEquipmentTypeAsync(request.EquipmentTypeId, cancellationToken);
        if (catLookup.IsFailure)
        {
            return catLookup.Error;
        }

        // 4. Active Brand check
        var brandLookup = await _catalogClient.GetBrandAsync(request.BrandId.Value, cancellationToken);
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
        // 6. Chuẩn bị Thumbnail Media
        if (validThumb.HasValue)
        {
            var mediaResult = await _mediaService.PreparePublicAsync(validThumb.Value, caller, cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        // 7. Write transaction
        var assetId = Guid.NewGuid();
        var assetResult = InventoryAsset.Create(
            assetId,
            caller.UserId!.Value,
            request.EquipmentTypeId,
            request.BrandId.Value,
            validModel,
            validSchema,
            validSpecs,
            now,
            validSerial,
            validYear,
            validThumb);

        if (assetResult.IsFailure)
        {
            return assetResult.Error;
        }

        var txResult = await _writeTransaction.ExecuteAsync(assetId, (ctx, _) =>
        {
            ctx.Assets.Add(assetResult.Value);
            return Task.FromResult(Result.Success(assetResult.Value));
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return Result.Success(InventoryAssetMapper.ToResponse(txResult.Value));
    }

    /// <summary>
    /// Cập nhật thông tin chi tiết tài sản theo chuẩn PATCH presence tracking.
    /// Thứ tự: auth/ownership -> merge & domain validation -> validate Brand nếu gửi -> validate schema/spec nếu gửi một trong hai -> media preparation -> write transaction.
    /// </summary>
    public async Task<Result<InventoryAssetResponse>> PatchAssetAsync(
        Guid assetId,
        PatchInventoryAssetRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var callerCheck = CheckAuthenticatedHuman(caller);
        if (callerCheck.IsFailure)
        {
            return callerCheck.Error;
        }

        if (request.IsEmpty)
        {
            return Error.Validation("EMPTY_PATCH", "PATCH request không được để trống.");
        }

        // 1. Snapshot đọc ngoài transaction để authorize và chuẩn bị
        var snapshot = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanManageAsset(caller, snapshot.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền quản lý tài sản này.");
        }

        var snapshotVersion = snapshot.Version;
        var now = _timeProvider.GetUtcNow();

        // 2. Merge target values từ request và snapshot
        var targetBrandId = request.IsBrandIdSpecified ? request.BrandId : snapshot.BrandId;
        var targetModel = request.IsModelSpecified ? (request.Model ?? string.Empty) : snapshot.Model;
        var targetSerial = request.IsSerialNumberSpecified ? request.SerialNumber : snapshot.SerialNumber;
        var targetYear = request.IsManufactureYearSpecified ? request.ManufactureYear : snapshot.ManufactureYear;
        var targetThumb = request.IsThumbnailMediaIdSpecified ? request.ThumbnailMediaId : snapshot.ThumbnailMediaId;
        var targetSchema = request.IsSchemaVersionSpecified ? (request.SchemaVersion ?? 0) : snapshot.SchemaVersion;
        var targetSpecs = request.IsSpecValuesSpecified
            ? (request.SpecValues.HasValue ? request.SpecValues.Value.GetRawText() : string.Empty)
            : snapshot.SpecValues;

        // 3. Domain validation tập trung trước downstream
        if (snapshot.BrandId.HasValue && !targetBrandId.HasValue)
        {
            return Error.Validation("CANNOT_REMOVE_BRAND", "Không thể xóa Brand của tài sản đã có Brand.");
        }

        if (request.IsBrandIdSpecified)
        {
            if (!targetBrandId.HasValue || targetBrandId.Value == Guid.Empty)
            {
                return Error.Validation("INVALID_BRAND_ID", "Brand ID không được rỗng.");
            }
        }

        var validation = InventoryAsset.ValidateDetails(
            targetModel,
            targetSerial,
            targetYear,
            targetThumb,
            targetSchema,
            targetSpecs,
            now);

        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var (validModel, validSerial, validYear, validThumbResult, validSchema, validSpecs) = validation.Value;

        // 4. Validate Brand nếu gửi
        if (request.IsBrandIdSpecified)
        {
            var brandResult = await _catalogClient.GetBrandAsync(targetBrandId!.Value, cancellationToken);
            if (brandResult.IsFailure)
            {
                return brandResult.Error;
            }

            if (!brandResult.Value.IsActive)
            {
                return Error.Validation("Brand.Inactive", "Thương hiệu đang ở trạng thái không hoạt động.");
            }
        }

        // 5. Validate schema/spec nếu gửi một trong hai
        if (request.IsSchemaVersionSpecified || request.IsSpecValuesSpecified)
        {
            var specResult = await _catalogClient.ValidateSpecificationAsync(
                snapshot.EquipmentTypeId,
                validSchema,
                validSpecs,
                cancellationToken);
            if (specResult.IsFailure)
            {
                return specResult.Error;
            }
        }

        // 6. Media preparation nếu thumbnail thay đổi sang một GUID mới khác snapshot
        if (request.IsThumbnailMediaIdSpecified && validThumbResult.HasValue && validThumbResult.Value != snapshot.ThumbnailMediaId)
        {
            var mediaResult = await _mediaService.PreparePublicAsync(validThumbResult.Value, caller, cancellationToken);
            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        // 7. Write transaction: advisory lock, xmin check, persist
        var txResult = await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
        {
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset is null)
            {
                return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
            }

            if (asset.OwnerUserId != snapshot.OwnerUserId)
            {
                return Error.Conflict("CONCURRENCY_CONFLICT", "Ownership của tài sản đã thay đổi.");
            }

            if (asset.Version != snapshotVersion)
            {
                return Error.Conflict("CONCURRENCY_CONFLICT", "Tài sản đã bị thay đổi bởi thao tác khác (xmin conflict).");
            }

            var txNow = _timeProvider.GetUtcNow();
            var updateResult = asset.UpdateDetails(
                targetBrandId,
                validModel,
                validSerial,
                validYear,
                validThumbResult,
                validSchema,
                validSpecs,
                txNow);

            if (updateResult.IsFailure)
            {
                return updateResult.Error;
            }

            ctx.Assets.Update(asset);
            return Result.Success(asset);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return Result.Success(InventoryAssetMapper.ToResponse(txResult.Value));
    }

    /// <summary>
    /// Bật hoặc tắt tính năng nhận đặt lịch của tài sản.
    /// Khi đổi cờ, phát sinh sự kiện InventoryAssetAvailabilityChanged atomically qua outbox.
    /// </summary>
    public async Task<Result<InventoryAssetResponse>> ToggleAcceptingBookingsAsync(
        Guid assetId,
        ToggleAcceptingBookingsRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var callerCheck = CheckAuthenticatedHuman(caller);
        if (callerCheck.IsFailure)
        {
            return callerCheck.Error;
        }

        var snapshot = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanManageAsset(caller, snapshot.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền quản lý tài sản này.");
        }

        var snapshotVersion = snapshot.Version;

        var txResult = await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
        {
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset is null)
            {
                return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
            }

            if (asset.OwnerUserId != snapshot.OwnerUserId)
            {
                return Error.Conflict("CONCURRENCY_CONFLICT", "Ownership của tài sản đã thay đổi.");
            }

            if (asset.Version != snapshotVersion)
            {
                return Error.Conflict("CONCURRENCY_CONFLICT", "Tài sản đã bị thay đổi bởi thao tác khác (xmin conflict).");
            }

            var now = _timeProvider.GetUtcNow();
            bool flagChanged = asset.AcceptingBookings != request.AcceptingBookings;

            if (request.AcceptingBookings)
            {
                asset.EnableBookings(now);
            }
            else
            {
                var disableResult = asset.DisableBookings(request.Reason, now);
                if (disableResult.IsFailure)
                {
                    return disableResult.Error;
                }
            }

            if (flagChanged)
            {
                var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(asset.Id, now);
                ctx.StageOutbox(outboxMsg);
            }

            ctx.Assets.Update(asset);
            return Result.Success(asset);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return Result.Success(InventoryAssetMapper.ToResponse(txResult.Value));
    }
}
