#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Operations;

public sealed partial class RentalOperationExecutor
{
    public async Task<Result> ExecuteExtendHoldAsync(
        Guid orderId,
        Guid itemId,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationKey);
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

        return await ExtendHoldCoreAsync(order, item, receipt, cancellationToken);
    }

    internal async Task<Result> ExtendHoldCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            return Result.Success();
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Check if deadline already passed
        if (!item.SupplierConfirmationDeadline.HasValue || utcNow >= item.SupplierConfirmationDeadline.Value)
        {
            return await HandleHoldFailureAsync(order, item, receipt, "Deadline passed prior to extend hold", cancellationToken);
        }

        ExtendHoldRequest request;
        if (!string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(receipt.PayloadJson);
                if (doc.RootElement.TryGetProperty("providerRequestJson", out var provProp))
                {
                    request = JsonSerializer.Deserialize<ExtendHoldRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    request = JsonSerializer.Deserialize<ExtendHoldRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của ExtendHold bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho ExtendHold.");
        }

        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        // Call external HTTP without holding DB transaction
        var result = await _inventoryClient.ExtendHoldAsync(item.AllocationGroupId, request, cancellationToken);

        utcNow = _timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            var holdData = result.Value;
            if (string.IsNullOrWhiteSpace(holdData.ReceiptRef) ||
                !string.Equals(holdData.AllocationGroupId, item.AllocationGroupId, StringComparison.OrdinalIgnoreCase))
            {
                RecordExecutionFailure(receipt, Error.Validation("INVALID_HOLD_RESPONSE", "ReceiptRef hoặc AllocationGroupId từ Inventory không hợp lệ."), utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, cancellationToken);
                return Error.Unprocessable("INVALID_HOLD_RESPONSE", "Phản hồi ExtendHold từ Inventory không hợp lệ.");
            }

            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(holdData, JsonOptions));

            var notifyEnvelope = new RentalEventEnvelope<RentalSupplierConfirmationRequestedPayload>(
                Guid.NewGuid(),
                RentalEventTypes.RentalSupplierConfirmationRequested,
                1,
                "rental-service",
                order.Id.ToString("D"),
                utcNow,
                receipt.OperationId,
                null,
                new RentalSupplierConfirmationRequestedPayload(
                    order.Id,
                    item.Id,
                    item.SupplierId,
                    item.EquipmentListingId,
                    item.Quantity,
                    item.SupplierConfirmationDeadline.Value));

            var outbox = new RentalOutboxMessage(
                notifyEnvelope.MessageId,
                _rentalEventsTopic,
                order.Id.ToString("D"),
                RentalEventTypes.RentalSupplierConfirmationRequested,
                JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
                utcNow);

            await _outboxRepository.AddAsync(outbox, cancellationToken);
            await CommitLocalChangesAsync(order, cancellationToken);

            return Result.Success();
        }

        if (result.Error.Code is InventoryContractSpec.ErrorCodes.HoldExpired or InventoryContractSpec.ErrorCodes.InvalidHoldExpiry)
        {
            return await HandleHoldFailureAsync(order, item, receipt, result.Error.Message, cancellationToken);
        }

        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        return result.Error;
    }

    public async Task<Result> ExecuteReleaseAllocationAsync(
        Guid orderId,
        Guid itemId,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return Error.NotFound("ITEM_NOT_FOUND", "Không tìm thấy mục thuê.");

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationKey);
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy receipt.");

        return await ReleaseAllocationCoreAsync(order, item, receipt, operationKey, cancellationToken);
    }

    internal async Task<Result> ReleaseAllocationCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        return await ReleaseAllocationInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    internal async Task<Result> ReleaseAllocationInternalAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        ReleaseAllocationRequest request;
        if (!string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(receipt.PayloadJson);
                if (doc.RootElement.TryGetProperty("providerRequestJson", out var provProp))
                {
                    request = JsonSerializer.Deserialize<ReleaseAllocationRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    request = JsonSerializer.Deserialize<ReleaseAllocationRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của ReleaseAllocation bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho ReleaseAllocation.");
        }

        var result = await _inventoryClient.ReleaseAllocationAsync(item.AllocationGroupId, request, cancellationToken);

        utcNow = _timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            if (string.IsNullOrWhiteSpace(result.Value.ReceiptRef) ||
                !string.Equals(result.Value.AllocationGroupId, item.AllocationGroupId, StringComparison.OrdinalIgnoreCase))
            {
                RecordExecutionFailure(receipt, Error.Validation("INVALID_RECEIPT", "ReceiptRef hoặc AllocationGroupId từ Inventory không hợp lệ."), utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, cancellationToken);
                return Error.Unprocessable("INVALID_RECEIPT", "ReceiptRef từ Inventory không được rỗng.");
            }

            // UnitAssignments only marked Released after valid Inventory receipt!
            item.MarkAssignmentsReleased();

            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(result.Value, JsonOptions));
            await CommitLocalChangesAsync(order, cancellationToken);

            // Execute prerequisite refund if waiting
            string refundOpKey = ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
            var pendingRefund = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey && r.Status == RentalOperationReceipt.StatusPending);
            if (pendingRefund != null)
            {
                await RequestRefundInternalAsync(order, item, pendingRefund, refundOpKey, cancellationToken);
            }

            return Result.Success();
        }

        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        return result.Error;
    }

    private async Task<Result> HandleHoldFailureAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string reason,
        CancellationToken cancellationToken)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        item.ApplyHoldExpired(utcNow, receipt.OperationId, reason);
        receipt.MarkFailed(InventoryContractSpec.ErrorCodes.HoldExpired, reason, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);

        string releaseKey = ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
        var releaseReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseKey);
        if (releaseReceipt == null)
        {
            releaseReceipt = new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                releaseKey,
                "ReleaseAllocation",
                ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.AllocationGroupId}"),
                isSuccess: false,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusPending,
                targetItemId: item.Id,
                payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseKey, reason), JsonOptions));
            order.AddOperationReceipt(releaseReceipt);
        }

        string refundKey = ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
        var refundReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundKey);
        if (refundReceipt == null)
        {
            var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
            refundReceipt = new RentalOperationReceipt(
                Guid.NewGuid(),
                order.Id,
                refundKey,
                "RequestRefund",
                ComputeOperationKey("RequestRefund", $"{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                isSuccess: false,
                errorCode: null,
                errorMessage: null,
                createdAt: utcNow,
                status: RentalOperationReceipt.StatusPending,
                targetItemId: item.Id,
                prerequisiteOperationId: releaseKey,
                payloadJson: JsonSerializer.Serialize(new RequestRefundRequest(order.Id, order.InvoiceRef!, order.PaymentRef!, refundItems, reason, refundKey), JsonOptions));
            order.AddOperationReceipt(refundReceipt);
        }

        order.EvaluateOrderStatusRollup(utcNow, receipt.OperationId);
        await CommitLocalChangesAsync(order, cancellationToken);

        // Await release under the current lock!
        await ReleaseAllocationInternalAsync(order, item, releaseReceipt, releaseKey, cancellationToken);

        return Result.Success();
    }
}
