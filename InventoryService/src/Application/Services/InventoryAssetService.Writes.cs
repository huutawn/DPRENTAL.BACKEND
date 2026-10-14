using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Services;

public sealed partial class InventoryAssetService
{
    /// <summary>
    /// Cập nhật thông tin vật lý của đơn vị thiết bị (serial number, năm sản xuất).
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
            return Error.Validation("EMPTY_PATCH_REQUEST", "Yêu cầu PATCH phải chứa ít nhất một trường cần cập nhật.");
        }

        var snapshot = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanManageAsset(caller, snapshot.Listing.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền chỉnh sửa tài sản này.");
        }

        if (request.IsSerialNumberSpecified && string.IsNullOrWhiteSpace(request.SerialNumber))
        {
            return Error.Validation("INVALID_SERIAL_NUMBER", "Số serial là bắt buộc và không được để trống.");
        }

        var targetSerial = request.IsSerialNumberSpecified ? request.SerialNumber!.Trim() : snapshot.SerialNumber;
        var targetYear = request.IsManufactureYearSpecified ? request.ManufactureYear : snapshot.ManufactureYear;

        var now = _timeProvider.GetUtcNow();
        var validation = InventoryAsset.ValidatePhysicalDetails(targetSerial, targetYear, now);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var txResult = await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
        {
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset is null)
            {
                return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
            }

            if (!InventoryAccessEvaluator.CanManageAsset(caller, asset.Listing.OwnerUserId))
            {
                return Error.Forbidden("FORBIDDEN", "Không có quyền chỉnh sửa tài sản này.");
            }

            if (asset.Version != snapshot.Version)
            {
                return Error.Conflict("STALE_INVENTORY_WRITE", "Dữ liệu tài sản đã được thay đổi; đọc lại trước khi thử lại.");
            }

            if (request.IsSerialNumberSpecified)
            {
                bool serialExists = await ctx.Assets.ExistsSerialInListingAsync(
                    asset.EquipmentListingId,
                    targetSerial,
                    excludeAssetId: assetId,
                    ct);

                if (serialExists)
                {
                    return Error.Conflict("SERIAL_ALREADY_EXISTS", "Số serial đã tồn tại trong danh mục thiết bị này.");
                }
            }

            var updateResult = asset.UpdatePhysicalDetails(targetSerial, targetYear, now);
            if (updateResult.IsFailure)
            {
                return updateResult.Error;
            }
            var revAsset = await ctx.GetNextSearchRevisionAsync(ct);
            ctx.StageOutbox(SearchProjectionEvents.CreateAssetProjectionOutboxMessage(asset.Id, "Upsert", revAsset, now, ctx.SearchBackfillRunId));
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

        if (!InventoryAccessEvaluator.CanManageAsset(caller, snapshot.Listing.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Không có quyền quản lý nhận lịch của tài sản này.");
        }

        var now = _timeProvider.GetUtcNow();
        var txResult = await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
        {
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset is null)
            {
                return Error.NotFound("ASSET_NOT_FOUND", "Tài sản không tồn tại.");
            }

            if (!InventoryAccessEvaluator.CanManageAsset(caller, asset.Listing.OwnerUserId))
            {
                return Error.Forbidden("FORBIDDEN", "Không có quyền quản lý nhận lịch của tài sản này.");
            }

            bool previousAcceptingBookings = asset.AcceptingBookings;
            Result toggleResult;

            if (request.AcceptingBookings)
            {
                toggleResult = asset.EnableBookings(now);
            }
            else
            {
                toggleResult = asset.DisableBookings(request.Reason, now);
            }

            if (toggleResult.IsFailure)
            {
                return toggleResult.Error;
            }

            // Chỉ phát sự kiện Availability khi trạng thái thực sự thay đổi
            if (previousAcceptingBookings != asset.AcceptingBookings)
            {
                var outboxMessage = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(asset.Id, now);
                ctx.StageOutbox(outboxMessage);
            }

            var revAsset = await ctx.GetNextSearchRevisionAsync(ct);
            var revListing = await ctx.GetNextSearchRevisionAsync(ct);
            ctx.StageOutbox(SearchProjectionEvents.CreateAssetProjectionOutboxMessage(asset.Id, "Upsert", revAsset, now, ctx.SearchBackfillRunId));
            ctx.StageOutbox(SearchProjectionEvents.CreateListingProjectionOutboxMessage(asset.EquipmentListingId, "Upsert", revListing, false, now, ctx.SearchBackfillRunId));

            return Result.Success(asset);
        }, cancellationToken);

        if (txResult.IsFailure)
        {
            return txResult.Error;
        }

        return Result.Success(InventoryAssetMapper.ToResponse(txResult.Value));
    }
}
