#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.IServices.Integration;
using Microsoft.Extensions.Logging;
using Dprental.RentalService.Application.Policies;

namespace Dprental.RentalService.Application.Services.Integration;

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

    public async Task<Result<bool>> ApplyRefundCompletedStagedAsync(
        RentalOrder order,
        KafkaMessageEnvelope<RefundCompletedPayload> envelope,
        RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default)
    {
        var payload = envelope.Payload;
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // 1. Validate references & currency
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

        // 2. Validate operation receipt matching
        var receipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == payload.OperationKey);
        if (receipt == null || receipt.Action != "RequestRefund")
        {
            return Error.Validation("INVALID_OPERATION_KEY", "Không tìm thấy yêu cầu hoàn tiền tương ứng trong đơn hàng.");
        }

        // 3. Stored refund request must exist, parse, and validate
        if (string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            return Error.Validation("MISSING_STORED_REQUEST", "Thiếu request payload JSON cho RequestRefund.");
        }

        RequestRefundRequest? storedRequest = null;
        try
        {
            using var doc = JsonDocument.Parse(receipt.PayloadJson);
            if (doc.RootElement.TryGetProperty("providerRequestJson", out var providerJsonProp))
            {
                string? providerJson = providerJsonProp.GetString();
                if (!string.IsNullOrWhiteSpace(providerJson))
                {
                    storedRequest = JsonSerializer.Deserialize<RequestRefundRequest>(providerJson, JsonOptions);
                }
            }
            else
            {
                storedRequest = JsonSerializer.Deserialize<RequestRefundRequest>(receipt.PayloadJson, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse stored RequestRefund payload for {OpKey}", receipt.OperationId);
            return Error.Validation("CORRUPTED_STORED_REQUEST", "Stored RequestRefund payload bị hỏng; không thể đối soát.");
        }

        if (storedRequest == null)
        {
            return Error.Validation("CORRUPTED_STORED_REQUEST", "Không thể trích xuất RequestRefund request từ journal.");
        }

        // Validate item membership, uniqueness, breakdown, and totals
        var payloadItemIds = payload.Items.Select(i => i.RentalItemId).ToList();
        if (payloadItemIds.Distinct().Count() != payloadItemIds.Count)
        {
            return Error.Validation("DUPLICATE_ITEM_REFUND", "Sự kiện chứa RentalItemId trùng lặp.");
        }

        if (payload.Items.Count != storedRequest.Items.Count)
        {
            return Error.Validation("ITEM_COUNT_MISMATCH", "Số lượng mục hoàn tiền trong sự kiện không khớp với yêu cầu.");
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

        // 4. Validate against stored ACK if present
        if (!string.IsNullOrWhiteSpace(receipt.ResponseJson))
        {
            try
            {
                var ack = JsonSerializer.Deserialize<RequestRefundResponse>(receipt.ResponseJson, JsonOptions);
                if (ack != null && !string.IsNullOrWhiteSpace(ack.RefundRef) &&
                    !string.Equals(ack.RefundRef, payload.RefundRef, StringComparison.OrdinalIgnoreCase))
                {
                    return Error.Conflict("REFUND_REF_MISMATCH", $"RefundRef ({payload.RefundRef}) không khớp với ACK đã lưu ({ack.RefundRef}).");
                }
            }
            catch
            {
                // Ignore parse errors on responseJson if non-fatal
            }
        }

        // 5. Check if already completed (RefundCompletionJson already recorded)
        if (!string.IsNullOrWhiteSpace(receipt.RefundCompletionJson))
        {
            try
            {
                var existingCompletion = JsonSerializer.Deserialize<RefundCompletedPayload>(receipt.RefundCompletionJson, JsonOptions);
                if (existingCompletion != null &&
                    string.Equals(existingCompletion.RefundRef, payload.RefundRef, StringComparison.OrdinalIgnoreCase) &&
                    existingCompletion.TotalRefundedAmount == payload.TotalRefundedAmount)
                {
                    _logger.LogInformation("RefundCompleted event {MessageId} replayed for already completed refund {RefundRef}",
                        envelope.MessageId, payload.RefundRef);
                    return Result.Success(false);
                }
            }
            catch
            {
                // Fall through to conflict
            }
            return Error.Conflict("REFUND_COMPLETION_CONFLICT", "Sự kiện hoàn tiền mâu thuẫn với kết quả hoàn tiền đã được ghi nhận trước đó.");
        }

        // 6. Mutate data: Record refund reference and financial audit once
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

        // Save immutable RefundCompletionJson separately from ACK ResponseJson
        receipt.SetRefundCompletion(JsonSerializer.Serialize(payload, JsonOptions), utcNow);
        receipt.MarkSucceeded(utcNow);

        RentalOrderStatusPolicy.Apply(order, utcNow, payload.OperationKey);
        order.RecordReadModelUpdate(utcNow);

        return Result.Success(true);
    }

    public async Task<Result> ProcessRefundCompletedAsync(
        KafkaMessageEnvelope<RefundCompletedPayload> envelope,
        CancellationToken cancellationToken = default)
    {
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

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(payload.RentalOrderId, TimeSpan.FromSeconds(5), cancellationToken);

        string rawPayload = JsonSerializer.Serialize(envelope, JsonOptions);
        string payloadFingerprint = ComputeHash(rawPayload);

        var existingInbox = await _inboxRepository.GetByMessageIdAsync(envelope.MessageId, cancellationToken);
        if (existingInbox != null && existingInbox.Status == RentalInboxMessage.StatusProcessed)
        {
            return Result.Success();
        }

        var order = await _orderRepository.GetByIdWithDetailsAsync(payload.RentalOrderId, cancellationToken);
        if (order == null)
        {
            return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{payload.RentalOrderId}'.");
        }

        var inboxMessage = existingInbox ?? new RentalInboxMessage(
            Guid.NewGuid(),
            envelope.MessageId,
            envelope.MessageType,
            envelope.Producer,
            envelope.AggregateId,
            envelope.CorrelationId,
            envelope.CausationId,
            rawPayload,
            utcNow,
            payloadFingerprint);

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            if (existingInbox == null)
            {
                await _inboxRepository.AddAsync(inboxMessage, cancellationToken);
            }

            var result = await ApplyRefundCompletedStagedAsync(order, envelope, inboxMessage, cancellationToken);
            if (result.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return result;
            }

            inboxMessage.MarkProcessed(utcNow);
            _inboxRepository.Update(inboxMessage);

            if (result.Value)
            {
                await _orderRepository.UpdateAsync(order, cancellationToken);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            _logger.LogError(ex, "Transaction failed while processing RefundCompleted message {MessageId}", envelope.MessageId);
            throw;
        }
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
