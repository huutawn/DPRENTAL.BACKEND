#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Payments;

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
    private readonly IInventoryCommandProducer _inventoryCommands;
    private readonly IRentalEventProducer _rentalEventProducer;
    public PaymentWorkflowService(
        IRentalOrderRepository orderRepository,
        IInboxRepository inboxRepository,
        IOutboxRepository outboxRepository,
        IOperationReceiptRepository receiptRepository,
        IOrderLockManager lockManager,
        IRentalOperationExecutor operationExecutor,
        IUnitOfWork unitOfWork,
        IInventoryCommandProducer inventoryCommands,
        IConfiguration configuration,
        ILogger<PaymentWorkflowService> logger,
        IRentalEventProducer rentalEventProducer,
        TimeProvider? timeProvider = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _receiptRepository = receiptRepository ?? throw new ArgumentNullException(nameof(receiptRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _inventoryCommands = inventoryCommands ?? throw new ArgumentNullException(nameof(inventoryCommands));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
        _rentalEventProducer = rentalEventProducer ?? throw new ArgumentNullException(nameof(rentalEventProducer));
    }

    public async Task<Result<bool>> ApplyInvoicePaidStagedAsync(
        RentalOrder order,
        KafkaMessageEnvelope<InvoicePaidPayload> envelope,
        RentalInboxMessage inboxMessage,
        CancellationToken cancellationToken = default)
    {
        var envValidation = ValidateEnvelope(envelope);
        if (envValidation.IsFailure) return envValidation.Error;

        var payload = envelope.Payload;
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

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
                return Result.Success(false);
            }
            return Error.Conflict("PAYMENT_REF_CONFLICT", "Đơn hàng đã được thanh toán với mã tham chiếu thanh toán khác.");
        }

        // 1. Actively cancelled order
        if (order.Status == RentalOrderStatus.Cancelled)
        {
            _logger.LogWarning("Payment arrived for cancelled Order {OrderId}. Staging full refund.", order.Id);
            order.MarkAsLatePayment(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
            await StageFullRefundAndReleaseAsync(order, payload, utcNow, cancellationToken);
            return Result.Success(true);
        }

        // 2. Calculate recovery cutoff
        DateTimeOffset paymentCutoff = order.PaymentDeadline.AddMinutes(15);
        DateTimeOffset supplierCutoff = order.Items
            .Where(i => i.SupplierConfirmationDeadline.HasValue)
            .Select(i => i.SupplierConfirmationDeadline!.Value)
            .DefaultIfEmpty(paymentCutoff)
            .Min();
        DateTimeOffset startOfDayCutoff = order.Items
            .Select(i => Clients.RentalInventoryDateAdapter.GetStartOfDayUtc(i.ExpectedStartDate))
            .Min();
        DateTimeOffset cutoff = paymentCutoff < supplierCutoff ? paymentCutoff : supplierCutoff;
        cutoff = cutoff < startOfDayCutoff ? cutoff : startOfDayCutoff;

        // 3. Determine if hold is still active
        bool isLatePayment = string.Equals(payload.PaymentDisposition, "LATE_REQUIRES_REFUND", StringComparison.OrdinalIgnoreCase) ||
                             order.Status == RentalOrderStatus.Expired ||
                             payload.PaidAt >= order.PaymentDeadline;

        if (!isLatePayment)
        {
            var markResult = order.MarkAsPaid(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
            if (markResult.IsFailure) return markResult.Error;

            order.RecordReadModelUpdate(utcNow);

            var orderPaidResult = await _rentalEventProducer.EnqueueRentalOrderPaidAsync(
                new RentalOrderPaidPayload(order.Id, order.RenterId, payload.InvoiceRef,
                    payload.PaymentRef, order.TotalPayableAmount, payload.PaidAt, order.Version),
                correlationId: envelope.CorrelationId,
                causationId: envelope.MessageId,
                cancellationToken: cancellationToken);
            if (orderPaidResult.IsFailure) return orderPaidResult.Error;

            foreach (var item in order.Items)
            {
                if (!item.EquipmentTypeId.HasValue || item.EquipmentTypeId.Value == Guid.Empty)
                {
                    return Error.Validation("INVALID_EQUIPMENT_TYPE_ID", $"EquipmentTypeId của rental item {item.Id} không hợp lệ.");
                }

                var enqueueResult = await _rentalEventProducer.EnqueueRentalItemPaidAsync(
                    new RentalItemPaidPayload(order.RenterId, item.EquipmentListingId,
                        item.EquipmentTypeId, order.Id, item.Id, order.Version),
                    correlationId: envelope.CorrelationId,
                    causationId: envelope.MessageId,
                    cancellationToken: cancellationToken);
                if (enqueueResult.IsFailure) return enqueueResult.Error;
                string opKey = RentalOperationExecutor.ComputeOperationKey("ExtendHold", $"{order.Id}:{item.Id}");
                var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == opKey);
                if (existingReceipt == null)
                {
                    DateTimeOffset newHoldExpiresAt = item.SupplierConfirmationDeadline is DateTimeOffset deadline
                        ? RentalItem.CalculateConfirmationHoldExpiry(deadline, item.ExpectedStartDate)
                        : payload.PaidAt.AddHours(24);
                    var extHoldReq = new ExtendHoldRequest(newHoldExpiresAt, opKey);
                    var extHoldCmd = new RentalOperationCommand(
                        1,
                        "ExtendHold",
                        order.Id,
                        item.Id,
                        opKey,
                        item.AllocationGroupId,
                        null,
                        JsonSerializer.Serialize(extHoldReq, JsonOptions));

                    var receipt = RentalOperationReceipt.CreatePending(
                        Guid.NewGuid(),
                        order.Id,
                        opKey,
                        "ExtendHold",
                        RentalOperationExecutor.ComputeFingerprint($"ExtendHold:{item.AllocationGroupId}:{newHoldExpiresAt:O}"),
                        utcNow,
                        item.Id,
                        JsonSerializer.Serialize(extHoldCmd, JsonOptions));

                    order.AddOperationReceipt(receipt);

                    var extendCmdPayload = new ExtendHoldCommandPayload(
                        order.Id,
                        item.Id,
                        opKey,
                        newHoldExpiresAt,
                        item.AllocationGroupId);
                    if (_inventoryCommands != null)
                    {
                        await _inventoryCommands.EnqueueExtendHoldAsync(extendCmdPayload, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                    }
                }
            }
        }
        else
        {
            if (utcNow >= cutoff)
            {
                _logger.LogWarning("Payment arrived after recovery cutoff for Order {OrderId}. Cannot recover. Staging full refund.", order.Id);
                order.MarkAsLatePayment(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
                await StageFullRefundAndReleaseAsync(order, payload, utcNow, cancellationToken);
                return Result.Success(true);
            }

            _logger.LogInformation("Payment arrived late/hold expired for Order {OrderId}. Initiating recovery re-allocation before cutoff {Cutoff}.", order.Id, cutoff);
            order.RecordPaymentReceived(payload.InvoiceRef, payload.PaymentRef, payload.PaidAt, utcNow);
            order.RecordReadModelUpdate(utcNow);
            foreach (var item in order.Items)
            {
                string recGroupId = $"rental-item:{item.Id}:rec-1";
                string recOpKey = RentalOperationExecutor.ComputeOperationKey("RecoveryHold", $"{order.Id}:{item.Id}");
                var existingReceipt = order.OperationReceipts.FirstOrDefault(r => r.OperationId == recOpKey);
                if (existingReceipt == null)
                {
                    DateTimeOffset holdExpiry = item.SupplierConfirmationDeadline is DateTimeOffset deadline
                        ? RentalItem.CalculateConfirmationHoldExpiry(deadline, item.ExpectedStartDate)
                        : cutoff;

                    var createHoldReq = new CreateHoldCommandPayload(
                        order.Id,
                        item.Id,
                        recOpKey,
                        item.EquipmentListingId,
                        item.Quantity,
                        item.ExpectedStartDate,
                        item.ExpectedEndDate,
                        holdExpiry,
                        Notes: "Recovery hold after payment",
                        AllocationGroupId: recGroupId,
                        ExecutionCutoffUtc: cutoff);

                    var receipt = RentalOperationReceipt.CreatePending(
                        Guid.NewGuid(),
                        order.Id,
                        recOpKey,
                        "RecoveryHold",
                        RentalOperationExecutor.ComputeFingerprint($"RecoveryHold:{recGroupId}"),
                        utcNow,
                        item.Id,
                        JsonSerializer.Serialize(createHoldReq, JsonOptions));

                    order.AddOperationReceipt(receipt);

                    if (_inventoryCommands != null)
                    {
                        await _inventoryCommands.EnqueueCreateHoldAsync(createHoldReq, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                    }
                }
            }
        }

        return Result.Success(true);
    }

    private async Task StageFullRefundAndReleaseAsync(
        RentalOrder order,
        InvoicePaidPayload payload,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        foreach (var item in order.Items)
        {
            string releaseOpKey = RentalOperationExecutor.ComputeOperationKey("ReleaseAllocation", $"{order.Id}:{item.Id}");
            var existingRelease = order.OperationReceipts.FirstOrDefault(r => r.OperationId == releaseOpKey);
            if (existingRelease == null)
            {
                var releaseReq = new ReleaseAllocationRequest(releaseOpKey, "Late payment arrived after deadline; releasing allocation");
                var releaseCmd = new RentalOperationCommand(
                    1,
                    "ReleaseAllocation",
                    order.Id,
                    item.Id,
                    releaseOpKey,
                    item.AllocationGroupId,
                    null,
                    JsonSerializer.Serialize(releaseReq, JsonOptions));

                var releaseReceipt = RentalOperationReceipt.CreatePending(
                    Guid.NewGuid(),
                    order.Id,
                    releaseOpKey,
                    "ReleaseAllocation",
                    RentalOperationExecutor.ComputeFingerprint($"ReleaseAllocation:{item.AllocationGroupId}"),
                    utcNow,
                    item.Id,
                    JsonSerializer.Serialize(releaseCmd, JsonOptions));

                order.AddOperationReceipt(releaseReceipt);

                var releaseCmdPayload = new ReleaseAllocationCommandPayload(
                    order.Id,
                    item.Id,
                    releaseOpKey,
                    "Late payment arrived after deadline; releasing allocation",
                    item.AllocationGroupId);
                if (_inventoryCommands != null)
                {
                    await _inventoryCommands.EnqueueReleaseAllocationAsync(releaseCmdPayload, correlationId: order.Id.ToString("D"), cancellationToken: cancellationToken);
                }
            }

            string refundKey = RentalOperationExecutor.ComputeOperationKey("RequestRefund", $"{order.Id}:{item.Id}");
            var existingRefund = order.OperationReceipts.FirstOrDefault(r => r.OperationId == refundKey);
            if (existingRefund == null)
            {
                var refundItems = new List<RefundItemRequest> { new(item.Id, item.RentalAmount, item.DepositAmount) };
                var refundReq = new RequestRefundRequest(order.Id, payload.InvoiceRef, payload.PaymentRef, refundItems, "Late payment arrived on terminal/expired order; 100% refund", refundKey);
                var refundCmd = new RentalOperationCommand(
                    1,
                    "RequestRefund",
                    order.Id,
                    item.Id,
                    refundKey,
                    null,
                    null,
                    JsonSerializer.Serialize(refundReq, JsonOptions));

                var refundReceipt = new RentalOperationReceipt(
                    Guid.NewGuid(),
                    order.Id,
                    refundKey,
                    "RequestRefund",
                    RentalOperationExecutor.ComputeFingerprint($"RequestRefund:{order.Id}:{item.RentalAmount + item.DepositAmount}"),
                    isSuccess: false,
                    errorCode: null,
                    errorMessage: null,
                    createdAt: utcNow,
                    status: RentalOperationReceipt.StatusPending,
                    targetItemId: item.Id,
                    prerequisiteOperationId: releaseOpKey,
                    payloadJson: JsonSerializer.Serialize(refundCmd, JsonOptions));

                order.AddOperationReceipt(refundReceipt);
            }
        }

        var enqRes = await _rentalEventProducer.EnqueueLatePaymentRefundRequestedAsync(
            new LatePaymentRefundRequestedPayload(
                order.Id,
                order.RenterId,
                payload.InvoiceRef,
                payload.PaymentRef,
                order.TotalPayableAmount,
                utcNow,
                order.Version),
            correlationId: order.Id.ToString("D"),
            cancellationToken: cancellationToken);
        if (enqRes.IsFailure)
        {
            _logger.LogWarning("Failed to enqueue LatePaymentRefundRequested event for Order {OrderId}: {Error}", order.Id, enqRes.Error.Message);
        }
    }

    public async Task<Result> ProcessInvoicePaidAsync(
        KafkaMessageEnvelope<InvoicePaidPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        var envValidation = ValidateEnvelope(envelope);
        if (envValidation.IsFailure) return envValidation;

        var payload = envelope.Payload;
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        RentalOrder? order;
        await using (var orderLock = await _lockManager.AcquireOrderLockAsync(payload.RentalOrderId, TimeSpan.FromSeconds(5), cancellationToken))
        {
            string rawPayload = JsonSerializer.Serialize(envelope, JsonOptions);
            string fingerprint = ComputeHash(rawPayload);

            var existingInbox = await _inboxRepository.GetByMessageIdAsync(envelope.MessageId, cancellationToken);
            if (existingInbox != null && existingInbox.Status == RentalInboxMessage.StatusProcessed)
            {
                return Result.Success();
            }

            order = await _orderRepository.GetByIdWithDetailsAsync(payload.RentalOrderId, cancellationToken);
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
                fingerprint);

            if (existingInbox == null)
            {
                await _inboxRepository.AddAsync(inboxMessage, cancellationToken);
            }

            await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await ApplyInvoicePaidStagedAsync(order, envelope, inboxMessage, cancellationToken);
                if (result.IsFailure)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return result;
                }

                inboxMessage.MarkProcessed(utcNow);
                if (existingInbox != null)
                {
                    _inboxRepository.Update(inboxMessage);
                }

                if (result.Value)
                {
                    await _orderRepository.UpdateAsync(order, cancellationToken);
                }

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch (Exception)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                throw;
            }
        }

        // Direct invocation/tests outside lock executes hold/cleanup


        return Result.Success();
    }

    private static Result ValidateEnvelope(KafkaMessageEnvelope<InvoicePaidPayload> envelope)
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
        if (envelope.Payload.PaidAt == default) return Error.Validation("INVALID_PAID_AT", "Thời gian thanh toán không hợp lệ.");
        if (!string.Equals(envelope.Payload.PaymentDisposition, "ON_TIME", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(envelope.Payload.PaymentDisposition, "LATE_REQUIRES_REFUND", StringComparison.OrdinalIgnoreCase))
            return Error.Validation("INVALID_PAYMENT_DISPOSITION", "PaymentDisposition không được hỗ trợ.");

        return Result.Success();
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
