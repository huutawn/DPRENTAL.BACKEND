#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Integration;

public sealed class RefundCompletedConsumer : IRefundCompletedConsumer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IInboxRepository _inboxRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RefundCompletedConsumer> _logger;

    public RefundCompletedConsumer(
        IRentalOrderRepository orderRepository,
        IInboxRepository inboxRepository,
        IOrderLockManager lockManager,
        IUnitOfWork unitOfWork,
        ILogger<RefundCompletedConsumer> logger,
        TimeProvider? timeProvider = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result> ProcessRefundCompletedAsync(
        PaymentEventEnvelope<RefundCompletedPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        // 1. Envelope validation
        if (envelope == null || envelope.Payload == null)
        {
            return Error.Validation("INVALID_ENVELOPE", "Envelope không được rỗng.");
        }

        if (envelope.Version != 1)
        {
            return Error.Validation("UNSUPPORTED_VERSION", "Chỉ hỗ trợ envelope version 1.");
        }

        if (!string.Equals(envelope.MessageType, "RefundCompleted", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("INVALID_MESSAGE_TYPE", "MessageType phải là RefundCompleted.");
        }

        if (!string.Equals(envelope.Producer, "payment-service", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("INVALID_PRODUCER", "Producer phải là payment-service.");
        }

        var payload = envelope.Payload;
        if (!Guid.TryParse(envelope.AggregateId, out var aggId) || aggId != payload.RentalOrderId)
        {
            return Error.Validation("AGGREGATE_ID_MISMATCH", "AggregateId không khớp với RentalOrderId.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // 2. Lock FIRST
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(payload.RentalOrderId, TimeSpan.FromSeconds(5), cancellationToken);

        // 3. Deduplication in Inbox under lock
        string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        string payloadFingerprint = ComputeHash(payloadJson);

        var existingInbox = await _inboxRepository.GetByMessageIdAsync(envelope.MessageId, cancellationToken);
        if (existingInbox != null)
        {
            if (string.Equals(existingInbox.PayloadFingerprint, payloadFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("RefundCompleted message {MessageId} replayed idempotently", envelope.MessageId);
                return Result.Success();
            }
            return Error.Conflict("REFUND_CONFLICT", "MessageId đã tồn tại với nội dung khác.");
        }

        // 4. Load Order with details
        var order = await _orderRepository.GetByIdWithDetailsAsync(payload.RentalOrderId, cancellationToken);
        if (order == null)
        {
            return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{payload.RentalOrderId}'.");
        }

        // 5. Validate references & currency
        if (!string.IsNullOrWhiteSpace(order.InvoiceRef) &&
            !string.Equals(order.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Conflict("INVOICE_REF_MISMATCH", "InvoiceRef trong sự kiện hoàn tiền không khớp với đơn hàng.");
        }

        if (!string.IsNullOrWhiteSpace(order.PaymentRef) &&
            !string.Equals(order.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Conflict("PAYMENT_REF_MISMATCH", "PaymentRef trong sự kiện hoàn tiền không khớp với đơn hàng.");
        }

        if (!string.Equals(payload.Currency, "VND", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("CURRENCY_MISMATCH", "Tiền tệ phải là VND.");
        }

        // 6. Validate operation request matching
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == payload.OperationKey);
        if (receipt == null || receipt.Action != "RequestRefund")
        {
            return Error.Validation("INVALID_OPERATION_KEY", "Không tìm thấy yêu cầu hoàn tiền tương ứng trong đơn hàng.");
        }

        // Validate items membership, uniqueness, and breakdown amounts with stored request
        if (!string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            try
            {
                var storedRequest = JsonSerializer.Deserialize<RequestRefundRequest>(receipt.PayloadJson, JsonOptions);
                if (storedRequest != null)
                {
                    // Check item membership and uniqueness
                    var payloadItemIds = payload.Items.Select(i => i.RentalItemId).ToList();
                    if (payloadItemIds.Distinct().Count() != payloadItemIds.Count)
                    {
                        return Error.Validation("DUPLICATE_ITEM_REFUND", "Sự kiện chứa RentalItemId trùng lặp.");
                    }

                    foreach (var pItem in payload.Items)
                    {
                        var storedItem = storedRequest.Items.FirstOrDefault(i => i.RentalItemId == pItem.RentalItemId);
                        if (storedItem == null)
                        {
                            return Error.Validation("ITEM_MEMBERSHIP_MISMATCH", $"Mục thuê {pItem.RentalItemId} không thuộc yêu cầu hoàn tiền.");
                        }

                        if (storedItem.RefundRentalAmount != pItem.RefundedRentalAmount ||
                            storedItem.RefundDepositAmount != pItem.RefundedDepositAmount)
                        {
                            return Error.Validation("AMOUNT_BREAKDOWN_MISMATCH", $"Số tiền hoàn của mục {pItem.RentalItemId} không khớp với yêu cầu.");
                        }
                    }

                    if (storedRequest.TotalRefundAmount != payload.TotalRefundedAmount)
                    {
                        return Error.Validation("TOTAL_REFUND_MISMATCH", "Tổng số tiền hoàn không khớp với yêu cầu.");
                    }
                }
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                _logger.LogWarning(ex, "Failed to parse stored RequestRefund payload for {OpKey}", receipt.OperationId);
            }
        }

        // 7. Mutate data: Record refund reference and financial audit for each refunded item
        foreach (var refundedItem in payload.Items)
        {
            var item = order.Items.FirstOrDefault(i => i.Id == refundedItem.RentalItemId);
            if (item != null)
            {
                item.RecordRefund(payload.RefundRef);

                order.AddStateTransition(new RentalStateTransition(
                    Guid.NewGuid(),
                    order.Id,
                    item.Id,
                    item.Status.ToString(),
                    item.Status.ToString(),
                    "RefundCompleted",
                    null,
                    ActorRole.System,
                    $"Refund confirmed: fee {refundedItem.RefundedRentalAmount:N0} VND, deposit {refundedItem.RefundedDepositAmount:N0} VND (RefundRef: {payload.RefundRef})",
                    utcNow,
                    payload.OperationKey));
            }
        }

        receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(payload, JsonOptions));

        var inboxMessage = new RentalInboxMessage(
            Guid.NewGuid(),
            envelope.MessageId,
            envelope.MessageType,
            envelope.Producer,
            envelope.AggregateId,
            envelope.CorrelationId,
            envelope.CausationId,
            payloadJson,
            utcNow,
            payloadFingerprint);
        inboxMessage.MarkProcessed(utcNow);
        await _inboxRepository.AddAsync(inboxMessage, cancellationToken);

        order.IncrementVersion();
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Processed RefundCompleted for Order {OrderId}, total refund: {Amount} VND (RefundRef: {RefundRef})",
            order.Id, payload.TotalRefundedAmount, payload.RefundRef);

        return Result.Success();
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
