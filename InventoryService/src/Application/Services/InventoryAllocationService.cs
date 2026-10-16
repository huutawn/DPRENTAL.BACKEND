using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Dịch vụ ứng dụng quản lý vòng đời giữ chỗ và phân bổ tài sản (Hold &amp; Allocation Core).
/// Thực thi nguyên tử dưới sự bảo vệ của pg_advisory_xact_lock mức tài sản.
/// </summary>
public sealed partial class InventoryAllocationService : IInventoryAllocationService
{
    private readonly IAssetAllocationRepository _allocationRepository;
    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly TimeProvider _timeProvider;
    private readonly AllocationCommandCoordinator _coordinator;
    public InventoryAllocationService(
        IAssetAllocationRepository allocationRepository,
        IInventoryWriteTransaction writeTransaction,
        TimeProvider? timeProvider = null)
        : this(allocationRepository, writeTransaction, null, timeProvider)
    {
    }

    public InventoryAllocationService(
        IAssetAllocationRepository allocationRepository,
        IInventoryWriteTransaction writeTransaction,
        AllocationCommandCoordinator? coordinator,
        TimeProvider? timeProvider = null)
    {
        _allocationRepository = allocationRepository ?? throw new ArgumentNullException(nameof(allocationRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _coordinator = coordinator ?? new AllocationCommandCoordinator(writeTransaction, _timeProvider);
    }

    /// <inheritdoc />
    public async Task<Result<CreateHoldResult>> CreateHoldAsync(
        Guid assetId,
        CreateHoldRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        // 1. Authorization: Fail-closed trước khi đọc dữ liệu
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!InventoryAccessEvaluator.CanWriteAllocations(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ service có quyền allocation.write mới được tạo giữ chỗ tài sản.");
        }

        // 2. Validate cấu trúc request và chuẩn hóa canonical expiry
        var validationResult = ValidateCreateHold(request);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var (period, canonicalHoldExpiresAt) = validationResult.Value;

        // 3. Thực thi atomic write dưới advisory lock mức tài sản
        var executeResult = await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
        {
            // a. Đọc lại asset sau advisory lock
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset is null)
            {
                return Error.NotFound("ASSET_NOT_FOUND", $"Tài sản với ID {assetId} không tồn tại.");
            }

            // b. Lấy clock từ TimeProvider sau advisory lock
            var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

            // c. Tra business key bằng repository hiện có (Replay trước điều kiện tạo mới)
            var existing = await ctx.Allocations.GetByExternalReferenceAsync(
                request.SourceSystem, request.ExternalReference, ct);

            if (existing is not null)
            {
                return TryReplayExistingHold(existing, assetId, period, canonicalHoldExpiresAt, request.Notes);
            }

            // d. Kiểm tra trạng thái Listing cha: Bắt buộc Active để nhận giữ chỗ mới
            var listing = asset.Listing ?? await ctx.Listings.GetByIdAsync(asset.EquipmentListingId, ct);
            if (listing is null)
            {
                return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
            }

            if (listing.Status != EquipmentListingStatus.Active)
            {
                return Error.Conflict("LISTING_NOT_ACTIVE", "Listing không ở trạng thái Active để nhận giữ chỗ mới.");
            }

            // e. Với key mới: validate bằng CreateHold với canonical expiry
            var holdId = Guid.NewGuid();
            var holdResult = AssetAllocation.CreateHold(
                id: holdId,
                assetId: assetId,
                period: period,
                holdExpiresAt: canonicalHoldExpiresAt,
                now: nowUtc,
                sourceSystem: request.SourceSystem,
                externalReference: request.ExternalReference,
                notes: request.Notes,
                actor: "system");

            if (holdResult.IsFailure)
            {
                return holdResult.Error;
            }

            var hold = holdResult.Value;

            // e. Tải overlapping blocks/candidate allocations và kiểm tra availability
            var overlappingBlocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(
                assetId, period.StartDate, period.EndDate, ct);
            var candidateAllocations = await ctx.Allocations.GetActiveCandidatesAsync(
                assetId, period.StartDate, period.EndDate, ct);

            var availResult = CheckAvailabilitySanitized(asset, period, nowUtc, candidateAllocations, overlappingBlocks);
            if (availResult.IsFailure)
            {
                return availResult.Error;
            }

            // f. Stage aggregate cùng transition tạo Hold
            ctx.Allocations.Add(hold);

            // g. Stage availability Outbox message
            var outboxMessage = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(assetId, nowUtc);
            ctx.StageOutbox(outboxMessage);
            ctx.StageCacheInvalidation([$"asset:{assetId:D}", $"listing:{asset.EquipmentListingId:D}", "public-discovery"]);

            return Result.Success(new CreateHoldResult(InventoryAllocationMapper.ToResponse(hold), IsReplay: false));
        }, cancellationToken);

        // 4. Xử lý race condition trên unique constraint giữa hai asset khác nhau:
        // Sau rollback, thực hiện đúng một transaction đối chiếu lại dưới cùng asset lock.
        if (executeResult.IsFailure && executeResult.Error.Code == "INVENTORY_DUPLICATE_KEY")
        {
            var retryLookupResult = await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
            {
                var existing = await ctx.Allocations.GetByExternalReferenceAsync(
                    request.SourceSystem, request.ExternalReference, ct);

                if (existing is null)
                {
                    return executeResult.Error;
                }

                return TryReplayExistingHold(existing, assetId, period, canonicalHoldExpiresAt, request.Notes);
            }, cancellationToken);

            return retryLookupResult;
        }

        return executeResult;
    }

    /// <inheritdoc />
    public async Task<Result<ListingHoldResponse>> CreateListingHoldAsync(
        Guid listingId,
        CreateListingHoldRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!InventoryAccessEvaluator.CanWriteAllocations(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ service có quyền allocation.write mới được tạo giữ chỗ tài sản.");
        }

        if (listingId == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (request.Quantity < 1 || request.Quantity > 500)
        {
            return Error.Validation("INVALID_QUANTITY", "Số lượng giữ chỗ phải từ 1 đến 500.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceSystem) || request.SourceSystem.Trim().Length > 100)
        {
            return Error.Validation("INVALID_SOURCE_SYSTEM", "SourceSystem không được rỗng và tối đa 100 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(request.GroupReference) || request.GroupReference.Trim().Length > 100)
        {
            return Error.Validation("INVALID_GROUP_REFERENCE", "GroupReference không được rỗng và tối đa 100 ký tự.");
        }

        if (request.StartDate == default)
        {
            return Error.Validation("START_DATE_REQUIRED", "Ngày bắt đầu không được để trống.");
        }

        if (request.EndDate == default)
        {
            return Error.Validation("END_DATE_REQUIRED", "Ngày kết thúc không được để trống.");
        }

        if (request.HoldExpiresAt == default)
        {
            return Error.Validation("HOLD_EXPIRES_AT_REQUIRED", "Thời hạn giữ chỗ không được để trống.");
        }

        var periodResult = RentalPeriod.Create(request.StartDate, request.EndDate);
        if (periodResult.IsFailure)
        {
            return periodResult.Error;
        }

        var period = periodResult.Value;
        var canonicalHoldExpiresAt = BusinessDateTime.NormalizeUtc(request.HoldExpiresAt);
        var sourceSystem = request.SourceSystem.Trim();
        var groupReference = request.GroupReference.Trim();
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();

        if (notes?.Length > 500)
        {
            return Error.Validation("INVALID_ALLOCATION_METADATA", "Ghi chú tối đa 500 ký tự.");
        }

        return await _writeTransaction.ExecuteGroupHoldAsync(sourceSystem, groupReference, listingId, async (ctx, ct) =>
        {
            var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

            // 1. Replay check: tra cứu toàn bộ nhóm
            var existingGroup = await ctx.Allocations.GetByGroupReferenceAsync(sourceSystem, groupReference, ct);
            if (existingGroup.Count > 0)
            {
                // Đối chiếu Listing
                if (existingGroup.Any(a => a.Asset.EquipmentListingId != listingId))
                {
                    return Error.Conflict("EXTERNAL_REFERENCE_REUSED", "Mã tham chiếu nhóm đã được sử dụng cho listing khác.");
                }

                // Đối chiếu số lượng
                if (existingGroup.Count != request.Quantity)
                {
                    return Error.Conflict("EXTERNAL_REFERENCE_REUSED", "Số lượng yêu cầu không khớp với nhóm đã tạo.");
                }

                // Đối chiếu khoảng thời gian
                if (existingGroup.Any(a => a.RentalPeriod.StartDate != period.StartDate || a.RentalPeriod.EndDate != period.EndDate))
                {
                    return Error.Conflict("EXTERNAL_REFERENCE_REUSED", "Khoảng thời gian không khớp với nhóm đã tạo.");
                }

                // Đối chiếu hạn giữ chỗ canonical
                if (existingGroup.Any(a => a.HoldExpiresAt != canonicalHoldExpiresAt))
                {
                    return Error.Conflict("EXTERNAL_REFERENCE_REUSED", "Thời hạn giữ chỗ không khớp với nhóm đã tạo.");
                }

                // Đối chiếu notes
                if (existingGroup.Any(a => !string.Equals(a.Notes, notes, StringComparison.Ordinal)))
                {
                    return Error.Conflict("EXTERNAL_REFERENCE_REUSED", "Ghi chú không khớp với nhóm đã tạo.");
                }

                // Kiểm tra trạng thái: không hồi sinh nhóm đã hết hạn/released/cancelled khi retry
                if (existingGroup.Any(a => a.Status is AllocationStatus.Released or AllocationStatus.Cancelled ||
                    (a.Status == AllocationStatus.Held && nowUtc >= a.HoldExpiresAt)))
                {
                    return Error.Conflict("HOLD_EXPIRED", "Lượt giữ chỗ nhóm đã hết hạn hoặc đã bị giải phóng.");
                }

                // Idempotent replay: trả lại các allocations hiện tại
                var existingResponses = existingGroup.Select(InventoryAllocationMapper.ToResponse).ToList();
                return Result.Success(new ListingHoldResponse(
                    listingId,
                    sourceSystem,
                    groupReference,
                    existingGroup.Count,
                    existingResponses,
                    IsReplay: true));
            }

            // 2. Validate thời hạn giữ chỗ cho request mới
            var startOfDayUtc = BusinessDateTime.StartOfDayUtc(period.StartDate);
            if (canonicalHoldExpiresAt <= nowUtc)
            {
                return Error.Validation("HOLD_EXPIRES_IN_PAST", "Thời hạn giữ chỗ phải lớn hơn thời điểm hiện tại.");
            }

            if (canonicalHoldExpiresAt >= startOfDayUtc)
            {
                return Error.Validation("HOLD_EXPIRES_AFTER_START", "Thời hạn giữ chỗ phải trước 00:00 ngày bắt đầu theo giờ Việt Nam.");
            }

            var holdCoreResult = await ExecuteCreateGroupHoldCoreAsync(
                ctx,
                listingId,
                request.Quantity,
                period,
                canonicalHoldExpiresAt,
                sourceSystem,
                groupReference,
                operationKey: null,
                notes: notes,
                nowUtc: nowUtc,
                ct: ct);

            if (holdCoreResult.IsFailure)
            {
                return holdCoreResult.Error;
            }

            var (createdAllocations, _) = holdCoreResult.Value;
            var responses = createdAllocations.Select(InventoryAllocationMapper.ToResponse).ToList();
            return Result.Success(new ListingHoldResponse(
                listingId,
                sourceSystem,
                groupReference,
                request.Quantity,
                responses,
                IsReplay: false));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<AllocationResponse>> AllocateAsync(
        Guid allocationId,
        AllocateRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        // 1. Authorization: Fail-closed trước khi đọc dữ liệu
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!InventoryAccessEvaluator.CanWriteAllocations(caller))
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ service có quyền allocation.write mới được phân bổ tài sản.");
        }

        // 2. Validate request
        var validationResult = ValidateAllocate(request);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        // 3. Đọc allocation để lấy riêng AssetId; 404 nếu thiếu
        var initialAllocation = await _allocationRepository.GetByIdAsync(allocationId, cancellationToken);
        if (initialAllocation is null)
        {
            return Error.NotFound("ALLOCATION_NOT_FOUND", $"Lượt phân bổ với ID {allocationId} không tồn tại.");
        }

        var assetId = initialAllocation.AssetId;

        // 4. Acquire asset lock qua ExecuteAsync
        return await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
        {
            // 5. Đọc lại allocation cùng transitions trong transaction sau advisory lock
            var allocation = await ctx.Allocations.GetByIdAsync(allocationId, ct);
            if (allocation is null)
            {
                return Error.NotFound("ALLOCATION_NOT_FOUND", $"Lượt phân bổ với ID {allocationId} không tồn tại.");
            }

            // 6. Lấy clock sau khi có lock
            var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

            // 7. Gọi Allocate với operation key (domain kiểm tra replay trước state/expiry)
            var allocateResult = allocation.Allocate(nowUtc, actor: "system", operationKey: request.OperationKey.Trim());
            if (allocateResult.IsFailure)
            {
                var error = allocateResult.Error;
                // Command conflict decision thuộc Application layer
                if (error.Code is "HOLD_EXPIRED" or "INVALID_STATUS_TRANSITION")
                {
                    return Error.Conflict(error.Code, error.Message);
                }

                return error;
            }

            // Lưu thay đổi bằng aggregate đang được EF tracking; không gọi ctx.Allocations.Update!
            // Không query overlap lại.
            // Không kiểm tra AcceptingBookings.
            // Không phát availability event.
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset != null)
            {
                ctx.StageCacheInvalidation([$"asset:{assetId:D}", $"listing:{asset.EquipmentListingId:D}", "public-discovery"]);
            }

            return Result.Success(InventoryAllocationMapper.ToResponse(allocation));
        }, cancellationToken);
    }

    private static Result<(RentalPeriod Period, DateTimeOffset CanonicalHoldExpiresAt)> ValidateCreateHold(CreateHoldRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceSystem))
        {
            return Error.Validation("SOURCE_SYSTEM_REQUIRED", "Hệ thống nguồn không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(request.ExternalReference))
        {
            return Error.Validation("EXTERNAL_REFERENCE_REQUIRED", "Mã tham chiếu không được để trống.");
        }
        if (request.ExternalReference.Trim().StartsWith("listing-hold:", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("RESERVED_EXTERNAL_REFERENCE", "Tiền tố 'listing-hold:' chỉ dành riêng cho group hold.");
        }


        if (request.StartDate == default)
        {
            return Error.Validation("START_DATE_REQUIRED", "Ngày bắt đầu không được để trống.");
        }

        if (request.EndDate == default)
        {
            return Error.Validation("END_DATE_REQUIRED", "Ngày kết thúc không được để trống.");
        }

        if (request.HoldExpiresAt == default)
        {
            return Error.Validation("HOLD_EXPIRES_AT_REQUIRED", "Thời hạn giữ chỗ không được để trống.");
        }

        var periodResult = RentalPeriod.Create(request.StartDate, request.EndDate);
        if (periodResult.IsFailure)
        {
            return periodResult.Error;
        }

        var canonicalHoldExpiresAt = BusinessDateTime.NormalizeUtc(request.HoldExpiresAt);
        return (periodResult.Value, canonicalHoldExpiresAt);
    }

    private static Result ValidateAllocate(AllocateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationKey) || request.OperationKey.Trim().Length > 100)
        {
            return Error.Validation("INVALID_OPERATION_KEY", "Operation key không hợp lệ.");
        }

        return Result.Success();
    }

    private static Result<CreateHoldResult> TryReplayExistingHold(
        AssetAllocation existing,
        Guid assetId,
        RentalPeriod period,
        DateTimeOffset canonicalHoldExpiresAt,
        string? notes)
    {
        var retryCheck = existing.ValidateExternalRetry(assetId, period, canonicalHoldExpiresAt, notes);
        if (retryCheck.IsFailure)
        {
            return retryCheck.Error;
        }

        return Result.Success(new CreateHoldResult(InventoryAllocationMapper.ToResponse(existing), IsReplay: true));
    }

    private static Result CheckAvailabilitySanitized(
        InventoryAsset asset,
        RentalPeriod period,
        DateTimeOffset nowUtc,
        IEnumerable<AssetAllocation> candidateAllocations,
        IEnumerable<CalendarBlock> overlappingBlocks)
    {
        var availResult = AssetAvailabilityPolicy.CheckAvailability(
            asset, period, nowUtc, candidateAllocations, overlappingBlocks);

        if (availResult.IsFailure)
        {
            if (availResult.Error.Code == "ASSET_BLOCKED")
            {
                return Error.Conflict("ASSET_BLOCKED", "Tài sản đã có lịch chặn trong khoảng thời gian yêu cầu.");
            }

            if (availResult.Error.Code == "ASSET_UNAVAILABLE")
            {
                return Error.Conflict("ASSET_UNAVAILABLE", "Tài sản không khả dụng trong khoảng thời gian yêu cầu.");
            }

            return availResult.Error;
        }

        return Result.Success();
    }
}
