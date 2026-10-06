using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Domain.Kafka.Outbox;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
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
public sealed class InventoryAllocationService : IInventoryAllocationService
{
    private readonly IAssetAllocationRepository _allocationRepository;
    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly TimeProvider _timeProvider;

    public InventoryAllocationService(
        IAssetAllocationRepository allocationRepository,
        IInventoryWriteTransaction writeTransaction,
        TimeProvider? timeProvider = null)
    {
        _allocationRepository = allocationRepository ?? throw new ArgumentNullException(nameof(allocationRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            // d. Với key mới: validate bằng CreateHold với canonical expiry
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
