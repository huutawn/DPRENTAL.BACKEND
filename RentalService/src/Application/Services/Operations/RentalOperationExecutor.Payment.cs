#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor
{
    public async Task<Result> ExecuteCancelInvoiceAsync(
        Guid orderId,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null) return Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng.");

        if (string.IsNullOrWhiteSpace(order.InvoiceRef)) return Result.Success();

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == operationKey);
        if (receipt == null) return Error.NotFound("RECEIPT_NOT_FOUND", "Không tìm thấy operation receipt.");

        return await CancelInvoiceInternalAsync(order, receipt, operationKey, cancellationToken);
    }

    internal async Task<Result> CancelInvoiceCoreAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        return await CancelInvoiceInternalAsync(order, receipt, operationKey, cancellationToken);
    }

    internal async Task<Result> CancelInvoiceInternalAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(order.InvoiceRef)) return Result.Success();
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        CancelInvoiceRequest request;
        if (!string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(receipt.PayloadJson);
                if (doc.RootElement.TryGetProperty("providerRequestJson", out var provProp))
                {
                    request = JsonSerializer.Deserialize<CancelInvoiceRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    request = JsonSerializer.Deserialize<CancelInvoiceRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của CancelInvoice bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho CancelInvoice.");
        }
        var result = await _paymentClient.CancelInvoiceAsync(request, cancellationToken);

        utcNow = _timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            if (!result.Value.Cancelled || !string.Equals(result.Value.InvoiceRef, order.InvoiceRef, StringComparison.OrdinalIgnoreCase))
            {
                RecordExecutionFailure(receipt, Error.Validation("CANCEL_UNCONFIRMED", "PaymentService không xác nhận hủy invoice (Cancelled != true hoặc InvoiceRef sai)."), utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, cancellationToken);
                return Error.Unprocessable("CANCEL_UNCONFIRMED", "PaymentService không xác nhận hủy invoice.");
            }
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(result.Value, JsonOptions));
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        if (string.Equals(result.Error.Code, PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, StringComparison.OrdinalIgnoreCase))
        {
            receipt.MarkFailed(result.Error.Code, result.Error.Message, utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Error.Conflict(PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, "Invoice was already paid; halting expiry.");
        }

        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        return result.Error;
    }

    public async Task<Result> ExecuteRequestRefundAsync(
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

        if (receipt.Status == RentalOperationReceipt.StatusManualRefundRequired)
        {
            return Error.Conflict("MANUAL_REFUND_REQUIRED", "Tác vụ hoàn tiền late payment yêu cầu xử lý thủ công, không hỗ trợ gọi refund tự động.");
        }

        return await RequestRefundInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    public Task<Result> RequestRefundDirectAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        return RequestRefundInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    internal async Task<Result> RequestRefundCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        return await RequestRefundInternalAsync(order, item, receipt, operationKey, cancellationToken);
    }

    internal async Task<Result> RequestRefundInternalAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();
        if (receipt.Status == RentalOperationReceipt.StatusManualRefundRequired)
        {
            return Error.Conflict("MANUAL_REFUND_REQUIRED", "Tác vụ hoàn tiền late payment yêu cầu xử lý thủ công, không hỗ trợ gọi refund tự động.");
        }


        // Strict prerequisite check: ReleaseAllocation must be confirmed Succeeded
        if (!string.IsNullOrWhiteSpace(receipt.PrerequisiteOperationId))
        {
            var prereq = order.OperationReceipts.FirstOrDefault(r => r.OperationId == receipt.PrerequisiteOperationId);
            if (prereq == null || !prereq.IsSuccess || prereq.Status != RentalOperationReceipt.StatusSucceeded)
            {
                _logger.LogInformation("Refund operation {OpKey} waiting for prerequisite {Prereq}", operationKey, receipt.PrerequisiteOperationId);
                return Result.Success();
            }
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        RequestRefundRequest request;
        if (!string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(receipt.PayloadJson);
                if (doc.RootElement.TryGetProperty("providerRequestJson", out var provProp))
                {
                    request = JsonSerializer.Deserialize<RequestRefundRequest>(provProp.GetString()!, JsonOptions)!;
                }
                else
                {
                    request = JsonSerializer.Deserialize<RequestRefundRequest>(receipt.PayloadJson, JsonOptions)!;
                }
            }
            catch
            {
                return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload JSON của RequestRefund bị hỏng; cần đối soát.");
            }
        }
        else
        {
            return Error.Failure("MISSING_REQUEST_PAYLOAD", "Thiếu request payload JSON cho RequestRefund.");
        }
        // Amount = 0 does not disburse
        if (request.TotalRefundAmount <= 0)
        {
            receipt.MarkSucceeded(utcNow, "{\"zeroAmount\":true}");
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        var result = await _paymentClient.RequestRefundAsync(request, cancellationToken);

        utcNow = _timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            var refundData = result.Value;
            if (string.IsNullOrWhiteSpace(refundData.RefundRef) ||
                !string.Equals(refundData.Status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(refundData.OperationKey, operationKey, StringComparison.OrdinalIgnoreCase))
            {
                RecordExecutionFailure(receipt, Error.Validation("INVALID_REFUND_RESPONSE", "Phản hồi từ PaymentService không hợp lệ."), utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, cancellationToken);
                return Error.Unprocessable("INVALID_REFUND_RESPONSE", "Phản hồi hoàn tiền từ PaymentService không hợp lệ.");
            }
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(refundData, JsonOptions));
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);
        return result.Error;
    }

    public async Task<Result> ExecutePaymentExpiryAsync(
        Guid orderId,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null || order.Status != RentalOrderStatus.PendingPayment) return Result.Success();

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        if (utcNow < order.PaymentDeadline) return Result.Success();

        // 1. Cancel invoice first. An order whose invoice was never created (Payment was down for the whole
        //    payment window) has nothing to cancel, and a receipt for it would stay Pending forever.
        if (!string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            string cancelOpKey = ComputeOperationKey("CancelInvoice", order.Id.ToString());
            var cancelReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == cancelOpKey);
            if (cancelReceipt == null)
            {
                cancelReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(),
                    order.Id,
                    cancelOpKey,
                    "CancelInvoice",
                    ComputeOperationKey("CancelInvoice", $"{order.Id}:{order.InvoiceRef}"),
                    isSuccess: false,
                    errorCode: null,
                    errorMessage: null,
                    createdAt: utcNow,
                    status: RentalOperationReceipt.StatusPending,
                    payloadJson: JsonSerializer.Serialize(new CancelInvoiceRequest(order.Id, order.InvoiceRef, "Payment window expired", cancelOpKey), JsonOptions));
                order.AddOperationReceipt(cancelReceipt);
                await CommitLocalChangesAsync(order, cancellationToken);
            }

            var cancelRes = await CancelInvoiceInternalAsync(order, cancelReceipt, cancelOpKey, cancellationToken);
            if (cancelRes.IsFailure)
            {
                if (string.Equals(cancelRes.Error.Code, PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Invoice was already collected for Order {OrderId}. Halting expiry and waiting for InvoicePaid event.", order.Id);
                    return Result.Success(); // Halts expiry! Does NOT release holds or expire order.
                }
                return cancelRes;
            }
        }

        // 2. Release all allocation groups
        var releaseKeys = new List<(RentalItem item, string releaseKey)>();
        foreach (var item in order.Items)
        {
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
                    payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseKey, "Payment window expired"), JsonOptions));
                order.AddOperationReceipt(releaseReceipt);
            }
            releaseKeys.Add((item, releaseKey));
        }

        await CommitLocalChangesAsync(order, cancellationToken);

        bool allReleased = true;
        foreach (var (item, releaseKey) in releaseKeys)
        {
            var receipt = order.OperationReceipts.First(r => r.OperationId == releaseKey);
            var relRes = await ReleaseAllocationInternalAsync(order, item, receipt, releaseKey, cancellationToken);
            if (relRes.IsFailure) allReleased = false;
        }

        if (!allReleased)
        {
            return Error.ServiceUnavailable("RELEASE_PENDING", "Một số allocations chưa hoàn tất release; sẽ retry qua worker.");
        }

        // 3. Mark Expired ONLY after valid cancel and all releases succeeded
        utcNow = _timeProvider.GetUtcNow();
        order.MarkPaymentExpired(utcNow, operationKey);
        order.RecordReadModelUpdate(utcNow);

        var outboxEnvelope = new KafkaMessageEnvelope<RentalOrderExpiredPayload>(
            Guid.NewGuid(),
            KafkaContractSpec.EventTypes.RentalOrderExpired,
            1,
            "rental-service",
            order.Id.ToString("D"),
            utcNow,
            operationKey,
            null,
            new RentalOrderExpiredPayload(order.Id, order.RenterId, utcNow, "Payment window expired without payment"));

        var outbox = new RentalOutboxMessage(
            outboxEnvelope.MessageId,
            _rentalEventsTopic,
            order.Id.ToString("D"),
            KafkaContractSpec.EventTypes.RentalOrderExpired,
            JsonSerializer.Serialize(outboxEnvelope, JsonOptions),
            utcNow);

        await _outboxRepository.AddAsync(outbox, cancellationToken);
        await CommitLocalChangesAsync(order, cancellationToken);

        return Result.Success();
    }

    internal async Task<Result> PaymentExpiryCoreAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        return await ExecutePaymentExpiryAsync(order.Id, receipt.OperationId, cancellationToken);
    }

    internal async Task<Result> ExecuteCreateInvoiceAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(order.InvoiceRef))
        {
            if (_invoiceIssuer != null)
            {
                return await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
            }
            return Result.Success();
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        if (order.PaymentDeadline <= utcNow)
        {
            receipt.MarkFailed("ORDER_EXPIRED", "Payment deadline has passed; halting invoice creation.", utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Error.Conflict("ORDER_EXPIRED", "Payment deadline has passed; halting invoice creation.");
        }

        if (order.Status != RentalOrderStatus.PendingPayment)
        {
            receipt.MarkFailed("ORDER_NOT_PENDING", "Order is no longer pending payment; halting invoice creation.", utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            await CommitLocalChangesAsync(order, cancellationToken);
            return Result.Success();
        }

        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        if (_invoiceIssuer == null)
        {
            return Error.Failure("INVOICE_ISSUER_UNAVAILABLE", "Invoice issuer is not configured.");
        }

        var invoiceResult = await _invoiceIssuer.EnsureInvoiceAsync(order, cancellationToken);
        utcNow = _timeProvider.GetUtcNow();

        if (invoiceResult.IsSuccess)
        {
            // Issuer already validated, attached invoice, marked receipt Succeeded, updated operation to Ready, and committed in one SaveChangesAsync.
            return Result.Success();
        }

        RecordExecutionFailure(receipt, invoiceResult.Error, utcNow, forceUncertain: false, deadline: order.PaymentDeadline);
        await CommitLocalChangesAsync(order, cancellationToken);

        if (receipt.Status == RentalOperationReceipt.StatusFailed && _checkoutOperationRepo != null)
        {
            var op = await _checkoutOperationRepo.GetByExpectedOrderIdAsync(order.Id, cancellationToken);
            if (op != null)
            {
                op.SetInvoiceStatus("Failed", utcNow);
                await _checkoutOperationRepo.UpdateAsync(op, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        return invoiceResult.Error;
    }
}
