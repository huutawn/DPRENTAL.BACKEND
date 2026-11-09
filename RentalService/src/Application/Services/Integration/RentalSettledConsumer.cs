#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.Integration;

/// <remarks>
/// Áp dụng RentalSettled từ Payment vào order/item và receipt liên quan.
/// Nhánh Staged để dispatcher commit kết quả nghiệp vụ cùng Inbox; nhận yêu cầu settlement chưa phải đã quyết toán.
/// </remarks>
public sealed class RentalSettledConsumer(
    IRentalEventProducer eventProducer,
    TimeProvider timeProvider) : IRentalSettledConsumer
{
    // Chỉ hoàn tất item khi Inventory đã xác nhận đóng và event Payment khớp journal; phát event hoàn tất đúng lúc state thay đổi.
    public async Task<Result<bool>> ApplyRentalSettledStagedAsync(
        RentalOrder order, KafkaMessageEnvelope<RentalSettledPayload> envelope,
        RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default)
    {
        // Lấy payload của envelope đã đi qua bước kiểm tra đầu vào để xử lý cùng một bản tin xuyên suốt use case.
        var payload = envelope.Payload;
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (payload.RentalOrderId != order.Id || payload.RentalItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.SettlementRef) || string.IsNullOrWhiteSpace(payload.OperationKey) ||
            payload.CompletedAt == default)
            return Error.Validation("INVALID_SETTLEMENT_EVENT", "Settlement event does not identify this order and item.");

        // Đối chiếu số tiền và tiền tệ theo dữ liệu của luồng; không chấp nhận chênh lệch khi ghi nhận kết quả tài chính.
        if (!string.Equals(payload.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            return Error.Validation("CURRENCY_MISMATCH", "Tiền tệ phải là VND.");
        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (!string.IsNullOrWhiteSpace(order.InvoiceRef) &&
            !string.Equals(order.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase))
            return Error.Conflict("INVOICE_REF_MISMATCH", "InvoiceRef không khớp với đơn hàng.");
        // Đối chiếu tham chiếu thanh toán để tách khoản tiền đã ghi nhận khỏi một thanh toán khác hoặc sự kiện lặp.
        if (!string.IsNullOrWhiteSpace(order.PaymentRef) &&
            !string.Equals(order.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase))
            return Error.Conflict("PAYMENT_REF_MISMATCH", "PaymentRef không khớp với đơn hàng.");
        var item = order.Items.FirstOrDefault(i => i.Id == payload.RentalItemId);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (item is null)
            return Error.Validation("ITEM_MEMBERSHIP_MISMATCH",
                $"Mục thuê {payload.RentalItemId} không thuộc đơn hàng.");
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (!item.InventoryClosureConfirmedAt.HasValue)
            return Error.Conflict("INVENTORY_CLOSURE_PENDING",
                "Inventory has not confirmed closure for all returned assets.");
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == payload.OperationKey);
        // Đối chiếu action, phạm vi và fingerprint; cùng key nhưng khác yêu cầu phải báo conflict thay vì dùng kết quả cũ.
        if (receipt is null || receipt.Action != RentalItem.SettleRentalAction || receipt.TargetItemId != item.Id)
            return Error.Validation("INVALID_OPERATION_KEY",
                "Không tìm thấy yêu cầu quyết toán tương ứng trong đơn hàng.");
        var stored = RentalOperationExecutor.ReadStoredProviderRequest<SettleRentalRequest>(receipt.PayloadJson);
        // Kiểm tra dữ liệu tùy chọn hoặc kết quả tra cứu trước khi dùng; xử lý riêng trường hợp không có giá trị.
        if (stored is null)
            return Error.Validation("CORRUPTED_STORED_REQUEST",
                "Stored SettleRental payload bị hỏng; không thể đối soát.");
        // Kiểm tra mã hóa đơn đã được gắn/khớp trước khi thao tác Payment, tránh xử lý nhầm hoặc tạo hóa đơn thay thế.
        if (stored.FinalDeductionAmount != payload.FinalDeductionAmount ||
            stored.RefundedDepositAmount != payload.RefundedDepositAmount ||
            stored.RentalOrderId != payload.RentalOrderId || stored.RentalItemId != payload.RentalItemId ||
            !string.Equals(stored.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stored.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stored.OperationKey, payload.OperationKey, StringComparison.OrdinalIgnoreCase))
            return Error.Validation("AMOUNT_MISMATCH",
                "Số tiền quyết toán không khớp với yêu cầu đã gửi.");
        bool wasCompleted = item.Status == RentalItemStatus.Completed;
        RentalOrderStatus previousOrderStatus = order.Status;

        // Ghi thay đổi của quyết định hiện tại vào aggregate; persistence và các xác nhận bên ngoài được xử lý ở các bước tiếp theo.
        var applied = item.RecordPaymentSettlementConfirmed(payload.SettlementRef, payload.CompletedAt, payload.OperationKey);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (applied.IsFailure) return applied.Error;
        receipt.MarkSucceeded(now);   // chốt receipt nếu ACK chưa kịp ghi; gọi lại vô hại

        // So trạng thái sau áp dụng với trạng thái trước đó để chỉ phát event hoàn tất khi có chuyển trạng thái thực sự.
        bool isNowCompleted = item.Status == RentalItemStatus.Completed;

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (wasCompleted)
        {
            return Result.Success(false);
        }

        // Tổng hợp lại trạng thái order từ các item sau quyết định; không ép các item khác đi theo một trạng thái chung.
        RentalOrderStatusPolicy.Apply(order, now, payload.OperationKey);
        order.RecordReadModelUpdate(now);

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (!wasCompleted && isNowCompleted)
        {
            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            if (!item.CompletedAt.HasValue)
            {
                return Error.Failure("COMPLETED_AT_MISSING",
                    "Mục thuê chuyển sang Completed nhưng không có timestamp CompletedAt hợp lệ.");
            }

            DateTimeOffset itemCompletedAt = item.CompletedAt.Value;
            var enqueueResult = await eventProducer.EnqueueRentalItemCompletedAsync(
                new RentalItemCompletedPayload(
                    order.RenterId,
                    item.EquipmentListingId,
                    item.EquipmentTypeId,
                    order.Id,
                    item.Id,
                    order.Version,
                    itemCompletedAt),
                correlationId: envelope.CorrelationId,
                causationId: envelope.MessageId,
                cancellationToken: cancellationToken);

            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (enqueueResult.IsFailure)
            {
                return enqueueResult.Error;
            }
        }

        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (previousOrderStatus != RentalOrderStatus.Completed && order.Status == RentalOrderStatus.Completed)
        {
            // Ghi event/command vào Outbox; enqueue thành công chỉ xác nhận đã lưu ý định gửi, chưa xác nhận service đích đã thực thi.
            var orderCompletedResult = await eventProducer.EnqueueRentalOrderCompletedAsync(
                new RentalOrderCompletedPayload(order.Id, order.RenterId, payload.CompletedAt, order.Version),
                envelope.CorrelationId, envelope.MessageId, cancellationToken);
            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (orderCompletedResult.IsFailure) return orderCompletedResult.Error;
        }
        return Result.Success(true);
    }
}
