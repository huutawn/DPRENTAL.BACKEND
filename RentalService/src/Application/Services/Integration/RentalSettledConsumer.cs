#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.Integration;

public sealed class RentalSettledConsumer(TimeProvider timeProvider) : IRentalSettledConsumer
{
    public Task<Result> ApplyRentalSettledStagedAsync(
        RentalOrder order, KafkaMessageEnvelope<RentalSettledPayload> envelope,
        RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default)
    {
        var payload = envelope.Payload;
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (payload.RentalOrderId != order.Id || payload.RentalItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.SettlementRef) || string.IsNullOrWhiteSpace(payload.OperationKey) ||
            payload.CompletedAt == default)
            return Task.FromResult<Result>(Error.Validation("INVALID_SETTLEMENT_EVENT", "Settlement event does not identify this order and item."));

        if (!string.Equals(payload.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Result>(Error.Validation("CURRENCY_MISMATCH", "Tiền tệ phải là VND."));

        if (!string.IsNullOrWhiteSpace(order.InvoiceRef) &&
            !string.Equals(order.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Result>(Error.Conflict("INVOICE_REF_MISMATCH", "InvoiceRef không khớp với đơn hàng."));

        if (!string.IsNullOrWhiteSpace(order.PaymentRef) &&
            !string.Equals(order.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Result>(Error.Conflict("PAYMENT_REF_MISMATCH", "PaymentRef không khớp với đơn hàng."));

        var item = order.Items.FirstOrDefault(i => i.Id == payload.RentalItemId);
        if (item is null)
            return Task.FromResult<Result>(Error.Validation("ITEM_MEMBERSHIP_MISMATCH",
                $"Mục thuê {payload.RentalItemId} không thuộc đơn hàng."));

        if (!item.InventoryClosureConfirmedAt.HasValue)
            return Task.FromResult<Result>(Error.Conflict("INVENTORY_CLOSURE_PENDING",
                "Inventory has not confirmed closure for all returned assets."));

        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == payload.OperationKey);
        if (receipt is null || receipt.Action != RentalItem.SettleRentalAction || receipt.TargetItemId != item.Id)
            return Task.FromResult<Result>(Error.Validation("INVALID_OPERATION_KEY",
                "Không tìm thấy yêu cầu quyết toán tương ứng trong đơn hàng."));

        var stored = RentalOperationExecutor.ReadStoredProviderRequest<SettleRentalRequest>(receipt.PayloadJson);
        if (stored is null)
            return Task.FromResult<Result>(Error.Validation("CORRUPTED_STORED_REQUEST",
                "Stored SettleRental payload bị hỏng; không thể đối soát."));

        if (stored.FinalDeductionAmount != payload.FinalDeductionAmount ||
            stored.RefundedDepositAmount != payload.RefundedDepositAmount ||
            stored.RentalOrderId != payload.RentalOrderId || stored.RentalItemId != payload.RentalItemId ||
            !string.Equals(stored.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stored.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stored.OperationKey, payload.OperationKey, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Result>(Error.Validation("AMOUNT_MISMATCH",
                "Số tiền quyết toán không khớp với yêu cầu đã gửi."));

        bool wasCompleted = item.Status == RentalItemStatus.Completed;

        var applied = item.RecordPaymentSettlementConfirmed(payload.SettlementRef, payload.CompletedAt, payload.OperationKey);
        if (applied.IsFailure) return Task.FromResult(applied);

        receipt.MarkSucceeded(now);   // chốt receipt nếu ACK chưa kịp ghi; gọi lại vô hại

        if (!wasCompleted && item.Status == RentalItemStatus.Completed)
            order.EvaluateOrderStatusRollup(now, payload.OperationKey);   // cập nhật order khi mọi item đã kết thúc

        return Task.FromResult(Result.Success());
    }
}
