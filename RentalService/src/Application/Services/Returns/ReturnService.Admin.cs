using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

/// <remarks>
/// Nhánh Admin đọc và giải quyết yêu cầu trả có vấn đề. Kiểm tra quyền Admin trước khi truy vấn hoặc thay đổi.
/// Khi quyết định cần ghi nhận trả thực tế, dùng chung ExecuteReturnConfirmationCoreAsync để giữ nhất quán bằng chứng và lệnh Inventory.
/// </remarks>
public sealed partial class ReturnService
{
    private const string AdminResolveReturnAction = "AdminResolveReturnRequest";

    public async Task<Result<PagedResponse<AdminReturnRequestSummaryResponse>>> GetAdminReturnRequestsAsync(
        AdminReturnRequestQuery query,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var auth = RentalAuthorizationPolicy.AuthorizeAdminReturnQuery(caller);
        if (auth.IsFailure) return auth.Error;

        var now = timeProvider.GetUtcNow();
        int page = Math.Max(1, query.Page);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);

        var criteria = new AdminReturnRequestSearchCriteria(query.Disputed, query.Overdue, query.ItemId, query.OrderId);
        var paged = await repository.SearchReturnRequestsForAdminAsync(
            criteria, now, (page - 1) * pageSize, pageSize, cancellationToken);

        var summaries = paged.Items.Select(x =>
        {
            var disputedSet = (x.DisputedAssignmentIds ?? []).ToHashSet();
            var units = x.Units ?? [];
            var unitStatuses = units.Select(u =>
            {
                if (u.ClosedAt.HasValue) return ReturnRequestUnitStatus.Confirmed;
                if (u.RejectedAt.HasValue) return ReturnRequestUnitStatus.Rejected;
                if (disputedSet.Contains(u.RentalUnitAssignmentId)) return ReturnRequestUnitStatus.Disputed;
                return ReturnRequestUnitStatus.PendingSupplierConfirmation;
            }).ToList();

            var reqStatus = ReturnRequestStatusPolicy.GetRequestStatus(unitStatuses);
            bool isOverdue = units.Any(u => !u.ClosedAt.HasValue && !u.RejectedAt.HasValue) && now >= x.ConfirmationDeadlineAt;

            return new AdminReturnRequestSummaryResponse(
                x.ReturnRequestId,
                x.RentalItemId,
                x.OrderId,
                x.OrderCode,
                x.RenterId,
                x.SupplierId,
                x.SubmittedAt,
                x.ConfirmationDeadlineAt,
                reqStatus,
                isOverdue,
                units.Count,
                units.Count(u => !u.ClosedAt.HasValue && !u.RejectedAt.HasValue),
                unitStatuses.Count(s => s == ReturnRequestUnitStatus.Disputed),
                unitStatuses.Count(s => s == ReturnRequestUnitStatus.Confirmed),
                unitStatuses.Count(s => s == ReturnRequestUnitStatus.Rejected));
        }).ToList();

        return OrderQueryMapper.ToPage(summaries, page, pageSize, paged.TotalCount);
    }

    public async Task<Result<AdminReturnRequestDetailResponse>> GetAdminReturnRequestDetailAsync(
        Guid returnRequestId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var auth = RentalAuthorizationPolicy.AuthorizeAdminReturnQuery(caller);
        if (auth.IsFailure) return auth.Error;

        var agg = await repository.GetReturnRequestAggregateAsync(returnRequestId, cancellationToken);
        if (agg is null)
            return Error.NotFound("RETURN_REQUEST_NOT_FOUND", "Return request not found.");

        var (order, item, request) = agg.Value;
        var now = timeProvider.GetUtcNow();

        var requestResponse = OrderQueryMapper.ToReturnRequests(item, now, includeAdminNotes: true)
            .First(r => r.ReturnRequestId == returnRequestId);

        var inventoryUnits = item.UnitAssignments
            .Where(a => request.Units.Any(u => u.RentalUnitAssignmentId == a.Id))
            .OrderBy(a => a.AssetId)
            .Select(a => new AdminReturnUnitInventoryStatusResponse(
                a.Id,
                a.AssetId,
                a.AllocationId,
                a.Status,
                a.ReturnRecordId,
                a.ReturnCondition,
                a.AllocationClosedAt,
                a.InventoryDispositionRef))
            .ToList();

        return new AdminReturnRequestDetailResponse(
            requestResponse,
            order.Id,
            order.OrderCode,
            item.SupplierId,
            order.RenterId,
            item.Status,
            inventoryUnits);
    }

    public async Task<Result<ReturnRequestResponse>> ResolveReturnRequestAsync(
        Guid itemId,
        Guid returnRequestId,
        IRentalCallerContext caller,
        AdminResolveReturnRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var auth = RentalAuthorizationPolicy.AuthorizeAdminReturnResolution(caller);
        if (auth.IsFailure) return auth.Error;
        Guid adminId = caller.UserId!.Value;

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");

        if (request is null)
            return Error.Validation("ReturnResolution.InvalidRequest", "Request body is required.");
        if (!Enum.IsDefined(request.Decision))
            return Error.Validation("ReturnResolution.InvalidDecision", "Invalid return resolution decision.");
        if (string.IsNullOrWhiteSpace(request.DecisionNote) || request.DecisionNote.Trim().Length > 2000)
            return Error.Validation("ReturnResolution.InvalidNote", "Decision note is required and cannot exceed 2000 characters.");
        if (request.AssignmentIds is null || request.AssignmentIds.Count == 0)
            return Error.Validation("ReturnResolution.EmptyAssignments", "AssignmentIds must be specified.");
        if (request.AssignmentIds.Any(id => id == Guid.Empty))
            return Error.Validation("ReturnResolution.InvalidAssignmentId", "AssignmentId cannot be empty GUID.");
        if (request.AssignmentIds.Distinct().Count() != request.AssignmentIds.Count)
            return Error.Validation("ReturnResolution.DuplicateAssignmentIds", "Duplicate assignment IDs are not allowed.");
        if (request.ResolutionMediaIds != null && request.ResolutionMediaIds.Count > 10)
            return Error.Validation("ReturnResolution.TooManyMediaIds", "Maximum 10 resolution media IDs are allowed.");
        if (request.ResolutionMediaIds != null && (request.ResolutionMediaIds.Contains(Guid.Empty) || request.ResolutionMediaIds.Distinct().Count() != request.ResolutionMediaIds.Count))
            return Error.Validation("ReturnResolution.InvalidMediaIds", "Resolution media IDs must be non-empty and distinct.");

        if (request.Decision == ReturnResolutionDecision.ConfirmReceived)
        {
            if (!request.ActualReturnAt.HasValue || request.ActualReturnAt.Value == default)
                return Error.Validation("ReturnResolution.InvalidActualReturnAt", "ActualReturnAt is required when confirming receipt.");
            if (request.Units is null || request.Units.Count == 0 || request.Units.Any(u => u is null || u.MediaIds is null))
                return Error.Validation("ReturnResolution.InvalidUnits", "Units inspection details and media collections are required when confirming receipt.");

            var unitAssignIds = request.Units.Select(u => u.RentalUnitAssignmentId).ToList();
            if (unitAssignIds.Distinct().Count() != request.Units.Count || !unitAssignIds.ToHashSet().SetEquals(request.AssignmentIds))
                return Error.Validation("ReturnResolution.UnitsMismatch", "Inspection units must match targeted AssignmentIds exactly.");

            foreach (var unit in request.Units)
            {
                if (!Enum.IsDefined(unit.Condition))
                    return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

                if (unit.Description?.Length > 2000)
                    return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

                if (unit.MediaIds.Count > 10 || unit.MediaIds.Contains(Guid.Empty) ||
                    unit.MediaIds.Distinct().Count() != unit.MediaIds.Count)
                    return Error.Validation("Return.InvalidEvidence", "Invalid condition, description or media IDs.");

                if (unit.Condition == UnitCondition.Damaged)
                {
                    if (string.IsNullOrWhiteSpace(unit.Description))
                        return Error.Validation("Return.DamagedDescriptionRequired", "Description is required when unit condition is Damaged.");
                    if (unit.MediaIds.Count == 0)
                        return Error.Validation("Return.DamagedMediaRequired", "Media evidence is required when unit condition is Damaged.");
                }
            }
        }

        string key = idempotencyKey.Trim();
        var sortedAssignIds = string.Join(",", request.AssignmentIds.OrderBy(id => id));
        var sortedResMedia = request.ResolutionMediaIds != null ? string.Join(",", request.ResolutionMediaIds.OrderBy(id => id)) : "";
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            ReturnRequestId = returnRequestId,
            AdminId = adminId,
            Decision = request.Decision.ToString(),
            DecisionNote = request.DecisionNote.Trim(),
            AssignmentIds = sortedAssignIds,
            ResolutionMediaIds = sortedResMedia,
            ActualReturnAt = request.ActualReturnAt?.ToUniversalTime(),
            Units = request.Units?.OrderBy(u => u.RentalUnitAssignmentId).Select(u => new
            {
                u.RentalUnitAssignmentId,
                Condition = u.Condition.ToString(),
                u.Description,
                MediaIds = (u.MediaIds ?? []).OrderBy(id => id)
            })
        });
        string fingerprint = RentalOperationExecutor.ComputeFingerprint(payload);

        var orderId = await repository.GetOrderIdByItemIdAsync(itemId, cancellationToken);
        if (!orderId.HasValue) return RentalErrors.ItemNotFound;

        await using var orderLock = await locks.AcquireOrderLockAsync(orderId.Value, TimeSpan.FromSeconds(5), cancellationToken);
        var order = await repository.GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null) return RentalErrors.ItemNotFound;

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return RentalErrors.ItemNotFound;

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == key);
        if (receipt is not null)
        {
            if (receipt.Action != AdminResolveReturnAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(receipt.ApiResponseJson) ? null :
                JsonSerializer.Deserialize<ReturnRequestResponse>(receipt.ApiResponseJson, ReceiptJsonOptions);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }

        var returnReq = item.ReturnRequests.FirstOrDefault(r => r.Id == returnRequestId);
        if (returnReq is null)
            return Error.NotFound("RETURN_REQUEST_NOT_FOUND", "Return request not found on this item.");

        var reqUnitsByAssign = returnReq.Units.ToDictionary(u => u.RentalUnitAssignmentId);
        foreach (var assignId in request.AssignmentIds)
        {
            if (!reqUnitsByAssign.TryGetValue(assignId, out var u))
                return Error.Validation("ReturnResolution.AssignmentNotInRequest", $"Assignment {assignId} does not belong to this return request.");
            if (!u.IsActionable)
                return Error.Conflict("ReturnResolution.UnitNotActionable", $"Assignment {assignId} is already closed or rejected.");
        }

        if (request.ResolutionMediaIds != null && request.ResolutionMediaIds.Count > 0)
        {
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                request.ResolutionMediaIds, adminId, mediaClient, ct: cancellationToken);
            if (evidence.IsFailure) return evidence.Error;
        }

        var now = timeProvider.GetUtcNow();

        if (request.Decision == ReturnResolutionDecision.RejectRequest)
        {
            foreach (var assignId in request.AssignmentIds)
            {
                reqUnitsByAssign[assignId].SetRejectedAt(now);
            }

            var resolution = new ReturnRequestResolution(
                Guid.NewGuid(),
                returnRequestId,
                adminId,
                ReturnResolutionDecision.RejectRequest,
                request.DecisionNote.Trim(),
                request.ResolutionMediaIds ?? [],
                now,
                request.AssignmentIds);

            returnReq.AddResolution(resolution);
            order.RecordReadModelUpdate(now);

            var response = OrderQueryMapper.ToReturnRequests(item, now, includeAdminNotes: true)
                .First(r => r.ReturnRequestId == returnRequestId);

            order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                key,
                AdminResolveReturnAction,
                fingerprint,
                true,
                null,
                null,
                now,
                targetItemId: itemId,
                payloadJson: payload,
                apiResponseJson: JsonSerializer.Serialize(response)));

            await repository.UpdateAsync(order, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            if (notifier != null)
            {
                await NotifyParticipantsOrderChangedAsync(order, item, OrderChangeTypes.ReturnResolved, now, cancellationToken);
            }

            return response;
        }

        // Decision == ConfirmReceived -> delegate to unified return workflow
        var coreResult = await ExecuteReturnConfirmationCoreAsync(
            order,
            item,
            request.ActualReturnAt!.Value,
            request.DecisionNote,
            request.Units!,
            adminId,
            ActorRole.Admin,
            key,
            fingerprint,
            payload,
            AdminResolveReturnAction,
            "AdminConfirmReceived",
            "Admin confirmed returned units",
            (rec, processedAt) =>
            {
                var confirmResolution = new ReturnRequestResolution(
                    Guid.NewGuid(),
                    returnRequestId,
                    adminId,
                    ReturnResolutionDecision.ConfirmReceived,
                    request.DecisionNote.Trim(),
                    request.ResolutionMediaIds ?? [],
                    processedAt,
                    request.AssignmentIds);
                returnReq.AddResolution(confirmResolution);
            },
            (rec, processedAt) => JsonSerializer.Serialize(
                OrderQueryMapper.ToReturnRequests(item, processedAt, includeAdminNotes: true)
                    .First(r => r.ReturnRequestId == returnRequestId)),
            [OrderChangeTypes.ReturnSubmitted, OrderChangeTypes.ReturnResolved],
            cancellationToken);

        if (coreResult.IsFailure) return coreResult.Error;
        var (_, finishedAt) = coreResult.Value;
        return OrderQueryMapper.ToReturnRequests(item, finishedAt, includeAdminNotes: true)
            .First(r => r.ReturnRequestId == returnRequestId);
    }

    private async Task NotifyParticipantsOrderChangedAsync(
        RentalOrder order, RentalItem item, string changeType, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (notifier is null) return;
        try
        {
            await notifier.NotifyRenterOrderChangedAsync(
                order.RenterId,
                new RenterOrderChangedNotification(
                    Guid.NewGuid(),
                    order.Id,
                    order.Version,
                    changeType,
                    now),
                cancellationToken);

            var subOrder = order.SubOrders.FirstOrDefault(s => s.SupplierId == item.SupplierId);
            if (subOrder != null)
            {
                await notifier.NotifySupplierSubOrderChangedAsync(
                    item.SupplierId,
                    new SupplierSubOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        subOrder.Id,
                        order.Version,
                        changeType,
                        now),
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger != null)
            {
                LogAdminReturnNotificationFailed(logger, ex, order.Id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send return notification for order {OrderId}")]
    private static partial void LogAdminReturnNotificationFailed(ILogger logger, Exception ex, Guid orderId);
}
