#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Producers;

public sealed class RentalEventProducer : IRentalEventProducer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IOutboxRepository _outboxRepository;
    private readonly KafkaOptions _kafkaOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalEventProducer> _logger;

    public RentalEventProducer(
        IOutboxRepository outboxRepository,
        IOptions<KafkaOptions> kafkaOptions,
        TimeProvider? timeProvider = null,
        ILogger<RentalEventProducer>? logger = null)
    {
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RentalEventProducer>.Instance;
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

        string payloadJson = JsonSerializer.Serialize(envelope, JsonOptions);

        var outboxMessage = new RentalOutboxMessage(
            messageId,
            topic,
            partitionKey,
            KafkaContractSpec.EventTypes.RentalOrderCreated,
            payloadJson,
            utcNow);

        var enqueueResult = await _outboxRepository.EnqueueAtomicAsync(outboxMessage, cancellationToken);
        if (enqueueResult.IsFailure)
        {
            _logger.LogWarning("Failed to enqueue RentalOrderCreated event {MessageId}: {Error}", messageId, enqueueResult.Error.Message);
            return enqueueResult.Error;
        }

        _logger.LogInformation("Enqueued RentalOrderCreated event {MessageId} for Order {OrderId} (Replay={IsReplay})",
            messageId, payload.RentalOrderId, enqueueResult.Value.IsReplay);

        return new ProducerEnqueueResult(
            messageId,
            enqueueResult.Value.IsReplay,
            KafkaContractSpec.EventTypes.RentalOrderCreated,
            topic,
            partitionKey);
    }
}
