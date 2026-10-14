#pragma warning disable CA1848, CA1873
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IOutboxRepository _outboxRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentEventsDispatcher> _logger;
    private readonly KafkaOptions _kafkaOptions;

    [ActivatorUtilitiesConstructor]
    public PaymentEventsDispatcher(
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
    {
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _paymentWorkflowService = paymentWorkflowService ?? throw new ArgumentNullException(nameof(paymentWorkflowService));
        _refundCompletedConsumer = refundCompletedConsumer ?? throw new ArgumentNullException(nameof(refundCompletedConsumer));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentEventsDispatcher>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Overload for backward-compatibility with tests using IConfiguration
    internal PaymentEventsDispatcher(
        IInboxRepository inboxRepository,
        IRentalOrderRepository orderRepository,
        IOrderLockManager lockManager,
        IPaymentWorkflowService paymentWorkflowService,
        IRefundCompletedConsumer refundCompletedConsumer,
        IOutboxRepository outboxRepository,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<PaymentEventsDispatcher>? logger = null,
        TimeProvider? timeProvider = null)
        : this(
            inboxRepository,
            orderRepository,
            lockManager,
            paymentWorkflowService,
            refundCompletedConsumer,
            outboxRepository,
            unitOfWork,
            Microsoft.Extensions.Options.Options.Create(new KafkaOptions
            {
                PaymentEventsTopic = configuration["Kafka:PaymentEventsTopic"] ?? KafkaContractSpec.Topics.PaymentEvents,
                PaymentDlqTopic = configuration["Kafka:PaymentDlqTopic"] ?? KafkaContractSpec.Topics.PaymentDlq
            }),
            logger,
            timeProvider)
    {
    }

    public async Task<Result> ReceiveMessageAsync(
        string messageJson,
        string? topic = null,
        int? partition = null,
        long? offset = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            var dlqResult = await RouteToDlqAsync(messageJson ?? string.Empty, "EMPTY_MESSAGE", "Message không được rỗng.", topic, partition, offset, null, cancellationToken);
            if (dlqResult.IsFailure)
            {
                return dlqResult;
            }
            return Error.Validation("EMPTY_MESSAGE", "Message không được rỗng.");
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
                return dlqResult;
            }
            return Error.Validation("MALFORMED_JSON", "Cấu trúc JSON không hợp lệ.");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("messageType", out var msgTypeProp) || string.IsNullOrWhiteSpace(msgTypeProp.GetString()))
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "MISSING_MESSAGE_TYPE", "Thiếu trường messageType", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes;
                return Error.Validation("MISSING_MESSAGE_TYPE", "Thiếu trường messageType.");
            }

            string messageType = msgTypeProp.GetString()!;

            // Validate Envelope v1 schema
            if (!root.TryGetProperty("messageId", out var msgIdProp) || msgIdProp.ValueKind != JsonValueKind.String || !msgIdProp.TryGetGuid(out var messageId) || messageId == Guid.Empty)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "MISSING_MESSAGE_ID", "Trường messageId bị thiếu hoặc không phải Guid hợp lệ.", topic, partition, offset, null, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes;
                return Error.Validation("MISSING_MESSAGE_ID", "Trường messageId bị thiếu hoặc không phải Guid hợp lệ.");
            }

            if (!root.TryGetProperty("version", out var versionProp) || versionProp.GetInt32() != KafkaContractSpec.CurrentEnvelopeVersion)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "UNSUPPORTED_VERSION", "Chỉ hỗ trợ envelope version 1", topic, partition, offset, messageId, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes;
                return Error.Validation("UNSUPPORTED_VERSION", "Chỉ hỗ trợ envelope version 1.");
            }

            string producer = root.TryGetProperty("producer", out var prodProp) ? prodProp.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(producer, KafkaContractSpec.Producers.PaymentService, StringComparison.OrdinalIgnoreCase))
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_PRODUCER", $"Producer không hợp lệ: '{producer}'", topic, partition, offset, messageId, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes;
                return Error.Validation("INVALID_PRODUCER", $"Producer không hợp lệ: '{producer}'.");
            }

            string aggregateId = root.TryGetProperty("aggregateId", out var aggProp) ? aggProp.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(aggregateId) || !Guid.TryParse(aggregateId, out _))
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_AGGREGATE_ID", "AggregateId phải là Guid hợp lệ", topic, partition, offset, messageId, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes;
                return Error.Validation("INVALID_AGGREGATE_ID", "AggregateId phải là Guid hợp lệ.");
            }

            if (!root.TryGetProperty("payload", out var payloadProp) || payloadProp.ValueKind != JsonValueKind.Object)
            {
                var dlqRes = await RouteToDlqAsync(messageJson, "INVALID_PAYLOAD", "Payload phải là JSON object.", topic, partition, offset, messageId, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes;
                return Error.Validation("INVALID_PAYLOAD", "Payload phải là JSON object.");
            }

            string? correlationId = root.TryGetProperty("correlationId", out var corrProp) && corrProp.ValueKind == JsonValueKind.String ? corrProp.GetString() : null;
            Guid? causationId = root.TryGetProperty("causationId", out var causProp) && causProp.ValueKind == JsonValueKind.String && causProp.TryGetGuid(out var causGuid) ? causGuid : null;

            // Only InvoicePaid and RefundCompleted are handled
            if (!string.Equals(messageType, KafkaContractSpec.EventTypes.InvoicePaid, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(messageType, KafkaContractSpec.EventTypes.RefundCompleted, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Ignored unhandled payment event type: {Type}", messageType);
                return Result.Success();
            }

            string canonicalNew = CanonicalJson.Normalize(messageJson);
            string fingerprint = ComputeSha256Hex(canonicalNew);

            // Deduplication check
            var existing = await _inboxRepository.GetByMessageIdAsync(messageId, cancellationToken);
            if (existing != null)
            {
                // Compare using canonical JSON representation directly from stored payload
                if (string.Equals(existing.MessageType, messageType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Producer, producer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.AggregateId, aggregateId, StringComparison.OrdinalIgnoreCase) &&
                    CanonicalJson.AreEquivalent(existing.Payload, messageJson))
                {
                    _logger.LogInformation("Idempotent duplicate Kafka payment event {MessageId} matched existing inbox", messageId);
                    return Result.Success();
                }

                _logger.LogWarning("Conflicting Kafka payment event for messageId {MessageId}. Routing to DLQ.", messageId);
                var dlqRes = await RouteToDlqAsync(messageJson, "MESSAGE_CONFLICT", "MessageId đã tồn tại với nội dung mâu thuẫn.", topic, partition, offset, messageId, cancellationToken);
                if (dlqRes.IsFailure) return dlqRes;
                return Error.Conflict("MESSAGE_CONFLICT", "MessageId đã tồn tại với nội dung mâu thuẫn.");
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

            try
            {
                await _inboxRepository.AddAsync(inboxMessage, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Persisted inbox message {MessageId} (Type: {Type}, Aggregate: {AggregateId})",
                    messageId, messageType, aggregateId);
                return Result.Success();
            }
            catch (DbUpdateException ex)
            {
                // Only treat unique constraint violations as potential duplicates; propagate other DB errors for retry
                if (IsUniqueConstraintViolation(ex))
                {
                    _logger.LogWarning("Unique constraint on persisting Inbox message {MessageId}; checking for concurrent duplicate", messageId);
                    var reloaded = await _inboxRepository.GetByMessageIdAsync(messageId, cancellationToken);
                    if (reloaded != null &&
                        string.Equals(reloaded.MessageType, messageType, StringComparison.OrdinalIgnoreCase) &&
                        CanonicalJson.AreEquivalent(reloaded.Payload, messageJson))
                    {
                        return Result.Success();
                    }

                    var dlqRes = await RouteToDlqAsync(messageJson, "MESSAGE_CONFLICT", "Xung đột khóa MessageId trên database.", topic, partition, offset, messageId, cancellationToken);
                    if (dlqRes.IsFailure) return dlqRes;
                    return Error.Conflict("MESSAGE_CONFLICT", "Xung đột khóa MessageId trên database.");
                }

                _logger.LogError(ex, "Database persistence error while saving Inbox message {MessageId}", messageId);
                return Error.Failure("INBOX_PERSISTENCE_FAILED", ex.Message);
            }
        }
    }

    public async Task<Result> ProcessInboxMessageAsync(
        Guid inboxMessageId,
        CancellationToken cancellationToken = default)
    {
        var msg = await _inboxRepository.GetByIdForProcessingAsync(inboxMessageId, cancellationToken);
        if (msg == null)
        {
            return Error.NotFound("INBOX_MESSAGE_NOT_FOUND", $"Không tìm thấy Inbox message '{inboxMessageId}'.");
        }

        if (msg.Status == RentalInboxMessage.StatusProcessed || msg.Status == RentalInboxMessage.StatusDeadLettered)
        {
            return Result.Success();
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        if (msg.NextRetryAt.HasValue && msg.NextRetryAt.Value > utcNow)
        {
            return Result.Success();
        }

        if (!Guid.TryParse(msg.AggregateId, out var orderId))
        {
            msg.MarkDeadLettered("AggregateId không phải Guid hợp lệ.");
            _inboxRepository.Update(msg);
            await RouteToDlqAsync(msg.Payload, "INVALID_AGGREGATE_ID", "AggregateId không phải Guid.", msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.MessageId, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Error.Validation("INVALID_AGGREGATE_ID", "AggregateId không phải Guid.");
        }

        await using var orderLock = await _lockManager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(5), cancellationToken);

        // RE-READ Inbox under the order lock to guarantee no concurrent worker already completed it
        msg = await _inboxRepository.GetByIdForProcessingAsync(inboxMessageId, cancellationToken);
        if (msg == null || msg.Status == RentalInboxMessage.StatusProcessed || msg.Status == RentalInboxMessage.StatusDeadLettered)
        {
            return Result.Success();
        }

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order == null)
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
                msg.MarkDeadLettered($"Đã hết 10 lần thử lại không tìm thấy đơn hàng '{orderId}'.");
                _inboxRepository.Update(msg);
                await RouteToDlqAsync(msg.Payload, "ORDER_NOT_FOUND_EXHAUSTED", "Đã hết số lần retry tìm đơn hàng.", msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.MessageId, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Error.NotFound("ORDER_NOT_FOUND_EXHAUSTED", $"Đã hết số lần retry tìm đơn hàng '{orderId}'.");
            }
        }

        Result result;
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
                msg.MarkDeadLettered(ex.Message);
                _inboxRepository.Update(msg);
                await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", ex.Message, msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.MessageId, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Error.Validation("DESERIALIZATION_FAILED", "Không thể deserialize stored InvoicePaid envelope.");
            }

            if (envelope == null || envelope.Payload == null)
            {
                msg.MarkDeadLettered("Stored InvoicePaid envelope or payload is null.");
                _inboxRepository.Update(msg);
                await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", "Stored envelope is null", msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.MessageId, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Error.Validation("DESERIALIZATION_FAILED", "Stored envelope is null.");
            }

            result = await _paymentWorkflowService.ApplyInvoicePaidStagedAsync(order, envelope, msg, cancellationToken);
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
                msg.MarkDeadLettered(ex.Message);
                _inboxRepository.Update(msg);
                await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", ex.Message, msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.MessageId, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Error.Validation("DESERIALIZATION_FAILED", "Không thể deserialize stored RefundCompleted envelope.");
            }

            if (envelope == null || envelope.Payload == null)
            {
                msg.MarkDeadLettered("Stored RefundCompleted envelope or payload is null.");
                _inboxRepository.Update(msg);
                await RouteToDlqAsync(msg.Payload, "DESERIALIZATION_FAILED", "Stored envelope is null", msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.MessageId, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return Error.Validation("DESERIALIZATION_FAILED", "Stored envelope is null.");
            }

            result = await _refundCompletedConsumer.ApplyRefundCompletedStagedAsync(order, envelope, msg, cancellationToken);
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
            msg.MarkProcessed(utcNow);
            _inboxRepository.Update(msg);

            order.IncrementVersion();
            await _orderRepository.UpdateAsync(order, cancellationToken);

            // Commit order state, inbox Processed, and staged outbox records in the same transaction
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Successfully processed inbox message {Id} ({Type}) for Order {OrderId}",
                msg.Id, msg.MessageType, order.Id);
            return Result.Success();
        }
        else
        {
            bool isRetryable = result.Error.Code is "INVOICE_NOT_ATTACHED" or "ORDER_NOT_FOUND";
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
                msg.MarkDeadLettered(result.Error.Message);
                _inboxRepository.Update(msg);
                await RouteToDlqAsync(msg.Payload, result.Error.Code, result.Error.Message, msg.KafkaTopic, msg.KafkaPartition, msg.KafkaOffset, msg.MessageId, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return result;
            }
        }
    }

    public async Task<Result> DispatchRawMessageAsync(string messageJson, CancellationToken cancellationToken = default)
    {
        var receiveRes = await ReceiveMessageAsync(messageJson, null, null, null, cancellationToken);
        if (receiveRes.IsFailure)
        {
            return receiveRes;
        }

        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            if (doc.RootElement.TryGetProperty("messageId", out var msgIdProp) && msgIdProp.TryGetGuid(out var msgId))
            {
                var inboxMsg = await _inboxRepository.GetByMessageIdAsync(msgId, cancellationToken);
                if (inboxMsg != null)
                {
                    return await ProcessInboxMessageAsync(inboxMsg.Id, cancellationToken);
                }
            }
        }
        catch
        {
            // If parse fails after receive, let return success for receive
        }

        return Result.Success();
    }

    private static string ComputeSha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Guid ComputeDeterministicDlqId(string rawMessage, string? topic, int? partition, long? offset, Guid? messageId)
    {
        if (messageId.HasValue && messageId.Value != Guid.Empty)
        {
            return messageId.Value;
        }

        string input = $"{topic ?? "unknown"}:{partition?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}:{offset?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}:{rawMessage}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
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
        Guid? messageId,
        CancellationToken ct)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid dlqId = ComputeDeterministicDlqId(rawMessage, topic, partition, offset, messageId);

        var dlqEnvelope = new KafkaMessageEnvelope<DeadLetterPayload>(
            dlqId,
            KafkaContractSpec.EventTypes.DeadLetter,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            dlqId.ToString("D"),
            utcNow,
            null,
            null,
            new DeadLetterPayload("PaymentEvent", rawMessage, $"{errorCode}: {errorDetail}", utcNow));

        var outbox = new RentalOutboxMessage(
            dlqId,
            _kafkaOptions.PaymentDlqTopic,
            dlqId.ToString("D"),
            KafkaContractSpec.EventTypes.DeadLetter,
            JsonSerializer.Serialize(dlqEnvelope, JsonOptions),
            utcNow);

        try
        {
            var enqueueResult = await _outboxRepository.EnqueueAtomicAsync(outbox, ct);
            if (enqueueResult.IsFailure)
            {
                if (enqueueResult.Error.Code == "OUTBOX_MESSAGE_CONFLICT")
                {
                    _logger.LogInformation("DLQ outbox record {DlqId} already exists from previous attempt; considering DLQ routed", dlqId);
                    return Result.Success();
                }

                _logger.LogError("Failed to persist DLQ outbox record {DlqId}: {Error}", dlqId, enqueueResult.Error.Message);
                return enqueueResult.Error;
            }

            _logger.LogWarning("Routed invalid payment event to DLQ ({Topic}): {ErrorCode} - {Detail}", _kafkaOptions.PaymentDlqTopic, errorCode, errorDetail);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLQ persistence failed for record {DlqId}", dlqId);
            return Error.Failure("DLQ_PERSISTENCE_FAILED", ex.Message);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")
        {
            return true;
        }

        string msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("PK_", StringComparison.OrdinalIgnoreCase);
    }
}
