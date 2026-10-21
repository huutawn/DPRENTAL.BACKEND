#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor
{
    private const string CancelOrderAction = "CancelOrder";
    private const string DefaultRenterCancelReason = "Cancelled by renter before payment";
    private const int MaxCancelReasonLength = 500;

    /// <summary>
    /// Renter hủy cả đơn khi đang PendingPayment.
    /// Thứ tự: kiểm tra quyền sở hữu, khóa order, idempotency, kiểm tra PendingPayment,
    /// hủy invoice (hoặc chặn nếu invoice đang được tạo), xếp lệnh release từng allocation vào outbox,
    /// rồi mới lưu trạng thái Cancelled, receipt và outbox event trong một lần commit.
    /// </summary>
    public async Task<Result<CancelOrderResponse>> ExecuteRenterCancelOrderAsync(
        Guid orderId,
        Guid renterId,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (renterId == Guid.Empty)
        {
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu danh tính người thuê hợp lệ.");
        }

        string normalizedKey = idempotencyKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key là bắt buộc và tối đa 64 ký tự.");
        }

        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? DefaultRenterCancelReason : reason.Trim();
        if (normalizedReason.Length > MaxCancelReasonLength)
        {
            return Error.Validation("INVALID_CANCEL_REASON", $"Lý do hủy tối đa {MaxCancelReasonLength} ký tự.");
        }

        // Kiểm tra sở hữu trước khi giữ lock để người ngoài không chiếm lock của đơn người khác.
        Guid? ownerId = await _orderRepository.GetRenterIdByOrderIdAsync(orderId, cancellationToken);
        if (ownerId != renterId)
        {
            return Error.NotFound("Order.NotFound", "Không tìm thấy đơn hàng.");
        }

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null || order.RenterId != renterId)
        {
            return Error.NotFound("Order.NotFound", "Không tìm thấy đơn hàng.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        string fingerprint = ComputeFingerprint(new
        {
            Action = CancelOrderAction,
            OrderId = orderId,
            RenterId = renterId,
            Reason = normalizedReason
        });

        // 1. Idempotency: cùng khóa + cùng nội dung trả lại kết quả đã lưu.
        var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == normalizedKey);
        if (existingReceipt != null)
        {
            if (existingReceipt.Action != CancelOrderAction ||
                !string.Equals(existingReceipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
            }

            if (existingReceipt.IsSuccess &&
                existingReceipt.Status == RentalOperationReceipt.StatusSucceeded &&
                !string.IsNullOrWhiteSpace(existingReceipt.ApiResponseJson))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<CancelOrderResponse>(existingReceipt.ApiResponseJson, JsonOptions);
                    if (stored != null) return stored;
                }
                catch (JsonException)
                {
                    return Error.Failure("CORRUPTED_JOURNAL_RESPONSE", "Dữ liệu phản hồi trong journal không hợp lệ.");
                }
            }

            return Error.Conflict("IDEMPOTENCY_CONFLICT", "Khóa idempotency đã được sử dụng với hành động hoặc nội dung khác.");
        }

        // 2. Chỉ hủy được khi đang chờ thanh toán.
        if (order.Status != RentalOrderStatus.PendingPayment)
        {
            return Error.Conflict("Order.NotPendingPayment", $"Đơn hàng không ở trạng thái PendingPayment (trạng thái hiện tại: {order.Status}).");
        }

        // 3. Invoice: hủy nếu đã có InvoiceRef; nếu chưa có thì phải chứng minh invoice không đang được tạo.
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
                    payloadJson: JsonSerializer.Serialize(new CancelInvoiceRequest(order.Id, order.InvoiceRef, normalizedReason, cancelOpKey), JsonOptions));
                order.AddOperationReceipt(cancelReceipt);
                await CommitLocalChangesAsync(order, cancellationToken);
            }

            var cancelRes = await CancelInvoiceInternalAsync(order, cancelReceipt, cancelOpKey, cancellationToken);
            if (cancelRes.IsFailure)
            {
                if (string.Equals(cancelRes.Error.Code, PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Invoice was already paid for Order {OrderId}. Halting renter cancel and waiting for InvoicePaid event.", order.Id);
                    return Error.Conflict(PaymentContractSpec.ErrorCodes.InvoiceAlreadyPaid, "Hóa đơn đã được thanh toán; không thể hủy đơn, hệ thống sẽ đối soát thanh toán.");
                }

                return cancelRes.Error;
            }
        }
        else
        {
            if (!IsInvoiceCreationSafeToAbandon(order))
            {
                return Error.ServiceUnavailable(
                    "Order.InvoiceCreationInProgress",
                    "Hóa đơn của đơn hàng đang được tạo hoặc chưa rõ kết quả; vui lòng thử lại cùng Idempotency-Key sau ít phút.");
            }

            AbandonPendingInvoiceCreation(order, utcNow);
            await CommitLocalChangesAsync(order, cancellationToken);
        }

        // 4. Release từng allocation (cùng operation key với luồng payment-expiry để không tạo trùng).
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
                    payloadJson: JsonSerializer.Serialize(new ReleaseAllocationRequest(releaseKey, normalizedReason), JsonOptions));
                releaseReceipt.SetInventoryOperationKey(
                    ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.AllocationGroupId}"));
                order.AddOperationReceipt(releaseReceipt);
            }

            releaseKeys.Add((item, releaseKey));
        }

        await CommitLocalChangesAsync(order, cancellationToken);

        bool allReleaseCommandsQueued = true;
        foreach (var (item, releaseKey) in releaseKeys)
        {
            var releaseReceipt = order.OperationReceipts.First(r => r.OperationId == releaseKey);
            var relRes = await ReleaseAllocationInternalAsync(order, item, releaseReceipt, releaseKey, cancellationToken);
            if (relRes.IsFailure) allReleaseCommandsQueued = false;
        }

        if (!allReleaseCommandsQueued)
        {
            return Error.ServiceUnavailable("RELEASE_PENDING", "Một số lệnh giải phóng phân bổ chưa được gửi; vui lòng thử lại cùng Idempotency-Key.");
        }

        // 5. Chỉ đánh dấu Cancelled sau khi invoice đã hủy (hoặc không có) và mọi release đã được enqueue.
        // Inventory xác nhận bất đồng bộ; trạng thái assignment vẫn được giữ cho đến callback thành công.
        utcNow = _timeProvider.GetUtcNow();
        order.MarkCancelled(utcNow, normalizedKey, normalizedReason, renterId, ActorRole.Renter);
        order.RecordReadModelUpdate(utcNow);

        var response = new CancelOrderResponse(order.Id, order.OrderCode, order.Status.ToString(), utcNow);

        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            normalizedKey,
            CancelOrderAction,
            fingerprint,
            isSuccess: true,
            errorCode: null,
            errorMessage: null,
            createdAt: utcNow,
            status: RentalOperationReceipt.StatusSucceeded,
            completedAt: utcNow,
            apiResponseJson: JsonSerializer.Serialize(response, JsonOptions)));

        var envelope = new KafkaMessageEnvelope<RentalOrderCancelledPayload>(
            OutboxMessageIdGenerator.ComputeDeterministicId(
                KafkaContractSpec.Producers.RentalService,
                KafkaContractSpec.EventTypes.RentalOrderCancelled,
                normalizedKey),
            KafkaContractSpec.EventTypes.RentalOrderCancelled,
            1,
            KafkaContractSpec.Producers.RentalService,
            order.Id.ToString("D"),
            utcNow,
            normalizedKey,
            null,
            new RentalOrderCancelledPayload(order.Id, order.RenterId, utcNow, normalizedReason));

        await _outboxRepository.AddAsync(
            new RentalOutboxMessage(
                envelope.MessageId,
                _rentalEventsTopic,
                order.Id.ToString("D"),
                KafkaContractSpec.EventTypes.RentalOrderCancelled,
                JsonSerializer.Serialize(envelope, JsonOptions),
                utcNow),
            cancellationToken);

        await CommitLocalChangesAsync(order, cancellationToken);
        return response;
    }

    /// <summary>
    /// Khi chưa có InvoiceRef, chỉ coi là an toàn để hủy nếu CHỨNG MINH được chưa có lần gọi tạo invoice nào
    /// có thể đã thành công phía Payment: mọi receipt CreateInvoice chưa từng attempt, hoặc đã thất bại
    /// dứt khoát (Business, không uncertain). Không có receipt CreateInvoice nào, hoặc receipt đang Processing,
    /// uncertain, retry-transient, exhausted => không an toàn, vì Payment có thể đã tạo invoice mà Rental chưa lưu InvoiceRef.
    /// </summary>
    private static bool IsInvoiceCreationSafeToAbandon(RentalOrder order)
    {
        var createReceipts = order.OperationReceipts.Where(r => r.Action == "CreateInvoice").ToList();
        if (createReceipts.Count == 0)
        {
            return false;
        }

        foreach (var r in createReceipts)
        {
            bool neverAttempted =
                r.Status == RentalOperationReceipt.StatusPending &&
                r.RetryCount == 0 &&
                !r.LastAttemptedAt.HasValue &&
                !r.IsOutcomeUncertain;

            bool definitivelyFailed =
                r.Status == RentalOperationReceipt.StatusFailed &&
                r.ErrorClassification == RentalOperationReceipt.ClassificationBusiness &&
                !r.IsOutcomeUncertain;

            if (!neverAttempted && !definitivelyFailed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Đóng các receipt CreateInvoice chưa attempt để worker không tạo invoice cho đơn đã hủy.
    /// Chỉ gọi sau khi <see cref="IsInvoiceCreationSafeToAbandon"/> trả true.
    /// </summary>
    private static void AbandonPendingInvoiceCreation(RentalOrder order, DateTimeOffset utcNow)
    {
        foreach (var r in order.OperationReceipts.Where(r =>
                     r.Action == "CreateInvoice" && r.Status == RentalOperationReceipt.StatusPending))
        {
            r.MarkFailed(
                "ORDER_CANCELLED",
                "Order cancelled by renter before invoice creation was attempted.",
                utcNow,
                RentalOperationReceipt.ClassificationBusiness,
                isTransient: false);
        }
    }
}
