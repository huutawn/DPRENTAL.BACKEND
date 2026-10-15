#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RentalService.Application.Kafka.Producers;

public sealed class InventoryCommandProducer : IInventoryCommandProducer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IKafkaOutboxWriter _outboxWriter;
    private readonly KafkaOptions _kafkaOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InventoryCommandProducer> _logger;

    [ActivatorUtilitiesConstructor]
    public InventoryCommandProducer(
        IKafkaOutboxWriter outboxWriter,
        IOptions<KafkaOptions> kafkaOptions,
        TimeProvider? timeProvider = null,
        ILogger<InventoryCommandProducer>? logger = null)
    {
        _outboxWriter = outboxWriter ?? throw new ArgumentNullException(nameof(outboxWriter));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InventoryCommandProducer>.Instance;
    }

    internal InventoryCommandProducer(
        IOutboxRepository outboxRepository,
        IOptions<KafkaOptions> kafkaOptions,
        TimeProvider? timeProvider = null,
        ILogger<InventoryCommandProducer>? logger = null)
        : this(new KafkaOutboxWriter(outboxRepository), kafkaOptions, timeProvider, logger)
    {
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueApplyReturnedUnitsAsync(
        ApplyReturnedUnitsCommandPayload command,
        CancellationToken cancellationToken = default)
    {
        if (command is null || command.RentalOrderId == Guid.Empty ||
            command.RentalItemId == Guid.Empty || command.ReturnRecordId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.AllocationGroupId) ||
            string.IsNullOrWhiteSpace(command.OperationKey) ||
            command.Units is null || command.Units.Count == 0 ||
            command.Units.Any(u => u.AllocationId == Guid.Empty ||
                u.Condition is not ("NORMAL" or "DAMAGED")) ||
            command.Units.Select(u => u.AllocationId).Distinct().Count() != command.Units.Count)
        {
            return Error.Validation("INVALID_RETURN_COMMAND", "Return command is incomplete or has duplicate allocations.");
        }

        var now = _timeProvider.GetUtcNow();
        var messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.CommandTypes.ApplyReturnedUnits,
            command.OperationKey);
        var key = command.RentalOrderId.ToString("D");
        var envelope = new KafkaMessageEnvelope<ApplyReturnedUnitsCommandPayload>(
            messageId, KafkaContractSpec.CommandTypes.ApplyReturnedUnits,
            KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
            key, now, command.ReturnRecordId.ToString("D"), null, command);
        return await _outboxWriter.EnqueueAsync(
            _kafkaOptions.InventoryReturnCommandsTopic, key, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueCreateHoldAsync(
        CreateHoldCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (command == null)
        {
            return Error.Validation("INVALID_COMMAND", "CreateHoldCommandPayload cannot be null.");
        }

        if (command.RentalOrderId == Guid.Empty)
        {
            return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        }

        if (command.RentalItemId == Guid.Empty)
        {
            return Error.Validation("INVALID_RENTAL_ITEM_ID", "RentalItemId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(command.OperationKey))
        {
            return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");
        }

        if (command.Request == null)
        {
            return Error.Validation("INVALID_REQUEST", "CreateHoldRequest cannot be null.");
        }

        if (command.Request.ListingId == Guid.Empty)
        {
            return Error.Validation("INVALID_LISTING_ID", "ListingId cannot be empty.");
        }

        if (command.Request.Quantity <= 0)
        {
            return Error.Validation("INVALID_QUANTITY", "Quantity must be greater than zero.");
        }

        if (command.Request.From > command.Request.To)
        {
            return Error.Validation("INVALID_DATE_RANGE", "ExpectedStartDate cannot be after ExpectedEndDate.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.CommandTypes.CreateHold,
            command.OperationKey);

        string topic = _kafkaOptions.InventoryCommandsTopic;
        string partitionKey = command.RentalOrderId.ToString("D");

        var envelope = new KafkaMessageEnvelope<CreateHoldCommandPayload>(
            messageId,
            KafkaContractSpec.CommandTypes.CreateHold,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            partitionKey,
            utcNow,
            correlationId,
            causationId,
            command);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueReleaseAllocationAsync(
        ReleaseAllocationCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (command == null)
        {
            return Error.Validation("INVALID_COMMAND", "ReleaseAllocationCommandPayload cannot be null.");
        }

        if (command.RentalOrderId == Guid.Empty)
        {
            return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(command.AllocationGroupId))
        {
            return Error.Validation("INVALID_ALLOCATION_GROUP_ID", "AllocationGroupId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(command.OperationKey))
        {
            return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");
        }

        if (command.Request == null)
        {
            return Error.Validation("INVALID_REQUEST", "ReleaseAllocationRequest cannot be null.");
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.CommandTypes.ReleaseAllocation,
            command.OperationKey);

        string topic = _kafkaOptions.InventoryCommandsTopic;
        string partitionKey = command.RentalOrderId.ToString("D");

        var envelope = new KafkaMessageEnvelope<ReleaseAllocationCommandPayload>(
            messageId,
            KafkaContractSpec.CommandTypes.ReleaseAllocation,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            partitionKey,
            utcNow,
            correlationId,
            causationId,
            command);

        return await _outboxWriter.EnqueueAsync(topic, partitionKey, envelope, cancellationToken);
    }
}
