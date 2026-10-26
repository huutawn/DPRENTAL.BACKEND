#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Producers;

public sealed class RentalEventProducer : IRentalEventProducer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IKafkaOutboxWriter _outboxWriter;
    private readonly KafkaOptions _kafkaOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalEventProducer> _logger;

    [ActivatorUtilitiesConstructor]
    public RentalEventProducer(
        IKafkaOutboxWriter outboxWriter,
        IOptions<KafkaOptions> kafkaOptions,
        TimeProvider? timeProvider = null,
        ILogger<RentalEventProducer>? logger = null)
    {
        _outboxWriter = outboxWriter ?? throw new ArgumentNullException(nameof(outboxWriter));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RentalEventProducer>.Instance;
    }

    internal RentalEventProducer(
         IOutboxRepository outboxRepository,
         IOptions<KafkaOptions> kafkaOptions,
         TimeProvider? timeProvider = null,
         ILogger<RentalEventProducer>? logger = null)
        : this(new KafkaOutboxWriter(outboxRepository), kafkaOptions, timeProvider, logger)
    {
    }

    public Task<Result<ProducerEnqueueResult>> EnqueueDomainEventAsync<TPayload>(
        string messageType, string logicalEntityId, string aggregateId, DateTimeOffset occurredAt,
        TPayload payload, string? correlationId = null, Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageType) || string.IsNullOrWhiteSpace(logicalEntityId) ||
            !Guid.TryParse(aggregateId, out Guid aggregateGuid) || aggregateGuid == Guid.Empty ||
            occurredAt == default || payload is null)
            return Task.FromResult(Result.Failure<ProducerEnqueueResult>(
                Error.Validation("INVALID_DOMAIN_EVENT", "Domain event identity, aggregate, time, and payload are required.")));
        var envelope = new KafkaMessageEnvelope<TPayload>(
            OutboxMessageIdGenerator.ComputeDeterministicId(KafkaContractSpec.Producers.RentalService, messageType, logicalEntityId),
            messageType, KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateGuid.ToString("D"), occurredAt, correlationId, causationId, payload);
        return _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateGuid.ToString("D"), envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCreatedAsync(
        RentalOrderCreatedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload == null)
        {
            return Error.Validation("INVALID_PAYLOAD", "RentalOrderCreatedPayload cannot be null.");
        }

        if (payload.RentalOrderId == Guid.Empty)
        {
            return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        }

        if (payload.OrderVersion <= 0)
        {
            return Error.Validation("INVALID_ORDER_VERSION", "OrderVersion must be a positive integer.");
        }
        if (string.IsNullOrWhiteSpace(payload.OrderCode))
        {
            return Error.Validation("INVALID_ORDER_CODE", "OrderCode cannot be empty.");
        }

        if (payload.RenterId == Guid.Empty)
        {
            return Error.Validation("INVALID_RENTER_ID", "RenterId cannot be empty.");
        }

        if (payload.TotalPayableAmount < 0)
        {
            return Error.Validation("INVALID_AMOUNT", "TotalPayableAmount cannot be negative.");
        }

        if (payload.Items == null || payload.Items.Count == 0)
        {
            return Error.Validation("EMPTY_ITEMS", "Items list cannot be empty in RentalOrderCreated event.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalOrderCreated,
            payload.RentalOrderId.ToString("D"));

        string topic = _kafkaOptions.RentalEventsTopic;
        string partitionKey = payload.RentalOrderId.ToString("D");

        var envelope = new KafkaMessageEnvelope<RentalOrderCreatedPayload>(
            messageId,
            KafkaContractSpec.EventTypes.RentalOrderCreated,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            partitionKey,
            utcNow,
            correlationId,
            causationId,
            payload);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }
    public async Task<Result<ProducerEnqueueResult>> EnqueueCartItemAddedAsync(
        CartItemAddedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload == null)
            return Error.Validation("INVALID_PAYLOAD", "CartItemAddedPayload cannot be null.");
        if (payload.UserId == Guid.Empty)
            return Error.Validation("INVALID_USER_ID", "UserId cannot be empty.");
        if (payload.EquipmentListingId == Guid.Empty)
            return Error.Validation("INVALID_LISTING_ID", "EquipmentListingId cannot be empty.");
        if (payload.EquipmentTypeId == Guid.Empty)
            return Error.Validation("INVALID_TYPE_ID", "EquipmentTypeId cannot be empty.");
        if (payload.CartId == Guid.Empty)
            return Error.Validation("INVALID_CART_ID", "CartId cannot be empty.");
        if (payload.CartItemId == Guid.Empty)
            return Error.Validation("INVALID_CART_ITEM_ID", "CartItemId cannot be empty.");

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.CartItemAdded,
            payload.CartItemId.ToString("D"));

        string topic = _kafkaOptions.RentalEventsTopic;
        string partitionKey = payload.CartId.ToString("D");

        var envelope = new KafkaMessageEnvelope<CartItemAddedPayload>(
            messageId,
            KafkaContractSpec.EventTypes.CartItemAdded,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            partitionKey,
            utcNow,
            correlationId,
            causationId,
            payload);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderPaidAsync(
        RentalOrderPaidPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload == null || payload.RentalOrderId == Guid.Empty || payload.RenterId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.InvoiceRef) || string.IsNullOrWhiteSpace(payload.PaymentRef) ||
            payload.PaidAmount < 0 || payload.PaidAt == default || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalOrderPaid payload is incomplete or invalid.");
        DateTimeOffset occurredAt = payload.PaidAt;
        string operationId = $"{payload.RentalOrderId:D}:{payload.PaymentRef}";
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalOrderPaid,
            operationId);
        string topic = _kafkaOptions.RentalEventsTopic;
        string partitionKey = payload.RentalOrderId.ToString("D");
        var envelope = new KafkaMessageEnvelope<RentalOrderPaidPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalOrderPaid,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            partitionKey, occurredAt, correlationId, causationId, payload);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCompletedAsync(
        RentalOrderCompletedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload == null || payload.RentalOrderId == Guid.Empty || payload.RenterId == Guid.Empty || payload.CompletedAt == default || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalOrderCompleted payload is incomplete or invalid.");
        string operationId = payload.RentalOrderId.ToString("D");
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalOrderCompleted,
            operationId);
        string topic = _kafkaOptions.RentalEventsTopic;
        string partitionKey = operationId;
        var envelope = new KafkaMessageEnvelope<RentalOrderCompletedPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalOrderCompleted,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            partitionKey, payload.CompletedAt, correlationId, causationId, payload);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemPaidAsync(
        RentalItemPaidPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload == null)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemPaidPayload cannot be null.");
        if (payload.UserId == Guid.Empty)
            return Error.Validation("INVALID_USER_ID", "UserId cannot be empty.");
        if (payload.EquipmentListingId == Guid.Empty)
            return Error.Validation("INVALID_LISTING_ID", "EquipmentListingId cannot be empty.");
        if (payload.EquipmentTypeId.HasValue && payload.EquipmentTypeId.Value == Guid.Empty)
            return Error.Validation("INVALID_TYPE_ID", "EquipmentTypeId cannot be empty.");
        if (payload.RentalOrderId == Guid.Empty)
            return Error.Validation("INVALID_ORDER_ID", "RentalOrderId cannot be empty.");
        if (payload.RentalItemId == Guid.Empty)
            return Error.Validation("INVALID_ITEM_ID", "RentalItemId cannot be empty.");
        if (payload.OrderVersion <= 0)
            return Error.Validation("INVALID_ORDER_VERSION", "OrderVersion must be a positive integer.");
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemPaid,
            payload.RentalItemId.ToString("D"));

        string topic = _kafkaOptions.RentalEventsTopic;
        string partitionKey = payload.RentalOrderId.ToString("D");

        var envelope = new KafkaMessageEnvelope<RentalItemPaidPayload>(
            messageId,
            KafkaContractSpec.EventTypes.RentalItemPaid,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            partitionKey,
            utcNow,
            correlationId,
            causationId,
            payload);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemCompletedAsync(
        RentalItemCompletedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload == null)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemCompletedPayload cannot be null.");
        if (payload.UserId == Guid.Empty)
            return Error.Validation("INVALID_USER_ID", "UserId cannot be empty.");
        if (payload.EquipmentListingId == Guid.Empty)
            return Error.Validation("INVALID_LISTING_ID", "EquipmentListingId cannot be empty.");
        if (payload.EquipmentTypeId.HasValue && payload.EquipmentTypeId.Value == Guid.Empty)
            return Error.Validation("INVALID_TYPE_ID", "EquipmentTypeId cannot be empty.");
        if (payload.RentalOrderId == Guid.Empty)
            return Error.Validation("INVALID_ORDER_ID", "RentalOrderId cannot be empty.");
        if (payload.RentalItemId == Guid.Empty)
            return Error.Validation("INVALID_ITEM_ID", "RentalItemId cannot be empty.");
        if (payload.OrderVersion <= 0)
            return Error.Validation("INVALID_ORDER_VERSION", "OrderVersion must be a positive integer.");
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemCompleted,
            payload.RentalItemId.ToString("D"));

        string topic = _kafkaOptions.RentalEventsTopic;
        string partitionKey = payload.RentalOrderId.ToString("D");

        var envelope = new KafkaMessageEnvelope<RentalItemCompletedPayload>(
            messageId,
            KafkaContractSpec.EventTypes.RentalItemCompleted,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            partitionKey,
            utcNow,
            correlationId,
            causationId,
            payload);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemReturnedAsync(
        RentalItemReturnedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.RenterId == Guid.Empty || payload.EquipmentListingId == Guid.Empty ||
            payload.Quantity <= 0 || payload.ReturnsCompletedAt == default || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemReturned payload is incomplete or invalid.");
        string aggregateId = payload.RentalOrderId.ToString("D");
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemReturned,
            payload.RentalItemId.ToString("D"));
        var envelope = new KafkaMessageEnvelope<RentalItemReturnedPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalItemReturned,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, _timeProvider.GetUtcNow(), correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderExpiredAsync(
        RentalOrderExpiredPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RenterId == Guid.Empty ||
            payload.ExpiredAt == default || string.IsNullOrWhiteSpace(payload.Reason) || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalOrderExpired payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        string logicalEntityId = correlationId ?? aggregateId;
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalOrderExpired,
            logicalEntityId);
        var envelope = new KafkaMessageEnvelope<RentalOrderExpiredPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalOrderExpired,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.ExpiredAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalOrderCancelledAsync(
        RentalOrderCancelledPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RenterId == Guid.Empty ||
            payload.CancelledAt == default || string.IsNullOrWhiteSpace(payload.Reason) || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalOrderCancelled payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        string logicalEntityId = correlationId ?? aggregateId;
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalOrderCancelled,
            logicalEntityId);
        var envelope = new KafkaMessageEnvelope<RentalOrderCancelledPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalOrderCancelled,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.CancelledAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalUnitsReturnedAsync(
        Dprental.RentalService.Application.DTOs.Integration.RentalUnitsReturnedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.OrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.ReturnRecordId == Guid.Empty || payload.ActualReturnAt == default || payload.SubmittedAt == default ||
            payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalUnitsReturned payload is incomplete or invalid.");

        string aggregateId = payload.OrderId.ToString("D");
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalUnitsReturned,
            payload.ReturnRecordId.ToString("D"));
        var envelope = new KafkaMessageEnvelope<Dprental.RentalService.Application.DTOs.Integration.RentalUnitsReturnedPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalUnitsReturned,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.SubmittedAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalSupplierConfirmationRequestedAsync(
        RentalSupplierConfirmationRequestedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.SupplierId == Guid.Empty || payload.EquipmentListingId == Guid.Empty || payload.Quantity <= 0 ||
            payload.SupplierConfirmationDeadline == default || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalSupplierConfirmationRequested payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested,
            payload.RentalItemId.ToString("D"));
        var envelope = new KafkaMessageEnvelope<RentalSupplierConfirmationRequestedPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalSupplierConfirmationRequested,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, _timeProvider.GetUtcNow(), correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemConfirmedAsync(
        RentalItemConfirmedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.SupplierId == Guid.Empty || payload.DecisionAt == default ||
            string.IsNullOrWhiteSpace(payload.InventoryConfirmationRef) || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemConfirmed payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemConfirmed,
            payload.RentalItemId.ToString("D"));
        var envelope = new KafkaMessageEnvelope<RentalItemConfirmedPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalItemConfirmed,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.DecisionAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemRejectedAsync(
        RentalItemRejectedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.SupplierId == Guid.Empty || payload.DecisionAt == default ||
            string.IsNullOrWhiteSpace(payload.Reason) || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemRejected payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        string logicalEntityId = correlationId ?? $"{payload.RentalOrderId:D}:{payload.RentalItemId:D}";
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemRejected,
            logicalEntityId);

        var sanitizedPayload = payload.OptionalNote is not null ? payload with { OptionalNote = null } : payload;
        var envelope = new KafkaMessageEnvelope<RentalItemRejectedPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalItemRejected,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.DecisionAt, correlationId, causationId, sanitizedPayload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemExpiredAsync(
        RentalItemExpiredPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.SupplierId == Guid.Empty || payload.ExpiredAt == default ||
            string.IsNullOrWhiteSpace(payload.Reason) || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemExpired payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        string logicalEntityId = correlationId ?? $"{payload.RentalOrderId:D}:{payload.RentalItemId:D}";
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemExpired,
            logicalEntityId);
        var envelope = new KafkaMessageEnvelope<RentalItemExpiredPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalItemExpired,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.ExpiredAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemCancelledAsync(
        RentalItemCancelledPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.ActorId == Guid.Empty || payload.CancelledAt == default ||
            string.IsNullOrWhiteSpace(payload.Reason) || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemCancelled payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        string logicalEntityId = correlationId ?? $"{payload.RentalOrderId:D}:{payload.RentalItemId:D}";
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemCancelled,
            logicalEntityId);
        var envelope = new KafkaMessageEnvelope<RentalItemCancelledPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalItemCancelled,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.CancelledAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalItemHandoverCompletedAsync(
        RentalItemHandoverCompletedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.RenterId == Guid.Empty || payload.SupplierId == Guid.Empty ||
            payload.EquipmentListingId == Guid.Empty || payload.Quantity <= 0 ||
            payload.HandoverRecordId == Guid.Empty || payload.ActualHandoverAt == default ||
            payload.CompletedAt == default || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalItemHandoverCompleted payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalItemHandoverCompleted,
            payload.HandoverRecordId.ToString("D"));
        var envelope = new KafkaMessageEnvelope<RentalItemHandoverCompletedPayload>(
            messageId, KafkaContractSpec.EventTypes.RentalItemHandoverCompleted,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.CompletedAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueRentalHandoverOverdueAsync(
        RentalHandoverOverduePayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty ||
            payload.SupplierId == Guid.Empty || payload.RenterId == Guid.Empty ||
            payload.OverdueAt == default || payload.OrderVersion <= 0)
            return Error.Validation("INVALID_PAYLOAD", "RentalHandoverOverdue payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        string logicalEntityId = correlationId ?? $"{payload.RentalOrderId:D}:{payload.RentalItemId:D}";
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalHandoverOverdue,
            logicalEntityId);
        var envelope = new KafkaMessageEnvelope<RentalHandoverOverduePayload>(
            messageId, KafkaContractSpec.EventTypes.RentalHandoverOverdue,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.OverdueAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueLatePaymentRefundRequestedAsync(
        LatePaymentRefundRequestedPayload payload,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (payload is null || payload.RentalOrderId == Guid.Empty || payload.RenterId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.InvoiceRef) || string.IsNullOrWhiteSpace(payload.PaymentRef) ||
            payload.TotalRefundAmount < 0 || payload.RequestedAt == default)
            return Error.Validation("INVALID_PAYLOAD", "LatePaymentRefundRequested payload is incomplete or invalid.");

        string aggregateId = payload.RentalOrderId.ToString("D");
        string operationId = $"{payload.RentalOrderId:D}:{payload.PaymentRef}";
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.LatePaymentRefundRequested,
            operationId);
        var envelope = new KafkaMessageEnvelope<LatePaymentRefundRequestedPayload>(
            messageId, KafkaContractSpec.EventTypes.LatePaymentRefundRequested,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            aggregateId, payload.RequestedAt, correlationId, causationId, payload);
        return await _outboxWriter.EnqueueAsync(_kafkaOptions.RentalEventsTopic, aggregateId, envelope, cancellationToken);
    }
}
