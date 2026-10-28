using System.Text.Json;
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
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Returns;

public sealed partial class ReturnService
{
    private const string ReportReturnIssueAction = "ReportReturnIssue";

    public async Task<Result<ReturnRequestResponse>> ReportReturnIssueAsync(
        Guid itemId,
        Guid returnRequestId,
        IRentalCallerContext caller,
        ReportReturnIssueRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.IsAuthenticated || caller.IsService || !caller.UserId.HasValue)
        {
            if (caller.IsService)
                return Error.Forbidden("FORBIDDEN", "Service token không được báo issue trả máy.");
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu đăng nhập.");
        }
        Guid callerUserId = caller.UserId.Value;
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key is required (maximum 64 characters).");

        if (request is null)
            return Error.Validation("ReturnIssue.InvalidRequest", "Request body is required.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)
            return Error.Validation("ReturnIssue.InvalidReason", "Reason is required and cannot exceed 500 characters.");
        if (request.Note?.Length > 2000)
            return Error.Validation("ReturnIssue.InvalidNote", "Note cannot exceed 2000 characters.");
        if (request.MediaIds != null && request.MediaIds.Count > 10)
            return Error.Validation("ReturnIssue.TooManyMediaIds", "Maximum 10 media IDs are allowed.");
        if (request.AssignmentIds != null)
        {
            if (request.AssignmentIds.Count == 0)
                return Error.Validation("ReturnIssue.EmptyAssignmentIds", "AssignmentIds must not be empty when provided.");
            if (request.AssignmentIds.Any(id => id == Guid.Empty))
                return Error.Validation("ReturnIssue.InvalidAssignmentId", "AssignmentId cannot be empty GUID.");
            if (request.AssignmentIds.Distinct().Count() != request.AssignmentIds.Count)
                return Error.Validation("ReturnIssue.DuplicateAssignmentIds", "Duplicate assignment IDs are not allowed.");
        }

        string key = idempotencyKey.Trim();
        string assignmentsFingerprint = (request.AssignmentIds != null && request.AssignmentIds.Count > 0)
            ? string.Join(",", request.AssignmentIds.Distinct().OrderBy(id => id))
            : "ALL_ACTIONABLE";
        string mediaFingerprint = (request.MediaIds != null && request.MediaIds.Count > 0)
            ? string.Join(",", request.MediaIds.Distinct().OrderBy(id => id))
            : "";
        string payload = JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            ReturnRequestId = returnRequestId,
            ReporterId = callerUserId,
            Reason = request.Reason.Trim(),
            Note = request.Note,
            MediaIds = mediaFingerprint,
            AssignmentIds = assignmentsFingerprint
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
            if (receipt.Action != ReportReturnIssueAction || receipt.TargetItemId != itemId || receipt.Fingerprint != fingerprint)
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "This key was used for another request.");
            var saved = string.IsNullOrWhiteSpace(receipt.ApiResponseJson) ? null :
                JsonSerializer.Deserialize<ReturnRequestResponse>(receipt.ApiResponseJson, ReceiptJsonOptions);
            return saved is null ? Error.Failure("INVALID_RETURN_RECEIPT", "Stored response is unavailable.") : saved;
        }

        ActorRole reporterRole;
        if (callerUserId == order.RenterId)
        {
            reporterRole = ActorRole.Renter;
        }
        else if (callerUserId == item.SupplierId)
        {
            reporterRole = ActorRole.Supplier;
        }
        else
        {
            return RentalErrors.ItemNotFound;
        }

        var returnReq = item.ReturnRequests.FirstOrDefault(r => r.Id == returnRequestId);
        if (returnReq is null)
            return Error.NotFound("RETURN_REQUEST_NOT_FOUND", "Return request not found on this item.");

        var reqUnitsByAssign = returnReq.Units.ToDictionary(u => u.RentalUnitAssignmentId);
        List<Guid> targetAssignmentIds = [];

        if (request.AssignmentIds != null && request.AssignmentIds.Count > 0)
        {
            foreach (var assignId in request.AssignmentIds)
            {
                if (!reqUnitsByAssign.TryGetValue(assignId, out var unit))
                    return Error.Validation("ReturnIssue.AssignmentNotInRequest", $"Assignment {assignId} does not belong to this return request.");
                if (!unit.IsActionable)
                    return Error.Conflict("ReturnIssue.UnitNotActionable", $"Assignment {assignId} is already closed or rejected.");
                targetAssignmentIds.Add(assignId);
            }
        }
        else
        {
            targetAssignmentIds = returnReq.Units.Where(u => u.IsActionable).Select(u => u.RentalUnitAssignmentId).ToList();
            if (targetAssignmentIds.Count == 0)
                return Error.Conflict("ReturnIssue.NoActionableUnits", "No actionable units remain in this return request.");
        }

        if (request.MediaIds != null && request.MediaIds.Count > 0)
        {
            var evidence = await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(
                request.MediaIds, callerUserId, mediaClient, ct: cancellationToken);
            if (evidence.IsFailure) return evidence.Error;
        }

        var now = timeProvider.GetUtcNow();
        var issue = new ReturnRequestIssue(
            Guid.NewGuid(),
            returnRequestId,
            callerUserId,
            reporterRole,
            request.Reason.Trim(),
            request.Note?.Trim(),
            request.MediaIds ?? [],
            now,
            targetAssignmentIds);

        returnReq.AddIssue(issue);
        order.RecordReadModelUpdate(now);

        var response = OrderQueryMapper.ToReturnRequests(item, now, includeAdminNotes: false)
            .First(r => r.ReturnRequestId == returnRequestId);

        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            key,
            ReportReturnIssueAction,
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
            try
            {
                await notifier.NotifyRenterOrderChangedAsync(
                    order.RenterId,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(),
                        order.Id,
                        order.Version,
                        OrderChangeTypes.ReturnDisputed,
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
                            OrderChangeTypes.ReturnDisputed,
                            now),
                        cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger != null)
                {
                    LogReturnIssueNotificationFailed(logger, ex, order.Id);
                }
            }
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send ReturnDisputed notification for order {OrderId}")]
    private static partial void LogReturnIssueNotificationFailed(ILogger logger, Exception ex, Guid orderId);
}
