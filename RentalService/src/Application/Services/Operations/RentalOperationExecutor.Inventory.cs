#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

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

        if (_inventoryCommands != null)
        {
            var extendCmd = new ExtendHoldCommandPayload(order.Id, item.Id, receipt.OperationId, request.NewHoldExpiresAt);
            await _inventoryCommands.EnqueueExtendHoldAsync(extendCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
        }

        Result<ExtendHoldResponse>? clientResult = null;
        try
        {
            clientResult = await _inventoryClient.ExtendHoldAsync(item.AllocationGroupId, request, cancellationToken);
        }
        catch
        {
            // Ignore if client is stub/no-op
        }

        if (clientResult != null && clientResult.IsSuccess)
        {
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(clientResult.Value, JsonOptions));

            var notifyEnvelope = new KafkaMessageEnvelope<RentalSupplierConfirmationRequestedPayload>(
                Guid.NewGuid(),
                KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested,
                1,
                KafkaContractSpec.Producers.RentalService,
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
                    item.SupplierConfirmationDeadline ?? utcNow.AddHours(24)));

            var outbox = new RentalOutboxMessage(
                notifyEnvelope.MessageId,
                _rentalEventsTopic,
                order.Id.ToString("D"),
                KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested,
                JsonSerializer.Serialize(notifyEnvelope, JsonOptions),
                utcNow);

            await _outboxRepository.AddAsync(outbox, cancellationToken);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        if (clientResult != null && clientResult.IsFailure && clientResult.Error.Code is InventoryContractSpec.ErrorCodes.HoldExpired or InventoryContractSpec.ErrorCodes.InvalidHoldExpiry)
        {
            return await HandleHoldFailureAsync(order, item, receipt, clientResult.Error.Message, cancellationToken);
        }

        receipt.MarkProcessing(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        return Result.Success();
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

        if (_inventoryCommands != null)
        {
            var releaseCmd = new ReleaseAllocationCommandPayload(order.Id, item.Id, receipt.OperationId, request.Reason);
            await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmd, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
        }

        Result<ReleaseAllocationResponse>? clientResult = null;
        try
        {
            clientResult = await _inventoryClient.ReleaseAllocationAsync(item.AllocationGroupId, request, cancellationToken);
        }
        catch
        {
            // Ignore
        }

        if (clientResult != null && clientResult.IsSuccess)
        {
            item.MarkAssignmentsReleased();
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(clientResult.Value, JsonOptions));
            await CommitLocalChangesAsync(order, cancellationToken);

            string refundOpKey = ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
            var pendingRefund = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundOpKey && r.Status == RentalOperationReceipt.StatusPending);
            if (pendingRefund != null)
            {
                await RequestRefundInternalAsync(order, item, pendingRefund, refundOpKey, cancellationToken);
            }

            return Result.Success();
        }

        receipt.MarkProcessing(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        return Result.Success();
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
