#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Payments;

public sealed class PaymentWorkflowService : IPaymentWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRentalOrderRepository _orderRepository;
    private readonly IInboxRepository _inboxRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOperationReceiptRepository _receiptRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentWorkflowService> _logger;
    private readonly string _rentalEventsTopic;

    public PaymentWorkflowService(
        IRentalOrderRepository orderRepository,
        IInboxRepository inboxRepository,
        IOutboxRepository outboxRepository,
        IOperationReceiptRepository receiptRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<PaymentWorkflowService> logger,
        TimeProvider? timeProvider = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _receiptRepository = receiptRepository ?? throw new ArgumentNullException(nameof(receiptRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
    }

    public async Task<Result> ProcessInvoicePaidAsync(
        PaymentEventEnvelope<InvoicePaidPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        // 1. Envelope validation
        var envValidation = ValidateEnvelope(envelope);
        if (envValidation.IsFailure)
        {
            _logger.LogWarning("InvoicePaid envelope validation failed: {Code} - {Msg}",
                envValidation.Error.Code, envValidation.Error.Message);
            return envValidation;
        }

        var payload = envelope.Payload;
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        List<(RentalItem item, string opKey)> operationsToExecute;
        bool isLatePayment;

        // 2. Local transaction under lock
        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(
            payload.RentalOrderId,
            TimeSpan.FromSeconds(5),
            cancellationToken))
        {
            // Deduplication under lock
            string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            string payloadFingerprint = ComputeHash(payloadJson);

            var existingInbox = await _inboxRepository.GetByMessageIdAsync(envelope.MessageId, cancellationToken);
            if (existingInbox != null)
            {
                if (string.Equals(existingInbox.PayloadFingerprint, payloadFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("InvoicePaid message {MessageId} replayed idempotently", envelope.MessageId);
                    return Result.Success();
                }
                return Error.Conflict("PAYMENT_CONFLICT", "MessageId đã tồn tại với nội dung thanh toán khác.");
            }

            // Load Order Aggregate with details
            var order = await _orderRepository.GetByIdWithDetailsAsync(payload.RentalOrderId, cancellationToken);
            if (order == null)
            {
                return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{payload.RentalOrderId}'.");
            }

            // Invoice must be already attached to Order
            if (string.IsNullOrWhiteSpace(order.InvoiceRef))
            {
                return Error.Failure("INVOICE_NOT_ATTACHED", "Đơn hàng chưa được gắn mã invoice từ Checkout; giữ event để retry.");
            }

            if (!string.Equals(order.InvoiceRef, payload.InvoiceRef, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("INVOICE_MISMATCH", $"InvoiceRef ({payload.InvoiceRef}) không khớp với đơn hàng ({order.InvoiceRef}).");
            }

            // Validate snapshot matching
            if (order.RenterId != payload.RenterId)
            {
                return Error.Validation("RENTER_MISMATCH", "RenterId trong sự kiện thanh toán không khớp với đơn hàng.");
            }

            if (!string.Equals(order.Currency, payload.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Validation("CURRENCY_MISMATCH", $"Tiền tệ thanh toán ({payload.Currency}) không khớp với đơn hàng ({order.Currency}).");
            }

            if (order.TotalPayableAmount != payload.PaidAmount)
            {
                return Error.Validation("AMOUNT_MISMATCH", $"Số tiền thanh toán ({payload.PaidAmount}) không khớp với tổng tiền đơn hàng ({order.TotalPayableAmount}).");
            }

            // Payment identity check
            if (!string.IsNullOrWhiteSpace(order.PaymentRef))
            {
                if (string.Equals(order.PaymentRef, payload.PaymentRef, StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Success();
                }
                return Error.Conflict("PAYMENT_REF_CONFLICT", "Đơn hàng đã được thanh toán với mã tham chiếu thanh toán khác.");
            }

            // Lifecycle branching
            isLatePayment = order.Status is RentalOrderStatus.Expired or RentalOrderStatus.Cancelled ||
                            payload.PaidAt >= order.PaymentDeadline;

            if (isLatePayment)
            {
                var prepRes = await PrepareLatePaymentAsync(order, envelope, payloadFingerprint, utcNow, cancellationToken);
                if (prepRes.IsFailure) return prepRes.Error;
                operationsToExecute = prepRes.Value;
            }
            else
            {
                var prepRes = await PrepareOnTimePaymentAsync(order, envelope, payloadFingerprint, utcNow, cancellationToken);
                if (prepRes.IsFailure) return prepRes.Error;
                operationsToExecute = prepRes.Value;
            }
        }

        // 3. Lock released! Execute operations outside lock
        if (isLatePayment)
        {
            foreach (var (item, opKey) in operationsToExecute)
            {
                await _operationExecutor.ExecuteRequestRefundAsync(payload.RentalOrderId, item.Id, opKey, cancellationToken);
            }
        }
        else
        {
            foreach (var (item, opKey) in operationsToExecute)
            {
                await _operationExecutor.ExecuteExtendHoldAsync(payload.RentalOrderId, item.Id, opKey, cancellationToken);
            }
        }

        return Result.Success();
    }

    private async Task<Result<List<(RentalItem item, string opKey)>>> PrepareLatePaymentAsync(
        RentalOrder order,
        PaymentEventEnvelope<InvoicePaidPayload> envelope,
        string payloadFingerprint,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var payload = envelope.Payload;
        _logger.LogWarning("Payment arrived late for Order {OrderId} (Status: {Status}, Deadline: {Deadline}, PaidAt: {PaidAt}). Executing late-payment refund.",
            order.Id, order.Status, order.PaymentDeadline, payload.PaidAt);

        order.MarkAsLatePayment(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);

        var inboxMessage = new RentalInboxMessage(
            Guid.NewGuid(),
            envelope.MessageId,
            envelope.MessageType,
            envelope.Producer,
            envelope.AggregateId,
            envelope.CorrelationId,
            envelope.CausationId,
            JsonSerializer.Serialize(payload, JsonOptions),
            utcNow,
            payloadFingerprint);
        inboxMessage.MarkProcessed(utcNow);
        await _inboxRepository.AddAsync(inboxMessage, cancellationToken);

        var refundOpKeys = new List<(RentalItem item, string opKey)>();
        foreach (var item in order.Items)
        {
            string refundKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
            var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundKey);
            if (existingReceipt == null)
            {
                var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
                var request = new RequestRefundRequest(order.Id, payload.InvoiceRef, payload.PaymentRef, refundItems, "Late payment arrived on terminal/expired order; 100% refund", refundKey);

                var refundReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(),
                    order.Id,
                    refundKey,
                    "RequestRefund",
                    RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                    isSuccess: false,
                    errorCode: null,
                    errorMessage: null,
                    createdAt: utcNow,
                    status: RentalOperationReceipt.StatusPending,
                    targetItemId: item.Id,
                    payloadJson: JsonSerializer.Serialize(request, JsonOptions));

                order.AddOperationReceipt(refundReceipt);
            }
            refundOpKeys.Add((item, refundKey));
        }

        var outboxEnvelope = new RentalEventEnvelope<LatePaymentRefundRequestedPayload>(
            Guid.NewGuid(),
            RentalEventTypes.LatePaymentRefundRequested,
            1,
            "rental-service",
            order.Id.ToString("D"),
            utcNow,
            envelope.CorrelationId,
            envelope.MessageId,
            new LatePaymentRefundRequestedPayload(
                order.Id,
                order.RenterId,
                payload.InvoiceRef,
                payload.PaymentRef,
                order.TotalPayableAmount,
                utcNow));

        var outboxMessage = new RentalOutboxMessage(
            outboxEnvelope.MessageId,
            _rentalEventsTopic,
            order.Id.ToString("D"),
            RentalEventTypes.LatePaymentRefundRequested,
            JsonSerializer.Serialize(outboxEnvelope, JsonOptions),
            utcNow);
        await _outboxRepository.AddAsync(outboxMessage, cancellationToken);

        order.IncrementVersion();
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return refundOpKeys;
    }

    private async Task<Result<List<(RentalItem item, string opKey)>>> PrepareOnTimePaymentAsync(
        RentalOrder order,
        PaymentEventEnvelope<InvoicePaidPayload> envelope,
        string payloadFingerprint,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var payload = envelope.Payload;

        var markResult = order.MarkAsPaid(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
        if (markResult.IsFailure) return markResult.Error;

        var inboxMessage = new RentalInboxMessage(
            Guid.NewGuid(),
            envelope.MessageId,
            envelope.MessageType,
            envelope.Producer,
            envelope.AggregateId,
            envelope.CorrelationId,
            envelope.CausationId,
            JsonSerializer.Serialize(payload, JsonOptions),
            utcNow,
            payloadFingerprint);
        inboxMessage.MarkProcessed(utcNow);
        await _inboxRepository.AddAsync(inboxMessage, cancellationToken);

        var extendHoldKeys = new List<(RentalItem item, string opKey)>();
        foreach (var item in order.Items)
        {
            string opKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
            var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
            if (existingReceipt == null)
            {
                var request = new ExtendHoldRequest(item.SupplierConfirmationDeadline!.Value, opKey);
                var receipt = new RentalOperationReceipt(
                    Guid.NewGuid(),
                    order.Id,
                    opKey,
                    "ExtendHold",
                    RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{item.AllocationGroupId}:{item.SupplierConfirmationDeadline:O}"),
                    isSuccess: false,
                    errorCode: null,
                    errorMessage: null,
                    createdAt: utcNow,
                    status: RentalOperationReceipt.StatusPending,
                    targetItemId: item.Id,
                    payloadJson: JsonSerializer.Serialize(request, JsonOptions));

                order.AddOperationReceipt(receipt);
            }
            extendHoldKeys.Add((item, opKey));
        }

        order.IncrementVersion();
        await _orderRepository.UpdateAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return extendHoldKeys;
    }

    private static Result ValidateEnvelope(PaymentEventEnvelope<InvoicePaidPayload> envelope)
    {
        if (envelope == null) return Error.Validation("INVALID_ENVELOPE", "Envelope không được rỗng.");
        if (envelope.Version != 1) return Error.Validation("UNSUPPORTED_VERSION", $"Chỉ hỗ trợ envelope version 1 (nhận {envelope.Version}).");
        if (!string.Equals(envelope.MessageType, "InvoicePaid", StringComparison.OrdinalIgnoreCase)) return Error.Validation("INVALID_MESSAGE_TYPE", $"MessageType không hợp lệ: {envelope.MessageType}.");
        if (!string.Equals(envelope.Producer, "payment-service", StringComparison.OrdinalIgnoreCase)) return Error.Validation("INVALID_PRODUCER", $"Producer không hợp lệ: {envelope.Producer}.");
        if (envelope.Payload == null) return Error.Validation("INVALID_PAYLOAD", "Payload không được rỗng.");
        if (!Guid.TryParse(envelope.AggregateId, out var aggId) || aggId != envelope.Payload.RentalOrderId) return Error.Validation("AGGREGATE_ID_MISMATCH", "AggregateId không khớp với RentalOrderId.");
        if (envelope.Payload.RentalOrderId == Guid.Empty) return Error.Validation("INVALID_ORDER_ID", "RentalOrderId không được để trống.");
        if (envelope.Payload.RenterId == Guid.Empty) return Error.Validation("INVALID_RENTER_ID", "RenterId không được để trống.");
        if (string.IsNullOrWhiteSpace(envelope.Payload.InvoiceRef)) return Error.Validation("INVALID_INVOICE_REF", "InvoiceRef không được để trống.");
        if (string.IsNullOrWhiteSpace(envelope.Payload.PaymentRef)) return Error.Validation("INVALID_PAYMENT_REF", "PaymentRef không được để trống.");
        if (!string.Equals(envelope.Payload.Currency, "VND", StringComparison.OrdinalIgnoreCase)) return Error.Validation("INVALID_CURRENCY", $"Tiền tệ không hợp lệ: {envelope.Payload.Currency}.");
        if (envelope.Payload.PaidAmount < 0) return Error.Validation("INVALID_PAID_AMOUNT", "Số tiền thanh toán không được âm.");

        return Result.Success();
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
