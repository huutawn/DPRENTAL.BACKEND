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
        if (payload.EquipmentTypeId == Guid.Empty)
            return Error.Validation("INVALID_TYPE_ID", "EquipmentTypeId cannot be empty.");
        if (payload.RentalOrderId == Guid.Empty)
            return Error.Validation("INVALID_ORDER_ID", "RentalOrderId cannot be empty.");
        if (payload.RentalItemId == Guid.Empty)
            return Error.Validation("INVALID_ITEM_ID", "RentalItemId cannot be empty.");

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
        if (payload.EquipmentTypeId == Guid.Empty)
            return Error.Validation("INVALID_TYPE_ID", "EquipmentTypeId cannot be empty.");
        if (payload.RentalOrderId == Guid.Empty)
            return Error.Validation("INVALID_ORDER_ID", "RentalOrderId cannot be empty.");
        if (payload.RentalItemId == Guid.Empty)
            return Error.Validation("INVALID_ITEM_ID", "RentalItemId cannot be empty.");

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
}
