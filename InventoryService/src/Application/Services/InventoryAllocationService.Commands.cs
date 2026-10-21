using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;

namespace Dprental.InventoryService.Application.Services;

public sealed partial class InventoryAllocationService
{
    private const string RentalSourceSystem = "rental";

    /// <summary>
    /// Core use case chọn N asset rảnh và tạo hold allocations theo thứ tự ID tăng dần.
    /// Dùng chung hoàn toàn giữa HTTP endpoint (CreateListingHoldAsync) và Kafka command (ProcessCreateHoldCommandAsync).
    /// </summary>
    public static async Task<Result<(IReadOnlyList<AssetAllocation> Allocations, IReadOnlyList<InventoryAsset> Units)>> ExecuteCreateGroupHoldCoreAsync(
        IInventoryWriteContext ctx,
        Guid listingId,
        int quantity,
        RentalPeriod period,
        DateTimeOffset canonicalHoldExpiresAt,
        string sourceSystem,
        string groupReference,
        string? operationKey,
        string? notes,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        // 1. Khóa Listing cha và kiểm tra trạng thái Active
        await ctx.AcquireListingLockAsync(listingId, ct);
        var listing = await ctx.Listings.GetByIdAsync(listingId, ct);
        if (listing is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (listing.Status != EquipmentListingStatus.Active)
        {
            return Error.Conflict("LISTING_NOT_ACTIVE", "Listing không ở trạng thái Active để nhận giữ chỗ mới.");
        }

        // 2. Tìm N candidate units accepting bookings theo thứ tự Asset ID tăng dần
        var listingSnapshot = await ctx.Listings.GetCalendarSnapshotAsync(listingId, period.StartDate, period.EndDate, ct);
        if (listingSnapshot is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        var freeUnits = listingSnapshot.Assets
            .Where(a => a.AcceptingBookings &&
                !a.CalendarBlocks.Any(cb => cb.StartDate <= period.EndDate && cb.EndDate >= period.StartDate) &&
                !a.Allocations.Any(al =>
                    AssetAvailabilityPolicy.OccupiesCalendar(al, nowUtc) &&
                    AssetAvailabilityPolicy.CheckCalendarOverlap(period.StartDate, period.EndDate, nowUtc, [al], Array.Empty<CalendarBlock>()).IsFailure))
            .OrderBy(a => a.Id)
            .ToList();

        if (freeUnits.Count < quantity)
        {
            return Error.Conflict("INSUFFICIENT_AVAILABILITY", "Không đủ số lượng thiết bị khả dụng trong khoảng thời gian yêu cầu.");
        }

        var chosenUnits = freeUnits.Take(quantity).ToList();

        // 3. Khóa các unit đã chọn theo thứ tự ID tăng dần
        await ctx.AcquireAssetLocksAsync(chosenUnits.Select(u => u.Id), ct);

        // 4. Tạo N allocations + transitions + outbox
        var createdAllocations = new List<AssetAllocation>(quantity);
        foreach (var unit in chosenUnits)
        {
            var unitExtRef = $"listing-hold:{groupReference}:{unit.AssetCode}";
            var holdResult = AssetAllocation.CreateHold(
                id: Guid.NewGuid(),
                assetId: unit.Id,
                period: period,
                holdExpiresAt: canonicalHoldExpiresAt,
                now: nowUtc,
                sourceSystem: sourceSystem,
                externalReference: unitExtRef,
                notes: notes,
                actor: "system",
                operationKey: operationKey,
                groupReference: groupReference);

            if (holdResult.IsFailure)
            {
                return holdResult.Error;
            }

            var hold = holdResult.Value;
            ctx.Allocations.Add(hold);
            createdAllocations.Add(hold);

            var outboxMsg = InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(unit.Id, nowUtc);
            ctx.StageOutbox(outboxMsg);
        }

        var groupScopes = new HashSet<string>(StringComparer.Ordinal) { $"listing:{listingId:D}", "public-discovery" };
        foreach (var unit in chosenUnits)
        {
            groupScopes.Add($"asset:{unit.Id:D}");
        }
        ctx.StageCacheInvalidation(groupScopes);

        return (createdAllocations, chosenUnits);
    }

    /// <inheritdoc />
    public async Task<Result<AllocationCommandOutcome>> ProcessCreateHoldCommandAsync(
        MessageEnvelope<CreateHoldPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");

        var command = envelope.Payload;
        if (command.Quantity < 1 || command.Quantity > 500)
        {
            return Error.Validation("INVALID_QUANTITY", "Số lượng giữ chỗ phải từ 1 đến 500.");
        }

        if (command.RentalOrderId == Guid.Empty || command.RentalItemId == Guid.Empty || command.EquipmentListingId == Guid.Empty)
        {
            return Error.Validation("INVALID_IDENTIFIER", "Order ID, Item ID hoặc Listing ID không hợp lệ.");
        }

        var periodResult = RentalPeriod.Create(command.StartDate, command.EndDate);
        if (periodResult.IsFailure) return periodResult.Error;

        var period = periodResult.Value;
        var canonicalHoldExpiresAt = BusinessDateTime.NormalizeUtc(command.HoldExpiresAt);

        var hashPayload = new
        {
            command.RentalOrderId,
            command.RentalItemId,
            command.EquipmentListingId,
            command.Quantity,
            command.StartDate,
            command.EndDate,
            HoldExpiresAt = canonicalHoldExpiresAt,
            Notes = command.Notes?.Trim()
        };

        return await _coordinator.CoordinateAsync<CreateHoldPayload>(
            AllocationMessageTypes.CreateHold,
            envelope.MessageId,
            envelope.CorrelationId,
            command.RentalOrderId,
            command.RentalItemId,
            command.OperationKey,
            hashPayload,
            handlerName,
            async (ctx, nowUtc, ct) =>
            {
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
                    command.EquipmentListingId,
                    command.Quantity,
                    period,
                    canonicalHoldExpiresAt,
                    RentalSourceSystem,
                    $"rental-item:{command.RentalItemId}",
                    command.OperationKey,
                    command.Notes?.Trim(),
                    nowUtc,
                    ct);

                if (holdCoreResult.IsFailure)
                {
                    return holdCoreResult.Error;
                }

                var (allocations, units) = holdCoreResult.Value;
                var mapping = units.Select((u, idx) => new AllocationAssetMappingPayload(
                    allocations[idx].Id,
                    u.Id,
                    u.AssetCode)).ToList();

                var successPayload = new InventoryAllocationCommandSucceededPayload(
                    command.RentalOrderId,
                    command.RentalItemId,
                    command.OperationKey,
                    AllocationMessageTypes.CreateHold,
                    Status: "Held",
                    HoldExpiresAt: canonicalHoldExpiresAt,
                    Allocations: mapping,
                    OccurredAt: nowUtc);

                return Result.Success(AllocationCommandOutcome.Succeeded(
                    AllocationMessageTypes.CreateHold,
                    command.OperationKey,
                    command.RentalOrderId,
                    command.RentalItemId,
                    successPayload));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<AllocationCommandOutcome>> ProcessExtendHoldCommandAsync(
        MessageEnvelope<ExtendHoldPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");

        var command = envelope.Payload;
        var canonicalNewHoldExpiresAt = BusinessDateTime.NormalizeUtc(command.NewHoldExpiresAt);
        var hashPayload = new
        {
            command.RentalOrderId,
            command.RentalItemId,
            NewHoldExpiresAt = canonicalNewHoldExpiresAt
        };

        return await _coordinator.CoordinateAsync<ExtendHoldPayload>(
            AllocationMessageTypes.ExtendHold,
            envelope.MessageId,
            envelope.CorrelationId,
            command.RentalOrderId,
            command.RentalItemId,
            command.OperationKey,
            hashPayload,
            handlerName,
            async (ctx, nowUtc, ct) =>
            {
                var groupAllocations = await ctx.Allocations.GetByGroupReferenceAsync(RentalSourceSystem, $"rental-item:{command.RentalItemId}", ct);
                if (groupAllocations.Count == 0)
                {
                    return Error.NotFound("GROUP_NOT_FOUND", "Không tìm thấy nhóm phân bổ tương ứng để gia hạn.");
                }

                await ctx.AcquireAssetLocksAsync(groupAllocations.Select(a => a.AssetId), ct);

                foreach (var alloc in groupAllocations)
                {
                    if (alloc.Status != AllocationStatus.Held || (alloc.HoldExpiresAt.HasValue && nowUtc >= alloc.HoldExpiresAt.Value))
                    {
                        return Error.Conflict("HOLD_EXPIRED", "Lượt giữ chỗ đã hết hạn hoặc không ở trạng thái Held.");
                    }

                    var extendRes = alloc.ExtendHold(canonicalNewHoldExpiresAt, nowUtc, "system", command.OperationKey);
                    if (extendRes.IsFailure) return extendRes.Error;
                }
                var extendScopes = new HashSet<string>(StringComparer.Ordinal) { "public-discovery" };
                foreach (var a in groupAllocations)
                {
                    extendScopes.Add($"asset:{a.AssetId:D}");
                    extendScopes.Add($"listing:{a.Asset.EquipmentListingId:D}");
                }
                ctx.StageCacheInvalidation(extendScopes);

                var mapping = groupAllocations.Select(a => new AllocationAssetMappingPayload(
                    a.Id, a.AssetId, a.Asset.AssetCode)).ToList();

                var successPayload = new InventoryAllocationCommandSucceededPayload(
                    command.RentalOrderId,
                    command.RentalItemId,
                    command.OperationKey,
                    AllocationMessageTypes.ExtendHold,
                    Status: "Held",
                    HoldExpiresAt: canonicalNewHoldExpiresAt,
                    Allocations: mapping,
                    OccurredAt: nowUtc);

                return Result.Success(AllocationCommandOutcome.Succeeded(
                    AllocationMessageTypes.ExtendHold,
                    command.OperationKey,
                    command.RentalOrderId,
                    command.RentalItemId,
                    successPayload));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<AllocationCommandOutcome>> ProcessConfirmAllocationCommandAsync(
        MessageEnvelope<ConfirmAllocationPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");

        var command = envelope.Payload;
        var hashPayload = new
        {
            command.RentalOrderId,
            command.RentalItemId
        };

        return await _coordinator.CoordinateAsync<ConfirmAllocationPayload>(
            AllocationMessageTypes.ConfirmAllocation,
            envelope.MessageId,
            envelope.CorrelationId,
            command.RentalOrderId,
            command.RentalItemId,
            command.OperationKey,
            hashPayload,
            handlerName,
            async (ctx, nowUtc, ct) =>
            {
                var groupAllocations = await ctx.Allocations.GetByGroupReferenceAsync(RentalSourceSystem, $"rental-item:{command.RentalItemId}", ct);
                if (groupAllocations.Count == 0)
                {
                    return Error.NotFound("GROUP_NOT_FOUND", "Không tìm thấy nhóm phân bổ tương ứng để xác nhận.");
                }

                await ctx.AcquireAssetLocksAsync(groupAllocations.Select(a => a.AssetId), ct);

                foreach (var alloc in groupAllocations)
                {
                    if (alloc.Status != AllocationStatus.Held || (alloc.HoldExpiresAt.HasValue && nowUtc >= alloc.HoldExpiresAt.Value))
                    {
                        return Error.Conflict("HOLD_EXPIRED", "Lượt giữ chỗ đã hết hạn hoặc không ở trạng thái Held.");
                    }

                    var allocateRes = alloc.Allocate(nowUtc, "system", command.OperationKey);
                    if (allocateRes.IsFailure) return allocateRes.Error;
                }
                var confirmScopes = new HashSet<string>(StringComparer.Ordinal) { "public-discovery" };
                foreach (var a in groupAllocations)
                {
                    confirmScopes.Add($"asset:{a.AssetId:D}");
                    confirmScopes.Add($"listing:{a.Asset.EquipmentListingId:D}");
                }
                ctx.StageCacheInvalidation(confirmScopes);

                var mapping = groupAllocations.Select(a => new AllocationAssetMappingPayload(
                    a.Id, a.AssetId, a.Asset.AssetCode)).ToList();

                var successPayload = new InventoryAllocationCommandSucceededPayload(
                    command.RentalOrderId,
                    command.RentalItemId,
                    command.OperationKey,
                    AllocationMessageTypes.ConfirmAllocation,
                    Status: "Allocated",
                    HoldExpiresAt: null,
                    Allocations: mapping,
                    OccurredAt: nowUtc);

                return Result.Success(AllocationCommandOutcome.Succeeded(
                    AllocationMessageTypes.ConfirmAllocation,
                    command.OperationKey,
                    command.RentalOrderId,
                    command.RentalItemId,
                    successPayload));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<AllocationCommandOutcome>> ProcessReleaseAllocationCommandAsync(
        MessageEnvelope<ReleaseAllocationPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");

        var command = envelope.Payload;
        var hashPayload = new
        {
            command.RentalOrderId,
            command.RentalItemId,
            Reason = command.Reason?.Trim()
        };

        return await _coordinator.CoordinateAsync<ReleaseAllocationPayload>(
            AllocationMessageTypes.ReleaseAllocation,
            envelope.MessageId,
            envelope.CorrelationId,
            command.RentalOrderId,
            command.RentalItemId,
            command.OperationKey,
            hashPayload,
            handlerName,
            async (ctx, nowUtc, ct) =>
            {
                var groupAllocations = await ctx.Allocations.GetByGroupReferenceAsync(RentalSourceSystem, $"rental-item:{command.RentalItemId}", ct);

                // Release trước Hold
                if (groupAllocations.Count == 0)
                {
                    var tombstonePayload = new InventoryAllocationCommandSucceededPayload(
                        command.RentalOrderId,
                        command.RentalItemId,
                        command.OperationKey,
                        AllocationMessageTypes.ReleaseAllocation,
                        Status: "Released",
                        HoldExpiresAt: null,
                        Allocations: Array.Empty<AllocationAssetMappingPayload>(),
                        OccurredAt: nowUtc);

                    return Result.Success(AllocationCommandOutcome.Succeeded(
                        AllocationMessageTypes.ReleaseAllocation,
                        command.OperationKey,
                        command.RentalOrderId,
                        command.RentalItemId,
                        tombstonePayload));
                }

                if (groupAllocations.Any(a => a.HandedOverAt.HasValue && !a.ReturnedAt.HasValue))
                {
                    return Error.Conflict("ALLOCATION_ALREADY_HANDED_OVER", "Máy đã bàn giao nhưng chưa trả không được giải phóng bằng release thông thường; phải đi qua return.");
                }

                await ctx.AcquireAssetLocksAsync(groupAllocations.Select(a => a.AssetId), ct);

                foreach (var alloc in groupAllocations)
                {
                    if (alloc.IsTerminalState()) continue;

                    if (alloc.Status == AllocationStatus.Held)
                    {
                        var relHoldRes = alloc.ReleaseHold(command.Reason ?? "Release requested", nowUtc, "system", command.OperationKey);
                        if (relHoldRes.IsFailure) return relHoldRes.Error;
                    }
                    else if (alloc.Status == AllocationStatus.Allocated)
                    {
                        var relRes = alloc.Release(command.Reason ?? "Release requested", nowUtc, "system", command.OperationKey);
                        if (relRes.IsFailure) return relRes.Error;
                    }

                    ctx.StageOutbox(InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(alloc.AssetId, nowUtc));
                }
                var releaseScopes = new HashSet<string>(StringComparer.Ordinal) { "public-discovery" };
                foreach (var a in groupAllocations)
                {
                    releaseScopes.Add($"asset:{a.AssetId:D}");
                    releaseScopes.Add($"listing:{a.Asset.EquipmentListingId:D}");
                }
                ctx.StageCacheInvalidation(releaseScopes);

                var mapping = groupAllocations.Select(a => new AllocationAssetMappingPayload(
                    a.Id, a.AssetId, a.Asset.AssetCode)).ToList();

                var successPayload = new InventoryAllocationCommandSucceededPayload(
                    command.RentalOrderId,
                    command.RentalItemId,
                    command.OperationKey,
                    AllocationMessageTypes.ReleaseAllocation,
                    Status: "Released",
                    HoldExpiresAt: null,
                    Allocations: mapping,
                    OccurredAt: nowUtc);

                return Result.Success(AllocationCommandOutcome.Succeeded(
                    AllocationMessageTypes.ReleaseAllocation,
                    command.OperationKey,
                    command.RentalOrderId,
                    command.RentalItemId,
                    successPayload));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<AllocationCommandOutcome>> ProcessMarkAssetsHandedOverCommandAsync(
        MessageEnvelope<MarkAssetsHandedOverPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");

        var command = envelope.Payload;
        var normalizedHandedOverAt = BusinessDateTime.NormalizeUtc(command.HandedOverAt);
        var hashPayload = new
        {
            command.RentalOrderId,
            command.RentalItemId,
            HandedOverAt = normalizedHandedOverAt
        };

        return await _coordinator.CoordinateAsync<MarkAssetsHandedOverPayload>(
            AllocationMessageTypes.MarkAssetsHandedOver,
            envelope.MessageId,
            envelope.CorrelationId,
            command.RentalOrderId,
            command.RentalItemId,
            command.OperationKey,
            hashPayload,
            handlerName,
            async (ctx, nowUtc, ct) =>
            {
                var groupAllocations = await ctx.Allocations.GetByGroupReferenceAsync(RentalSourceSystem, $"rental-item:{command.RentalItemId}", ct);
                if (groupAllocations.Count == 0)
                {
                    return Error.NotFound("GROUP_NOT_FOUND", "Không tìm thấy nhóm phân bổ tương ứng để ghi nhận bàn giao.");
                }

                await ctx.AcquireAssetLocksAsync(groupAllocations.Select(a => a.AssetId), ct);

                foreach (var alloc in groupAllocations)
                {
                    if (alloc.Status != AllocationStatus.Allocated)
                    {
                        return Error.Conflict("INVALID_STATUS_FOR_HANDOVER", $"Chỉ phân bổ Allocated mới được bàn giao. Trạng thái hiện tại: {alloc.Status}.");
                    }

                    var handoverRes = alloc.Handover(normalizedHandedOverAt, nowUtc, "system", command.OperationKey);
                    if (handoverRes.IsFailure) return handoverRes.Error;

                    ctx.StageOutbox(InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(alloc.AssetId, nowUtc));
                }
                var handoverScopes = new HashSet<string>(StringComparer.Ordinal) { "public-discovery" };
                foreach (var a in groupAllocations)
                {
                    handoverScopes.Add($"asset:{a.AssetId:D}");
                    handoverScopes.Add($"listing:{a.Asset.EquipmentListingId:D}");
                }
                ctx.StageCacheInvalidation(handoverScopes);

                var mapping = groupAllocations.Select(a => new AllocationAssetMappingPayload(
                    a.Id, a.AssetId, a.Asset.AssetCode)).ToList();

                var successPayload = new InventoryAllocationCommandSucceededPayload(
                    command.RentalOrderId,
                    command.RentalItemId,
                    command.OperationKey,
                    AllocationMessageTypes.MarkAssetsHandedOver,
                    Status: "Allocated",
                    HoldExpiresAt: null,
                    Allocations: mapping,
                    OccurredAt: nowUtc);

                return Result.Success(AllocationCommandOutcome.Succeeded(
                    AllocationMessageTypes.MarkAssetsHandedOver,
                    command.OperationKey,
                    command.RentalOrderId,
                    command.RentalItemId,
                    successPayload));
            },
            cancellationToken);
    }

    public async Task<Result<AllocationCommandOutcome>> ProcessMarkAssetsOverdueCommandAsync(
        MessageEnvelope<MarkAssetsOverduePayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Authentication is required.");
        var command = envelope.Payload;
        if (command.RentalOrderId == Guid.Empty || command.RentalItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.AllocationGroupId) || string.IsNullOrWhiteSpace(command.OperationKey) ||
            command.AllocationIds is not { Count: > 0 } || command.AllocationIds.Any(id => id == Guid.Empty) ||
            command.AllocationIds.Distinct().Count() != command.AllocationIds.Count)
            return Error.Validation("INVALID_OVERDUE_COMMAND", "Overdue command is incomplete or has duplicate allocations.");

        var hashPayload = new { command.RentalOrderId, command.RentalItemId, command.AllocationGroupId, AllocationIds = command.AllocationIds.Order().ToArray() };
        return await _coordinator.CoordinateAsync<MarkAssetsOverduePayload>(
            AllocationMessageTypes.MarkAssetsOverdue, envelope.MessageId, envelope.CorrelationId,
            command.RentalOrderId, command.RentalItemId, command.OperationKey, hashPayload, handlerName,
            async (ctx, nowUtc, ct) =>
            {
                var allocations = await ctx.Allocations.GetByGroupReferenceAsync(
                    RentalSourceSystem, $"rental-item:{command.RentalItemId}", ct);
                if (!string.Equals(command.AllocationGroupId, $"rental-item:{command.RentalItemId}", StringComparison.Ordinal) ||
                    allocations.Count != command.AllocationIds.Count ||
                    !command.AllocationIds.Order().SequenceEqual(allocations.Select(a => a.Id).Order()))
                    return Error.Validation("ALLOCATION_GROUP_MISMATCH", "Overdue command allocations do not match the rental item group.");

                await ctx.AcquireAssetLocksAsync(allocations.Select(a => a.AssetId), ct);
                DateOnly vietnamToday = BusinessDateTime.ToVietnamDate(nowUtc);
                foreach (var allocation in allocations)
                {
                    if (allocation.Status != AllocationStatus.Allocated || !allocation.HandedOverAt.HasValue ||
                        allocation.ReturnedAt.HasValue || allocation.RentalPeriod.EndDate >= vietnamToday)
                        return Error.Conflict("ALLOCATION_NOT_OVERDUE", "Allocation is not an active overdue handover.");
                }

                var success = new InventoryAllocationCommandSucceededPayload(
                    command.RentalOrderId, command.RentalItemId, command.OperationKey,
                    AllocationMessageTypes.MarkAssetsOverdue, "PROTECTED", null,
                    allocations.Select(a => new AllocationAssetMappingPayload(a.Id, a.AssetId, a.Asset.AssetCode)).ToArray(), nowUtc);
                return Result.Success(AllocationCommandOutcome.Succeeded(
                    AllocationMessageTypes.MarkAssetsOverdue, command.OperationKey,
                    command.RentalOrderId, command.RentalItemId, success));
            },
            (code, message, occurredAt) => AllocationCommandOutcome.Rejected(
                AllocationMessageTypes.MarkAssetsOverdue, command.OperationKey,
                command.RentalOrderId, command.RentalItemId, code, message, occurredAt),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<AllocationCommandOutcome>> ProcessApplyReturnedUnitsCommandAsync(
        MessageEnvelope<ApplyReturnedUnitsCommandPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated) return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");

        var command = envelope.Payload;
        if (command.Units == null || command.Units.Count == 0)
        {
            return Error.Validation("UNITS_REQUIRED", "Danh sách thiết bị hoàn trả không được để trống.");
        }

        if (command.Units.Select(u => u.AllocationId).Distinct().Count() != command.Units.Count)
        {
            return Error.Validation("DUPLICATE_ALLOCATION_IN_RETURN", "Danh sách hoàn trả chứa Allocation ID trùng lặp.");
        }

        if (command.Units.Any(u => !AssetReturnConditions.IsValid(u.Condition)))
        {
            return Error.Validation("INVALID_CONDITION", "Condition chỉ chấp nhận NORMAL hoặc DAMAGED.");
        }

        var normalizedReturnedAt = BusinessDateTime.NormalizeUtc(command.ActualReturnAt);
        var sortedUnits = command.Units.OrderBy(u => u.AllocationId).ToList();

        var hashPayload = new
        {
            command.RentalOrderId,
            command.RentalItemId,
            command.AllocationGroupId,
            command.ReturnRecordId,
            ActualReturnAt = normalizedReturnedAt,
            Units = sortedUnits.Select(u => new { u.AllocationId, Condition = u.Condition.Trim().ToUpperInvariant() })
        };

        return await _coordinator.CoordinateAsync<ApplyReturnedUnitsCommandPayload>(
            AllocationMessageTypes.ApplyReturnedUnits,
            envelope.MessageId,
            envelope.CorrelationId,
            command.RentalOrderId,
            command.RentalItemId,
            command.OperationKey,
            hashPayload,
            handlerName,
            async (ctx, nowUtc, ct) =>
            {
                var groupAllocations = await ctx.Allocations.GetByGroupReferenceAsync(RentalSourceSystem, $"rental-item:{command.RentalItemId}", ct);
                if (groupAllocations.Count == 0)
                {
                    return Error.NotFound("GROUP_NOT_FOUND", "Không tìm thấy nhóm phân bổ tương ứng để hoàn trả.");
                }

                var allocMap = groupAllocations.ToDictionary(a => a.Id);
                foreach (var unit in command.Units)
                {
                    if (!allocMap.TryGetValue(unit.AllocationId, out var alloc))
                    {
                        return Error.Validation("ALLOCATION_NOT_IN_GROUP", $"Allocation {unit.AllocationId} không thuộc nhóm rental-item:{command.RentalItemId}.");
                    }

                    if (alloc.Status != AllocationStatus.Allocated)
                    {
                        return Error.Conflict("INVALID_STATUS_FOR_RETURN", $"Chỉ phân bổ Allocated mới được hoàn trả. Allocation {unit.AllocationId} đang ở trạng thái {alloc.Status}.");
                    }

                    if (!alloc.HandedOverAt.HasValue)
                    {
                        return Error.Conflict("ALLOCATION_NOT_HANDED_OVER", $"Allocation {unit.AllocationId} chưa được bàn giao; không thể hoàn trả.");
                    }

                    if (normalizedReturnedAt < alloc.HandedOverAt.Value)
                    {
                        return Error.Validation("INVALID_RETURN_TIME", $"Thời điểm hoàn trả của allocation {unit.AllocationId} trước thời điểm bàn giao.");
                    }
                }

                var targetAssets = command.Units.Select(u => allocMap[u.AllocationId].AssetId).Distinct().ToList();
                await ctx.AcquireAssetLocksAsync(targetAssets, ct);

                Guid receiptId = Guid.NewGuid();
                var appliedUnits = new List<AppliedReturnedUnitPayload>(command.Units.Count);

                foreach (var unit in command.Units)
                {
                    var alloc = allocMap[unit.AllocationId];
                    bool isDamaged = AssetReturnConditions.IsDamaged(unit.Condition);
                    string conditionStr = isDamaged ? AssetReturnConditions.Damaged : AssetReturnConditions.Normal;

                    var returnRes = alloc.Return(normalizedReturnedAt, conditionStr, nowUtc, "system", command.OperationKey);
                    if (returnRes.IsFailure) return returnRes.Error;

                    string status;
                    string? damageDispositionRef = null;

                    if (isDamaged)
                    {
                        var asset = await ctx.Assets.GetByIdAsync(alloc.AssetId, ct);
                        asset?.DisableBookings($"Damaged on return: Record {command.ReturnRecordId}", nowUtc);
                        status = "OUT_OF_SERVICE";
                        damageDispositionRef = $"damaged:receipt:{receiptId:N}:asset:{alloc.AssetId:N}";
                    }
                    else
                    {
                        status = "RELEASED";
                    }

                    appliedUnits.Add(new AppliedReturnedUnitPayload(
                        alloc.Id,
                        alloc.AssetId,
                        status,
                        nowUtc,
                        damageDispositionRef));

                    ctx.StageOutbox(InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(alloc.AssetId, nowUtc));
                }
                var returnScopes = new HashSet<string>(StringComparer.Ordinal) { "public-discovery" };
                foreach (var unit in command.Units)
                {
                    var a = allocMap[unit.AllocationId];
                    returnScopes.Add($"asset:{a.AssetId:D}");
                    returnScopes.Add($"listing:{a.Asset.EquipmentListingId:D}");
                }
                ctx.StageCacheInvalidation(returnScopes);

                var appliedPayload = new ReturnedUnitsAppliedPayload(
                    command.RentalOrderId,
                    command.RentalItemId,
                    command.AllocationGroupId,
                    command.ReturnRecordId,
                    command.OperationKey,
                    receiptId.ToString("D"),
                    nowUtc,
                    appliedUnits);

                return Result.Success(AllocationCommandOutcome.ReturnApplied(
                    command.OperationKey,
                    command.RentalOrderId,
                    command.RentalItemId,
                    appliedPayload));
            },
            rejectionFactory: (errCode, errMsg, occurredAt) =>
                AllocationCommandOutcome.ReturnRejected(
                    command.OperationKey,
                    command.RentalOrderId,
                    command.RentalItemId,
                    command.AllocationGroupId,
                    command.ReturnRecordId,
                    errCode,
                    errMsg),
            cancellationToken: cancellationToken);
    }
}
