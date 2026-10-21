#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Dprental.RentalService.Application.Services.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Consumers;

public sealed class PaymentEventsDispatcher : IPaymentEventsDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IInboxRepository _inboxRepository;
    private readonly IRentalOrderRepository _orderRepository;
    private readonly IOrderLockManager _lockManager;
    private readonly IPaymentWorkflowService _paymentWorkflowService;
    private readonly IRefundCompletedConsumer _refundCompletedConsumer;
    private readonly IRentalSettledConsumer _rentalSettledConsumer;
    private readonly IKafkaOutboxWriter _outboxWriter;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentEventsDispatcher> _logger;
    private readonly KafkaOptions _kafkaOptions;
    private readonly IRentalCheckoutOperationRepository? _checkoutOperationRepo;
    private readonly IInventoryAllocationResultHandler _allocationResultHandler;
    private readonly InventoryReturnResultHandler? _inventoryReturnResultHandler;
    [ActivatorUtilitiesConstructor]
    public PaymentEventsDispatcher(
        IInboxRepository inboxRepository,
        IRentalOrderRepository orderRepository,
        IOrderLockManager lockManager,
        IPaymentWorkflowService paymentWorkflowService,
        IRefundCompletedConsumer refundCompletedConsumer,
        IKafkaOutboxWriter outboxWriter,
        IUnitOfWork unitOfWork,
        IInventoryAllocationResultHandler allocationResultHandler,
        IOptions<KafkaOptions> kafkaOptions,
        IRentalCheckoutOperationRepository? checkoutOperationRepo = null,
        ILogger<PaymentEventsDispatcher>? logger = null,
        TimeProvider? timeProvider = null,
        IRentalSettledConsumer? rentalSettledConsumer = null,
        InventoryReturnResultHandler? inventoryReturnResultHandler = null)
    {
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _paymentWorkflowService = paymentWorkflowService ?? throw new ArgumentNullException(nameof(paymentWorkflowService));
        _refundCompletedConsumer = refundCompletedConsumer ?? throw new ArgumentNullException(nameof(refundCompletedConsumer));
        _outboxWriter = outboxWriter ?? throw new ArgumentNullException(nameof(outboxWriter));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _allocationResultHandler = allocationResultHandler ?? throw new ArgumentNullException(nameof(allocationResultHandler));
        _checkoutOperationRepo = checkoutOperationRepo;
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentEventsDispatcher>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rentalSettledConsumer = rentalSettledConsumer ?? new Dprental.RentalService.Application.Services.Integration.RentalSettledConsumer(
            new RentalEventProducer(outboxWriter, Microsoft.Extensions.Options.Options.Create(_kafkaOptions), _timeProvider), _timeProvider);
        _inventoryReturnResultHandler = inventoryReturnResultHandler;
    }

    internal PaymentEventsDispatcher(
        IInboxRepository inboxRepository,
        IRentalOrderRepository orderRepository,
        IOrderLockManager lockManager,
        IPaymentWorkflowService paymentWorkflowService,
        IRefundCompletedConsumer refundCompletedConsumer,
        IKafkaOutboxWriter outboxWriter,
        IUnitOfWork unitOfWork,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<PaymentEventsDispatcher>? logger = null,
        TimeProvider? timeProvider = null)
        : this(
            inboxRepository,
            orderRepository,
            lockManager,
            paymentWorkflowService,
            refundCompletedConsumer,
            outboxWriter,
            unitOfWork,
            new InventoryAllocationResultHandler(orderRepository, unitOfWork, lockManager, timeProvider),
            kafkaOptions,
            null,
            logger,
            timeProvider)
    {
    }

    internal PaymentEventsDispatcher(
        IInboxRepository inboxRepository,
        IRentalOrderRepository orderRepository,
        IOrderLockManager lockManager,
        IPaymentWorkflowService paymentWorkflowService,
        IRefundCompletedConsumer refundCompletedConsumer,
        IKafkaOutboxWriter outboxWriter,
        IUnitOfWork unitOfWork,
        IRentalCheckoutOperationRepository checkoutOperationRepo,
        IInventoryCommandProducer inventoryCommands,
        ICheckoutOrderWriter orderWriter,
        ICheckoutInvoiceIssuer invoiceIssuer,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<PaymentEventsDispatcher>? logger = null,
        TimeProvider? timeProvider = null)
        : this(
            inboxRepository,
            orderRepository,
            lockManager,
            paymentWorkflowService,
            refundCompletedConsumer,
            outboxWriter,
            unitOfWork,
            new InventoryAllocationResultHandler(
                orderRepository,
                unitOfWork,
                lockManager,
                timeProvider,
                checkoutOperationRepo,
                inventoryCommands,
                orderWriter),
            kafkaOptions,
            checkoutOperationRepo,
            logger,
            timeProvider)
    {
    }

    internal PaymentEventsDispatcher(
        IInboxRepository inboxRepository,
        IRentalOrderRepository orderRepository,
        IOrderLockManager lockManager,
        IPaymentWorkflowService paymentWorkflowService,
        IRefundCompletedConsumer refundCompletedConsumer,
        IOutboxRepository outboxRepository,
        IUnitOfWork unitOfWork,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<PaymentEventsDispatcher>? logger = null,
        TimeProvider? timeProvider = null)
        : this(
            inboxRepository,
            orderRepository,
            lockManager,
            paymentWorkflowService,
            refundCompletedConsumer,
            new KafkaOutboxWriter(outboxRepository),
            unitOfWork,
            new InventoryAllocationResultHandler(orderRepository, unitOfWork, lockManager, timeProvider),
            kafkaOptions,
            null,
            logger,
            timeProvider)
    {
    }



    public async Task<Result<KafkaIntakeDisposition>> ReceiveMessageAsync(
        string messageJson,
        string? topic = null,
        int? partition = null,
        long? offset = null,
        string? publicationProof = null,
        string? messageKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            var dlqResult = await RouteToDlqAsync(messageJson ?? string.Empty, "EMPTY_MESSAGE", "Message không được rỗng.", topic, partition, offset, null, cancellationToken);
            if (dlqResult.IsFailure)
            {
                return dlqResult.Error;
            }
            return KafkaIntakeDisposition.DlqStored;
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(messageJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("JSON parse failure on payment event record [T:{Topic}, P:{Partition}, O:{Offset}]: {Error}", topic, partition, offset, ex.Message);
            var dlqResult = await RouteToDlqAsync(messageJson, "MALFORMED_JSON", ex.Message, topic, partition, offset, null, cancellationToken);
            if (dlqResult.IsFailure)
            {
                return dlqResult.Error;
            }
            return KafkaIntakeDisposition.DlqStored;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "MALFORMED_ROOT", "Root phải là JSON object.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            if (!root.TryGetProperty("messageType", out var msgTypeProp) ||
                msgTypeProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(msgTypeProp.GetString()))
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "MISSING_MESSAGE_TYPE", "Thiếu trường messageType hoặc kiểu dữ liệu không phải string.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            string messageType = msgTypeProp.GetString()!;

            if (!root.TryGetProperty("messageId", out var msgIdProp) ||
                msgIdProp.ValueKind != JsonValueKind.String ||
                !msgIdProp.TryGetGuid(out var messageId) ||
                messageId == Guid.Empty)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "MISSING_MESSAGE_ID", "Trường messageId bị thiếu hoặc không phải Guid hợp lệ.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            if (!root.TryGetProperty("version", out var versionProp) ||
                versionProp.ValueKind != JsonValueKind.Number ||
                !versionProp.TryGetInt32(out var version) ||
                version != KafkaContractSpec.CurrentEnvelopeVersion)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "UNSUPPORTED_VERSION", "Chỉ hỗ trợ envelope version 1.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            if (!root.TryGetProperty("producer", out var prodProp) ||
                prodProp.ValueKind != JsonValueKind.String)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_PRODUCER", "Trường producer bị thiếu hoặc không phải string.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            string producer = prodProp.GetString()!;
            bool isPaymentProducer = string.Equals(producer, KafkaContractSpec.Producers.PaymentService, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(producer, "payment-service", StringComparison.OrdinalIgnoreCase);
            bool isInventoryProducer = string.Equals(producer, KafkaContractSpec.Producers.InventoryService, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(producer, "inventory-service", StringComparison.OrdinalIgnoreCase);

            if (!isPaymentProducer && !isInventoryProducer)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_PRODUCER", $"Producer không hợp lệ: '{producer}'.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            if (!root.TryGetProperty("aggregateId", out var aggProp) ||
                aggProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(aggProp.GetString()) ||
                !Guid.TryParse(aggProp.GetString(), out _))
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_AGGREGATE_ID", "AggregateId phải là Guid hợp lệ.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            string aggregateId = aggProp.GetString()!;

            if (!root.TryGetProperty("occurredAt", out var occurredAtProp) ||
                occurredAtProp.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(occurredAtProp.GetString(), out var occurredAt) || occurredAt == default)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_OCCURRED_AT", "OccurredAt must be a valid timestamp.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            if (!root.TryGetProperty("payload", out var payloadProp) || payloadProp.ValueKind != JsonValueKind.Object)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_PAYLOAD", "Payload phải là JSON object.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }
            if (!payloadProp.TryGetProperty("rentalOrderId", out var payloadOrderProp) ||
                !payloadOrderProp.TryGetGuid(out var payloadOrderId) ||
                !Guid.TryParse(aggregateId, out Guid aggregateGuid) || aggregateGuid != payloadOrderId)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "AGGREGATE_PAYLOAD_MISMATCH",
                    "AggregateId must equal payload RentalOrderId.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            string? correlationId = root.TryGetProperty("correlationId", out var corrProp) && corrProp.ValueKind == JsonValueKind.String ? corrProp.GetString() : null;
            Guid? causationId = root.TryGetProperty("causationId", out var causProp) && causProp.ValueKind == JsonValueKind.String && causProp.TryGetGuid(out var causGuid) ? causGuid : null;

            bool isPaymentEvent = string.Equals(messageType, KafkaContractSpec.EventTypes.InvoicePaid, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(messageType, KafkaContractSpec.EventTypes.RefundCompleted, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(messageType, KafkaContractSpec.EventTypes.RentalSettled, StringComparison.OrdinalIgnoreCase);
            bool isAllocationEvent = string.Equals(messageType, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(messageType, KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(messageType, KafkaContractSpec.EventTypes.ReturnedUnitsApplied, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(messageType, KafkaContractSpec.EventTypes.ReturnedUnitsRejected, StringComparison.OrdinalIgnoreCase);

            if ((isPaymentEvent && (!isPaymentProducer || topic is not null && !string.Equals(topic, _kafkaOptions.PaymentEventsTopic, StringComparison.Ordinal))) ||
                (isAllocationEvent && (!isInventoryProducer || topic is not null && !string.Equals(topic, _kafkaOptions.InventoryAllocationEventsTopic, StringComparison.Ordinal))))
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "PRODUCER_TOPIC_MISMATCH",
                    "MessageType, producer và topic không khớp contract.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            if (!isPaymentEvent && !isAllocationEvent)
            {
                _logger.LogInformation("Ignored unhandled event type: {Type}", messageType);
                return Result.Success(KafkaIntakeDisposition.Ignored);
            }

            if (isPaymentEvent)
            {
                var proofResult = PaymentPublicationProofVerifier.Verify(messageJson, publicationProof, topic, messageKey, _kafkaOptions, utcNow);
                if (proofResult.IsFailure && proofResult.Error.Code != "PAYMENT_PROOF_KEY_UNAVAILABLE")
                {
                    var dlqResult = await RouteToDlqAsync(messageJson, proofResult.Error.Code, proofResult.Error.Message,
                        topic, partition, offset, null, cancellationToken);
                    return dlqResult.IsFailure ? dlqResult.Error : KafkaIntakeDisposition.DlqStored;
                }
            }

            string canonicalNew = CanonicalJson.Normalize(messageJson);
            string fingerprint = ComputeSha256Hex(canonicalNew);

            var existing = await _inboxRepository.GetByMessageIdAsync(messageId, cancellationToken);
            if (existing != null)
            {
                if (string.Equals(existing.MessageType, messageType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Producer, producer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.AggregateId, aggregateId, StringComparison.OrdinalIgnoreCase) &&
                    CanonicalJson.AreEquivalent(existing.Payload, messageJson))
                {
                    _logger.LogInformation("Idempotent duplicate Kafka payment event {MessageId} matched existing inbox", messageId);
                    return Result.Success(KafkaIntakeDisposition.Duplicate);
                }

                _logger.LogWarning("Conflicting Kafka payment event for messageId {MessageId}. Routing to DLQ.", messageId);
                var dlqRes = await RouteToDlqAsync(messageJson, "MESSAGE_CONFLICT", "MessageId đã tồn tại với nội dung mâu thuẫn.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }

            var inboxMessage = new RentalInboxMessage(
                Guid.NewGuid(),
                messageId,
                messageType,
                producer,
                aggregateId,
                correlationId,
                causationId,
                messageJson,
                utcNow,
                fingerprint,
                topic,
                partition,
                offset);

            if (isPaymentEvent) inboxMessage.AssignPublicationProof(publicationProof, messageKey ?? aggregateId);

            try
            {
                await _inboxRepository.AddAsync(inboxMessage, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Persisted inbox message {MessageId} (Type: {Type}, Aggregate: {AggregateId})",
                    messageId, messageType, aggregateId);
                return Result.Success(KafkaIntakeDisposition.InboxStored);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _unitOfWork.DiscardChanges();
                var reloaded = await _inboxRepository.GetByMessageIdAsync(messageId, cancellationToken);
                if (reloaded != null &&
                    string.Equals(reloaded.MessageType, messageType, StringComparison.OrdinalIgnoreCase) &&
                    CanonicalJson.AreEquivalent(reloaded.Payload, messageJson))
                {
                    return Result.Success(KafkaIntakeDisposition.Duplicate);
                }

                var dlqRes = await RouteToDlqAsync(messageJson, "MESSAGE_CONFLICT", "Xung đột khóa MessageId trên database.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes.Error;
                return KafkaIntakeDisposition.DlqStored;
            }
            catch (Exception ex)
            {
                _unitOfWork.DiscardChanges();
                _logger.LogError(ex, "Database persistence error while saving Inbox message {MessageId}", messageId);
                return Error.Failure("INBOX_PERSISTENCE_FAILED", ex.Message);
            }
        }
    }

    public async Task<Result> ProcessInboxMessageAsync(
        Guid inboxMessageId,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await _inboxRepository.GetDescriptorByIdAsync(inboxMessageId, cancellationToken);
        if (descriptor == null)
        {
            return Error.NotFound("INBOX_MESSAGE_NOT_FOUND", $"Không tìm thấy Inbox message '{inboxMessageId}'.");
        }

        if (descriptor.Status == RentalInboxMessage.StatusProcessed || descriptor.Status == RentalInboxMessage.StatusDeadLettered)
        {
            return Result.Success();
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        if (descriptor.NextRetryAt.HasValue && descriptor.NextRetryAt.Value > utcNow)
        {
            return Result.Success();
        }

        if (!Guid.TryParse(descriptor.AggregateId, out var orderId))
        {
            var freshMsg = await _inboxRepository.GetByIdForProcessingAsync(inboxMessageId, cancellationToken);
            if (freshMsg != null)
            {
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    freshMsg.MarkDeadLettered("AggregateId không phải Guid hợp lệ.");
                    _inboxRepository.Update(freshMsg);
                    var dlqRes = await RouteToDlqAsync(freshMsg.Payload, "INVALID_AGGREGATE_ID", "AggregateId không phải Guid.", freshMsg.KafkaTopic, freshMsg.KafkaPartition, freshMsg.KafkaOffset, freshMsg.Id, cancellationToken);
                    if (dlqRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return dlqRes.Error;
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return Error.Failure("DLQ_TRANSACTION_FAILED", ex.Message);
                }
            }
            return Error.Validation("INVALID_AGGREGATE_ID", "AggregateId không phải Guid.");
        }

        var identityMessage = await _inboxRepository.GetByIdForProcessingAsync(inboxMessageId, cancellationToken);
        if (identityMessage is null) return Error.NotFound("INBOX_MESSAGE_NOT_FOUND", "Inbox message was not found.");
        Guid payloadOrderId = ReadPayloadOrderId(identityMessage.Payload);
        if (payloadOrderId == Guid.Empty || payloadOrderId != orderId)
            return await DeadLetterInboxAsync(inboxMessageId, "AGGREGATE_PAYLOAD_MISMATCH",
                "AggregateId must equal payload RentalOrderId.", cancellationToken);

        Guid? renterUserId = null;
        if (descriptor.MessageType.Contains("Allocation", StringComparison.OrdinalIgnoreCase))
        {
            if (_checkoutOperationRepo != null)
            {
                var opDesc = await _checkoutOperationRepo.GetByExpectedOrderIdAsync(orderId, cancellationToken);
                if (opDesc != null) renterUserId = opDesc.RenterUserId;
            }
        }
        else
        {
            renterUserId = await _orderRepository.GetRenterIdByOrderIdAsync(orderId, cancellationToken);
        }

        IAsyncDisposable? renterLock = null;
        if (renterUserId.HasValue)
        {
            try
            {
                renterLock = await _lockManager.AcquireRenterLockAsync(renterUserId.Value, TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                return Error.Conflict("RENTER_LOCK_TIMEOUT", "Cannot acquire renter lock for inbox processing");
            }
        }

        await using var renterScope = renterLock;
        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);
        var msg = await _inboxRepository.GetByIdForProcessingAsync(inboxMessageId, cancellationToken);
        if (msg == null || msg.Status == RentalInboxMessage.StatusProcessed || msg.Status == RentalInboxMessage.StatusDeadLettered)
        {
            return Result.Success();
        }

        if (msg.NextRetryAt.HasValue && msg.NextRetryAt.Value > utcNow)
        {
            return Result.Success();
        }

        if (msg.RetryCount >= 10)
        {
            return Result.Success();
        }

        bool isAllocationEvent = string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.ReturnedUnitsApplied, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.ReturnedUnitsRejected, StringComparison.OrdinalIgnoreCase);

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null && !isAllocationEvent)
        {
            if (msg.RetryCount < 9)
            {
                double baseDelay = Math.Min(60, Math.Pow(2, msg.RetryCount));
                int jitterMs = Random.Shared.Next(0, 1000);
                DateTimeOffset nextRetry = utcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);

                msg.MarkFailed($"ORDER_NOT_FOUND: Không tìm thấy đơn hàng '{orderId}'.", nextRetry);
                _inboxRepository.Update(msg);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Error.NotFound("ORDER_NOT_FOUND", $"Không tìm thấy đơn hàng '{orderId}'.");
            }
            else
            {
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    msg.MarkDeadLettered($"Đã hết 10 lần thử lại không tìm thấy đơn hàng '{orderId}'.");
                    _inboxRepository.Update(msg);
                    var dlqRes = await RouteToDlqAsync(msg.Payload, "ORDER_NOT_FOUND_EXHAUSTED", "Đã hết số lần retry tìm đơn hàng.", msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.Id, cancellationToken);
                    if (dlqRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return dlqRes.Error;
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return Error.NotFound("ORDER_NOT_FOUND_EXHAUSTED", $"Đã hết số lần retry tìm đơn hàng '{orderId}'.");
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return Error.Failure("DLQ_TRANSACTION_FAILED", ex.Message);
                }
            }
        }

        if (isAllocationEvent)
        {
            await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                Result allocResult;
                if (msg.MessageType is KafkaContractSpec.EventTypes.ReturnedUnitsApplied or KafkaContractSpec.EventTypes.ReturnedUnitsRejected)
                {
                    if (_inventoryReturnResultHandler is null)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return Error.Failure("RETURN_RESULT_HANDLER_NOT_CONFIGURED", "Inventory return result handler is required.");
                    }
                    allocResult = msg.MessageType == KafkaContractSpec.EventTypes.ReturnedUnitsApplied
                        ? await _inventoryReturnResultHandler.ApplyStagedAsync(msg.Payload, cancellationToken)
                        : await _inventoryReturnResultHandler.RejectStagedAsync(msg.Payload, cancellationToken);
                }
                else
                {
                    allocResult = await _allocationResultHandler.HandleAllocationEventAsync(orderId, msg, order, cancellationToken);
                }
                if (allocResult.IsFailure)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return await RecordProcessingFailureAsync(inboxMessageId, allocResult.Error, cancellationToken);
                }

                msg.MarkProcessed(utcNow);
                _inboxRepository.Update(msg);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                _logger.LogInformation("Successfully processed allocation inbox message {Id} ({Type}) for Order {OrderId}",
                    msg.Id, msg.MessageType, orderId);
                return Result.Success();
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                throw;
            }
        }

        if (msg.MessageType is "InvoicePaid" or "RefundCompleted" or "RentalSettled")
        {
            var proofResult = PaymentPublicationProofVerifier.Verify(msg.Payload, msg.PublicationProofJson,
                msg.KafkaTopic, msg.KafkaMessageKey, _kafkaOptions, utcNow);
            if (proofResult.IsSuccess)
            {
                using var envelope = JsonDocument.Parse(msg.Payload);
                var root = envelope.RootElement;
                bool inboxBindingMatches = root.GetProperty("messageId").GetGuid() == msg.MessageId &&
                    string.Equals(root.GetProperty("messageType").GetString(), msg.MessageType, StringComparison.Ordinal) &&
                    string.Equals(root.GetProperty("aggregateId").GetString(), msg.AggregateId, StringComparison.Ordinal) &&
                    (string.Equals(root.GetProperty("producer").GetString(), KafkaContractSpec.Producers.PaymentService, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(root.GetProperty("producer").GetString(), "payment-service", StringComparison.OrdinalIgnoreCase));
                if (!inboxBindingMatches)
                    proofResult = Error.Validation("PAYMENT_PROOF_INBOX_METADATA_MISMATCH", "Stored inbox metadata differs from signed event envelope.");
            }
            if (proofResult.IsFailure)
            {
                if (proofResult.Error.Code == "PAYMENT_PROOF_KEY_UNAVAILABLE" && msg.RetryCount < 9)
                {
                    msg.MarkFailed(proofResult.Error.Message, utcNow.AddSeconds(Math.Min(60, Math.Pow(2, msg.RetryCount))));
                    _inboxRepository.Update(msg);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    return proofResult;
                }
                return await DeadLetterInboxAsync(msg.Id, proofResult.Error.Code, proofResult.Error.Message, cancellationToken);
            }
        }

        Result<bool> result;
        if (string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.InvoicePaid, StringComparison.OrdinalIgnoreCase))
        {
            KafkaMessageEnvelope<InvoicePaidPayload>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<InvoicePaidPayload>>(msg.Payload, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to deserialize stored InvoicePaid envelope {MessageId}: {Error}", msg.MessageId, ex.Message);
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    msg.MarkDeadLettered(ex.Message);
                    _inboxRepository.Update(msg);
                    var dlqRes = await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", ex.Message, msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.Id, cancellationToken);
                    if (dlqRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return dlqRes.Error;
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return Error.Validation("DESERIALIZATION_FAILED", "Không thể deserialize stored InvoicePaid envelope.");
                }
                catch (Exception tex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return Error.Failure("DLQ_TRANSACTION_FAILED", tex.Message);
                }
            }

            if (envelope == null || envelope.Payload == null)
            {
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    msg.MarkDeadLettered("Stored InvoicePaid envelope or payload is null.");
                    _inboxRepository.Update(msg);
                    var dlqRes = await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", "Stored envelope is null", msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.Id, cancellationToken);
                    if (dlqRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return dlqRes.Error;
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return Error.Validation("DESERIALIZATION_FAILED", "Stored envelope is null.");
                }
                catch (Exception tex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return Error.Failure("DLQ_TRANSACTION_FAILED", tex.Message);
                }
            }

            var applyResult = await ApplyStagedEventAsync(msg, order!,
                () => _paymentWorkflowService.ApplyInvoicePaidStagedAsync(order!, envelope!, msg, cancellationToken), cancellationToken);
            if (applyResult.IsSuccess) return applyResult;
            result = applyResult.Error;
        }
        else if (string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.RefundCompleted, StringComparison.OrdinalIgnoreCase))
        {
            KafkaMessageEnvelope<RefundCompletedPayload>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<RefundCompletedPayload>>(msg.Payload, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to deserialize stored RefundCompleted envelope {MessageId}: {Error}", msg.MessageId, ex.Message);
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    msg.MarkDeadLettered(ex.Message);
                    _inboxRepository.Update(msg);
                    var dlqRes = await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", ex.Message, msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.Id, cancellationToken);
                    if (dlqRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return dlqRes.Error;
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return Error.Validation("DESERIALIZATION_FAILED", "Không thể deserialize stored RefundCompleted envelope.");
                }
                catch (Exception tex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return Error.Failure("DLQ_TRANSACTION_FAILED", tex.Message);
                }
            }

            if (envelope == null || envelope.Payload == null)
            {
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    msg.MarkDeadLettered("Stored RefundCompleted envelope or payload is null.");
                    _inboxRepository.Update(msg);
                    var dlqRes = await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", "Stored envelope is null", msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.Id, cancellationToken);
                    if (dlqRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return dlqRes.Error;
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return Error.Validation("DESERIALIZATION_FAILED", "Stored envelope is null.");
                }
                catch (Exception tex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    return Error.Failure("DLQ_TRANSACTION_FAILED", tex.Message);
                }
            }

            var applyResult = await ApplyStagedEventAsync(msg, order!,
                () => _refundCompletedConsumer.ApplyRefundCompletedStagedAsync(order!, envelope!, msg, cancellationToken), cancellationToken);
            if (applyResult.IsSuccess) return applyResult;
            result = applyResult.Error;
        }
        else if (string.Equals(msg.MessageType, KafkaContractSpec.EventTypes.RentalSettled, StringComparison.OrdinalIgnoreCase))
        {
            KafkaMessageEnvelope<RentalSettledPayload>? envelope = null;
            try { envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<RentalSettledPayload>>(msg.Payload, JsonOptions); }
            catch (JsonException) { /* The shared failure path writes a DLQ record. */ }
            if (envelope?.Payload is null)
            {
                result = Error.Validation("DESERIALIZATION_FAILED", "Invalid RentalSettled event payload.");
            }
            else
            {
                var applyResult = await ApplyStagedEventAsync(msg, order!,
                    () => _rentalSettledConsumer.ApplyRentalSettledStagedAsync(order!, envelope, msg, cancellationToken), cancellationToken);
                if (applyResult.IsSuccess) return applyResult;
                result = applyResult.Error;
            }
        }
        else
        {
            msg.MarkProcessed(utcNow);
            _inboxRepository.Update(msg);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        if (result.IsSuccess)
        {
            await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                msg.MarkProcessed(utcNow);
                _inboxRepository.Update(msg);

                if (result.Value)
                {
                    order!.RecordReadModelUpdate(utcNow);
                    await _orderRepository.UpdateAsync(order, cancellationToken);
                }

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                _logger.LogInformation("Successfully processed inbox message {Id} ({Type}) for Order {OrderId}",
                    msg.Id, msg.MessageType, order!.Id);
                return Result.Success();
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                _logger.LogError(ex, "Transaction failed while committing workflow changes for inbox message {Id}", msg.Id);
                throw;
            }
        }
        else
        {
            bool isRetryable = result.Error.Code is "INVOICE_NOT_ATTACHED" or "ORDER_NOT_FOUND" or "INVENTORY_CLOSURE_PENDING";
            if (isRetryable && msg.RetryCount < 9)
            {
                double baseDelay = Math.Min(60, Math.Pow(2, msg.RetryCount));
                int jitterMs = Random.Shared.Next(0, 1000);
                DateTimeOffset nextRetry = utcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);

                msg.MarkFailed(result.Error.Message, nextRetry);
                _inboxRepository.Update(msg);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return result;
            }
            else
            {
                await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
                try
                {
                    msg.MarkDeadLettered(result.Error.Message);
                    _inboxRepository.Update(msg);
                    var dlqRes = await RouteToDlqAsync(msg.Payload, result.Error.Code, result.Error.Message, msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.Id, cancellationToken);
                    if (dlqRes.IsFailure)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        _unitOfWork.DiscardChanges();
                        return dlqRes.Error;
                    }
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return result;
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    _logger.LogError(ex, "Failed to persist DLQ and DeadLettered status for inbox message {Id}", msg.Id);
                    return Error.Failure("DLQ_PERSISTENCE_FAILED", ex.Message);
                }
            }
        }
    }

    private static Guid ReadPayloadOrderId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("payload", out var payload) &&
                   payload.TryGetProperty("rentalOrderId", out var orderId) && orderId.TryGetGuid(out Guid value)
                ? value : Guid.Empty;
        }
        catch (JsonException) { return Guid.Empty; }
    }

    private async Task<Result> DeadLetterInboxAsync(Guid inboxMessageId, string code, string reason, CancellationToken ct)
    {
        var message = await _inboxRepository.GetByIdForProcessingAsync(inboxMessageId, ct);
        if (message is null || message.Status is RentalInboxMessage.StatusProcessed or RentalInboxMessage.StatusDeadLettered)
            return Result.Success();
        await using var tx = await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            message.MarkDeadLettered($"{code}: {reason}");
            _inboxRepository.Update(message);
            var dlq = await RouteToDlqAsync(message.Payload, code, reason,
                message.KafkaTopic, message.KafkaPartition, message.KafkaOffset, message.Id, ct);
            if (dlq.IsFailure)
            {
                await tx.RollbackAsync(ct);
                _unitOfWork.DiscardChanges();
                return dlq.Error;
            }
            await _unitOfWork.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    private async Task<Result> RecordProcessingFailureAsync(Guid inboxMessageId, Error error, CancellationToken ct)
    {
        var message = await _inboxRepository.GetByIdForProcessingAsync(inboxMessageId, ct);
        if (message is null || message.Status is RentalInboxMessage.StatusProcessed or RentalInboxMessage.StatusDeadLettered)
            return Result.Success();

        await using var tx = await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            bool permanent = error.Code.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                             error.Code.StartsWith("INVALID", StringComparison.OrdinalIgnoreCase) ||
                             error.Code.Contains("MISMATCH", StringComparison.OrdinalIgnoreCase);
            if (message.RetryCount < 9 && !permanent)
            {
                TimeSpan delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, message.RetryCount)));
                message.MarkFailed($"{error.Code}: {error.Message}", now.Add(delay));
                _inboxRepository.Update(message);
                await _unitOfWork.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return error;
            }

            message.MarkDeadLettered($"{error.Code}: {error.Message}");
            _inboxRepository.Update(message);
            var dlq = await RouteToDlqAsync(message.Payload, error.Code, error.Message,
                message.KafkaTopic, message.KafkaPartition, message.KafkaOffset, message.Id, ct);
            if (dlq.IsFailure)
            {
                await tx.RollbackAsync(ct);
                _unitOfWork.DiscardChanges();
                return dlq.Error;
            }
            await _unitOfWork.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    private async Task<Result> ApplyStagedEventAsync(
        RentalInboxMessage message,
        RentalOrder order,
        Func<Task<Result<bool>>> apply,
        CancellationToken cancellationToken)
    {
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            Result<bool> result = await apply();
            if (result.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return result.Error;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            message.MarkProcessed(now);
            _inboxRepository.Update(message);
            if (result.Value)
            {
                order.RecordReadModelUpdate(now);
                await _orderRepository.UpdateAsync(order, cancellationToken);
            }
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Guid ComputeIntakeDlqId(string rawMessage, string? topic, int? partition, long? offset)
    {
        if (!string.IsNullOrWhiteSpace(topic) && partition.HasValue && offset.HasValue)
        {
            string key = $"intake:dlq:{topic}:{partition.Value}:{offset.Value}";
            return ComputeDeterministicGuid(key);
        }

        string rawHash = ComputeSha256Hex(rawMessage ?? string.Empty);
        return ComputeDeterministicGuid($"raw:dlq:{rawHash}");
    }

    private static Guid ComputeProcessingDlqId(Guid inboxMessageId)
    {
        return ComputeDeterministicGuid($"inbox:dlq:{inboxMessageId:D}");
    }

    private static Guid ComputeDeterministicGuid(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }




    private async Task<Result> RouteToDlqAsync(
        string rawMessage,
        string errorCode,
        string errorDetail,
        string? topic,
        int? partition,
        long? offset,
        Guid? inboxMessageId,
        CancellationToken ct)
    {
        IUnitOfWorkTransaction? intakeTransaction = null;
        if (!_unitOfWork.HasActiveTransaction)
            intakeTransaction = await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid dlqId = inboxMessageId.HasValue
            ? ComputeProcessingDlqId(inboxMessageId.Value)
            : ComputeIntakeDlqId(rawMessage, topic, partition, offset);

        string originalType = "UnknownEvent";
        try
        {
            using var original = JsonDocument.Parse(rawMessage ?? string.Empty);
            if (original.RootElement.ValueKind == JsonValueKind.Object &&
                original.RootElement.TryGetProperty("messageType", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
                originalType = typeElement.GetString() ?? originalType;
        }
        catch (JsonException) { }
        var dlqPayload = new DeadLetterPayload(originalType, rawMessage ?? string.Empty, $"{errorCode}: {errorDetail}", utcNow);
        var dlqEnvelope = new KafkaMessageEnvelope<DeadLetterPayload>(
            dlqId,
            KafkaContractSpec.EventTypes.DeadLetter,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            dlqId.ToString("D"),
            utcNow,
            null,
            null,
            dlqPayload);

        string dlqTopic = topic is not null && string.Equals(topic, _kafkaOptions.InventoryAllocationEventsTopic, StringComparison.Ordinal)
            ? _kafkaOptions.InventoryAllocationEventsDlqTopic
            : _kafkaOptions.PaymentDlqTopic;
        var enqueueResult = await _outboxWriter.EnqueueAsync(dlqTopic, dlqId.ToString("D"), dlqEnvelope, ct);
        if (enqueueResult.IsFailure)
        {
            if (intakeTransaction is not null)
            {
                await intakeTransaction.RollbackAsync(ct);
                _unitOfWork.DiscardChanges();
            }
            _logger.LogError("Failed to persist DLQ outbox record {DlqId}: {Error}", dlqId, enqueueResult.Error.Message);
            return enqueueResult.Error;
        }

        if (intakeTransaction is not null)
        {
            await _unitOfWork.SaveChangesAsync(ct);
            await intakeTransaction.CommitAsync(ct);
        }

        _logger.LogWarning("Routed invalid payment event to DLQ ({Topic}) [DlqId: {DlqId}, Replay: {Replay}]: {ErrorCode} - {Detail}",
            dlqTopic, dlqId, enqueueResult.Value.IsReplay, errorCode, errorDetail);
        return Result.Success();
        }
        catch
        {
            if (intakeTransaction is not null)
            {
                await intakeTransaction.RollbackAsync(ct);
                _unitOfWork.DiscardChanges();
            }
            throw;
        }
        finally
        {
            if (intakeTransaction is not null) await intakeTransaction.DisposeAsync();
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505";
    }
}
