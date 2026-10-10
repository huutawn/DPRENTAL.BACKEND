#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Operations;

public sealed partial class RentalOperationExecutor
{
    public async Task<Result<SupplierDecisionResponse>> ExecuteSupplierAcceptAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        if (item.SupplierId != supplierId)
        {
            return Error.Forbidden("SUPPLIER_MISMATCH", "Chỉ chủ thiết bị mới có quyền đưa ra quyết định.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string fingerprint = ComputeFingerprint($"accept:{itemId}:{supplierId}");

        // 1. Replay check on existing receipt with same key
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        if (existingReceipt != null)
        {
            if (existingReceipt.IsSuccess && existingReceipt.Status == RentalOperationReceipt.StatusSucceeded)
            {
                if (existingReceipt.Action == "SupplierAccept" &&
                    existingReceipt.TargetItemId == itemId &&
                    string.Equals(existingReceipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
                    {
                        return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal bị hỏng.");
                    }
                    try
                    {
                        var storedResp = JsonSerializer.Deserialize<SupplierDecisionResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                        if (storedResp != null) return storedResp;
                    }
                    catch
                    {
                        return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
                    }
                }
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            // In-flight processing with same key -> continue operation
        }

        // 2. DECISION_IN_PROGRESS check: check if any in-flight accept intent exists with different key
        if (existingReceipt == null && order.HasInFlightAccept(itemId))
        {
            return Error.Conflict("DECISION_IN_PROGRESS", "Yêu cầu chấp nhận đang được xử lý hoặc chờ đối soát; không thể thực hiện thao tác khác.");
        }

        // 3. Current state check
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return Error.Conflict("ITEM_ALREADY_DECIDED", "Mục thuê đã được quyết định với khóa idempotency khác.");
        }

        if (order.Status != RentalOrderStatus.Paid)
        {
            return Error.Conflict("ORDER_NOT_PAID", "Đơn hàng chưa được thanh toán.");
        }

        if (item.SupplierConfirmationDeadline.HasValue && utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return Error.Expired("SUPPLIER_DEADLINE_EXPIRED", "Đã quá thời hạn xác nhận của supplier.");
        }

        // 4. ExtendHold must be Succeeded
        string holdOpKey = ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
        var holdReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == holdOpKey || (r.Action == "ExtendHold" && r.TargetItemId == item.Id));
        if (holdReceipt == null || !holdReceipt.IsSuccess || holdReceipt.Status != RentalOperationReceipt.StatusSucceeded)
        {
            return Error.Conflict("HOLD_NOT_EXTENDED", "Hold cho thiết bị chưa được gia hạn thành công (Succeeded).");
        }

        // 5. Claim intent in journal before HTTP call
        var receipt = existingReceipt ?? new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            "SupplierAccept",
            fingerprint,
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusProcessing,
            targetItemId: item.Id);

        if (existingReceipt == null)
        {
            order.AddOperationReceipt(receipt);
        }
        else
        {
            receipt.MarkProcessing(utcNow);
        }

        await CommitLocalChangesAsync(order, cancellationToken);

        // 6. Execute ConfirmAllocation core
        return await ConfirmAllocationCoreAsync(order, item, receipt, normalizedKey, supplierId, isReconciliation: false, cancellationToken);
    }

    public async Task<Result<SupplierDecisionResponse>> ExecuteSupplierRejectAsync(
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        string reason,
        string? note,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        if (item.SupplierId != supplierId)
        {
            return Error.Forbidden("SUPPLIER_MISMATCH", "Chỉ chủ thiết bị mới có quyền đưa ra quyết định.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string normalizedKey = idempotencyKey.Trim();
        string fingerprint = ComputeFingerprint($"reject:{itemId}:{reason}:{note}");

        // Replay check
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        if (existingReceipt != null)
        {
            if (existingReceipt.IsSuccess && existingReceipt.Status == RentalOperationReceipt.StatusSucceeded)
            {
                if (existingReceipt.Action == "SupplierReject" &&
                    existingReceipt.TargetItemId == itemId &&
                    string.Equals(existingReceipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
                    {
                        return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal bị hỏng.");
                    }
                    try
                    {
                        var storedResp = JsonSerializer.Deserialize<SupplierDecisionResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                        if (storedResp != null) return storedResp;
                    }
                    catch
                    {
                        return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
                    }
                }
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }
        }

        // DECISION_IN_PROGRESS check: in-flight accept blocks reject
        if (order.HasInFlightAccept(itemId))
        {
            return Error.Conflict("DECISION_IN_PROGRESS", "Yêu cầu chấp nhận đang được xử lý hoặc chờ đối soát; không thể từ chối mục thuê.");
        }

        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return Error.Conflict("ITEM_ALREADY_DECIDED", "Mục thuê đã được quyết định với khóa idempotency khác.");
        }

        if (order.Status != RentalOrderStatus.Paid)
        {
            return Error.Conflict("ORDER_NOT_PAID", "Đơn hàng chưa được thanh toán.");
        }

        if (item.SupplierConfirmationDeadline.HasValue && utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return Error.Expired("SUPPLIER_DEADLINE_EXPIRED", "Đã quá thời hạn xác nhận của supplier.");
        }

        // Apply reject
        item.ApplySupplierReject(supplierId, utcNow, reason, note, normalizedKey);
        order.EvaluateOrderStatusRollup(utcNow, normalizedKey);

        var response = BuildSupplierResponse(order, item);
        string apiResponseJson = JsonSerializer.Serialize(response, JsonOptions);

        var rejectReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            "SupplierReject",
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: utcNow,
            targetItemId: item.Id,
            apiResponseJson: apiResponseJson);
        order.AddOperationReceipt(rejectReceipt);

        // Schedule ReleaseAllocation and single logical RequestRefund
        string releaseOpKey = ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
        var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
        if (releaseReceipt == null)
        {
            releaseReceipt = new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                releaseOpKey,
                "ReleaseAllocation",
                ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.AllocationGroupId}"),
                isSuccess: false,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusPending,
                targetItemId: item.Id,
                payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseOpKey, reason), JsonOptions));
            order.AddOperationReceipt(releaseReceipt);
        }

        string refundOpKey = ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
        var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey);
        if (refundReceipt == null)
        {
            var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
            refundReceipt = new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                refundOpKey,
                "RequestRefund",
                ComputeOperationKey("RequestRefund", $"{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                isSuccess: false,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusPending,
                targetItemId: item.Id,
                prerequisiteOperationId: releaseOpKey,
                payloadJson: JsonSerializer.Serialize(new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, reason, refundOpKey), JsonOptions));
            order.AddOperationReceipt(refundReceipt);
        }

        var notifyEnvelope = new RentalEventEnvelope<RentalItemRejectedPayload>(
            Guid.NewGuid(),
            RentalEventTypes.RentalItemRejected,
            1,
            "rental-service",
            order.Id.ToString("D"),
            utcNow,
            normalizedKey,
            null,
            new RentalItemRejectedPayload(order.Id, item.Id, supplierId, utcNow, reason, note));

        var outbox = new RentalOutboxMessage(
            notifyEnvelope.MessageId,
            _rentalEventsTopic,
            order.Id.ToString("D"),
            RentalEventTypes.RentalItemRejected,
            JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
            utcNow);

        await _outboxRepository.AddAsync(outbox, cancellationToken);
        await CommitLocalChangesAsync(order, cancellationToken);

        return response;
    }

    public async Task<Result> ExecuteReconcileAcceptAsync(
        Guid orderId,
        Guid itemId,
        string acceptOperationKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        var acceptReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == acceptOperationKey);
        if (acceptReceipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy receipt accept.");

        if (acceptReceipt.IsSuccess && acceptReceipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        // Invariant: Do NOT restore Rejected or Expired items to AwaitingHandover!
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            _logger.LogWarning("Cannot reconcile accept for item {ItemId} in terminal status {Status}", item.Id, item.Status);
            return Error.Conflict("ITEM_IN_TERMINAL_STATE", $"Mục thuê đã ở trạng thái {item.Status}; không thể khôi phục thành AwaitingHandover.");
        }

        var result = await ConfirmAllocationCoreAsync(order, item, acceptReceipt, acceptOperationKey, item.SupplierId, isReconciliation: true, cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    private async Task<Result<SupplierDecisionResponse>> ConfirmAllocationCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        Guid supplierId,
        bool isReconciliation,
        CancellationToken cancellationToken)
    {
        // Call Inventory ConfirmAllocation without holding DB transaction
        var confirmReq = new ConfirmAllocationRequest(operationKey);
        var confirmResult = await _inventoryClient.ConfirmAllocationAsync(item.AllocationGroupId, confirmReq, cancellationToken);

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        if (confirmResult.IsFailure)
        {
            _logger.LogWarning("ConfirmAllocation for item {ItemId} failed: {Code} - {Msg}", item.Id, confirmResult.Error.Code, confirmResult.Error.Message);
            RecordExecutionFailure(receipt, confirmResult.Error, utcNow);
            await CommitLocalChangesAsync(order, cancellationToken);
            return confirmResult.Error;
        }

        var confirmData = confirmResult.Value;
        var validation = ValidateAllocationResponse(item, confirmData);
        if (validation.IsFailure)
        {
            receipt.MarkFailed("INVALID_ALLOCATION", validation.Error.Message, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            await CommitLocalChangesAsync(order, cancellationToken);
            return validation.Error;
        }

        // Invariant: Item must still be in PendingSupplierConfirmation to transition
        if (item.Status != RentalItemStatus.PendingSupplierConfirmation)
        {
            return Error.Conflict("ITEM_IN_TERMINAL_STATE", $"Mục thuê đã ở trạng thái {item.Status}; không thể áp dụng kết quả.");
        }

        // Apply accept atomically
        item.ApplySupplierAccept(supplierId, utcNow, confirmData.ReceiptRef, operationKey);
        order.EvaluateOrderStatusRollup(utcNow, operationKey);

        var response = BuildSupplierResponse(order, item);
        string responseJson = JsonSerializer.Serialize(confirmData, JsonOptions);
        string apiResponseJson = JsonSerializer.Serialize(response, JsonOptions);
        receipt.MarkSucceeded(utcNow, responseJson, apiResponseJson);

        var notifyEnvelope = new RentalEventEnvelope<RentalItemConfirmedPayload>(
            Guid.NewGuid(),
            RentalEventTypes.RentalItemConfirmed,
            1,
            "rental-service",
            order.Id.ToString("D"),
            utcNow,
            operationKey,
            null,
            new RentalItemConfirmedPayload(order.Id, item.Id, supplierId, utcNow, confirmData.ReceiptRef));

        var outbox = new RentalOutboxMessage(
            notifyEnvelope.MessageId,
            _rentalEventsTopic,
            order.Id.ToString("D"),
            RentalEventTypes.RentalItemConfirmed,
            JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
            utcNow);

        await _outboxRepository.AddAsync(outbox, cancellationToken);
        await CommitLocalChangesAsync(order, cancellationToken);

        return response;
    }

    private static Result ValidateAllocationResponse(RentalItem item, ConfirmAllocationResponse confirmData)
    {
        if (string.IsNullOrWhiteSpace(confirmData.ReceiptRef))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "ReceiptRef từ Inventory không được rỗng.");
        }

        if (!string.Equals(confirmData.AllocationGroupId, item.AllocationGroupId, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "AllocationGroupId từ Inventory không khớp với đơn hàng.");
        }

        if (confirmData.Units == null || confirmData.Units.Count != item.UnitAssignments.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Số lượng units xác nhận không khớp với số lượng của đơn hàng.");
        }

        // All units must be distinct and confirmed
        var confirmedUnitIds = confirmData.Units.Select(u => u.AllocationId).ToList();
        if (confirmedUnitIds.Distinct().Count() != confirmedUnitIds.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Danh sách units từ Inventory có chứa AllocationId trùng lặp.");
        }

        foreach (var unit in item.UnitAssignments)
        {
            var match = confirmData.Units.FirstOrDefault(u => u.AllocationId == unit.AllocationId && u.AssetId == unit.AssetId);
            if (match == null)
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Unit assignment {unit.AllocationId} không tồn tại trong kết quả từ Inventory.");
            }

            if (!string.Equals(match.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Trạng thái của unit {unit.AllocationId} không phải là Confirmed (nhận: {match.Status}).");
            }
        }

        return Result.Success();
    }

    private static SupplierDecisionResponse BuildSupplierResponse(RentalOrder order, RentalItem item)
    {
        return new SupplierDecisionResponse(
            item.Id,
            order.Id,
            order.OrderCode,
            item.Status.ToString(),
            item.SupplierDecisionAt,
            item.SupplierDecisionBy,
            item.SupplierDecisionReason,
            item.SupplierDecisionNote);
    }
}
