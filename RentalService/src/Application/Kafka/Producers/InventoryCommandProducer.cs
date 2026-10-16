#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
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
        if (command == null) return Error.Validation("INVALID_COMMAND", "CreateHoldCommandPayload cannot be null.");
        if (command.RentalOrderId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        if (command.RentalItemId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ITEM_ID", "RentalItemId cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.OperationKey)) return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");
        if (command.EquipmentListingId == Guid.Empty) return Error.Validation("INVALID_LISTING_ID", "EquipmentListingId cannot be empty.");
        if (command.Quantity <= 0) return Error.Validation("INVALID_QUANTITY", "Quantity must be greater than zero.");

        var envelope = new KafkaMessageEnvelope<CreateHoldCommandPayload>(
            MessageId: OutboxMessageIdGenerator.ComputeDeterministicId(KafkaContractSpec.Producers.RentalService, KafkaContractSpec.CommandTypes.CreateHold, command.OperationKey),
            MessageType: KafkaContractSpec.CommandTypes.CreateHold,
            Version: KafkaContractSpec.CurrentEnvelopeVersion,
            Producer: KafkaContractSpec.Producers.RentalService,
            AggregateId: command.RentalOrderId.ToString("D"),
            OccurredAt: _timeProvider.GetUtcNow(),
            CorrelationId: string.IsNullOrWhiteSpace(correlationId) ? command.RentalOrderId.ToString("D") : correlationId,
            CausationId: causationId,
            Payload: command);

        return await _outboxWriter.EnqueueAsync(
            _kafkaOptions.InventoryCommandsTopic,
            command.RentalOrderId.ToString("D"),
            envelope,
            cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueExtendHoldAsync(
        ExtendHoldCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (command == null) return Error.Validation("INVALID_COMMAND", "ExtendHoldCommandPayload cannot be null.");
        if (command.RentalOrderId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        if (command.RentalItemId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ITEM_ID", "RentalItemId cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.OperationKey)) return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");

        var envelope = new KafkaMessageEnvelope<ExtendHoldCommandPayload>(
            MessageId: OutboxMessageIdGenerator.ComputeDeterministicId(KafkaContractSpec.Producers.RentalService, KafkaContractSpec.CommandTypes.ExtendHold, command.OperationKey),
            MessageType: KafkaContractSpec.CommandTypes.ExtendHold,
            Version: KafkaContractSpec.CurrentEnvelopeVersion,
            Producer: KafkaContractSpec.Producers.RentalService,
            AggregateId: command.RentalOrderId.ToString("D"),
            OccurredAt: _timeProvider.GetUtcNow(),
            CorrelationId: string.IsNullOrWhiteSpace(correlationId) ? command.RentalOrderId.ToString("D") : correlationId,
            CausationId: causationId,
            Payload: command);

        return await _outboxWriter.EnqueueAsync(
            _kafkaOptions.InventoryCommandsTopic,
            command.RentalOrderId.ToString("D"),
            envelope,
            cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueConfirmAllocationAsync(
        ConfirmAllocationCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (command == null) return Error.Validation("INVALID_COMMAND", "ConfirmAllocationCommandPayload cannot be null.");
        if (command.RentalOrderId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        if (command.RentalItemId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ITEM_ID", "RentalItemId cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.OperationKey)) return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");

        // MessageId cố định theo OperationKey: gửi lại cùng khóa hoặc expiry worker chốt nốt không ghi thêm lệnh trùng vào Outbox.
        var envelope = new KafkaMessageEnvelope<ConfirmAllocationCommandPayload>(
            MessageId: OutboxMessageIdGenerator.ComputeDeterministicId(
                KafkaContractSpec.Producers.RentalService,
                KafkaContractSpec.CommandTypes.ConfirmAllocation,
                command.OperationKey),
            MessageType: KafkaContractSpec.CommandTypes.ConfirmAllocation,
            Version: KafkaContractSpec.CurrentEnvelopeVersion,
            Producer: KafkaContractSpec.Producers.RentalService,
            AggregateId: command.RentalOrderId.ToString("D"),
            OccurredAt: _timeProvider.GetUtcNow(),
            CorrelationId: string.IsNullOrWhiteSpace(correlationId) ? command.RentalOrderId.ToString("D") : correlationId,
            CausationId: causationId,
            Payload: command);

        return await _outboxWriter.EnqueueAsync(
            _kafkaOptions.InventoryCommandsTopic,
            command.RentalOrderId.ToString("D"),
            envelope,
            cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueReleaseAllocationAsync(
        ReleaseAllocationCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (command == null) return Error.Validation("INVALID_COMMAND", "ReleaseAllocationCommandPayload cannot be null.");
        if (command.RentalOrderId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        if (command.RentalItemId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ITEM_ID", "RentalItemId cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.OperationKey)) return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.Reason)) return Error.Validation("INVALID_REASON", "Reason cannot be empty.");

        var envelope = new KafkaMessageEnvelope<ReleaseAllocationCommandPayload>(
            MessageId: OutboxMessageIdGenerator.ComputeDeterministicId(KafkaContractSpec.Producers.RentalService, KafkaContractSpec.CommandTypes.ReleaseAllocation, command.OperationKey),
            MessageType: KafkaContractSpec.CommandTypes.ReleaseAllocation,
            Version: KafkaContractSpec.CurrentEnvelopeVersion,
            Producer: KafkaContractSpec.Producers.RentalService,
            AggregateId: command.RentalOrderId.ToString("D"),
            OccurredAt: _timeProvider.GetUtcNow(),
            CorrelationId: string.IsNullOrWhiteSpace(correlationId) ? command.RentalOrderId.ToString("D") : correlationId,
            CausationId: causationId,
            Payload: command);

        return await _outboxWriter.EnqueueAsync(
            _kafkaOptions.InventoryCommandsTopic,
            command.RentalOrderId.ToString("D"),
            envelope,
            cancellationToken);
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueMarkAssetsHandedOverAsync(
        MarkAssetsHandedOverCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (command == null) return Error.Validation("INVALID_COMMAND", "MarkAssetsHandedOverCommandPayload cannot be null.");
        if (command.RentalOrderId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        if (command.RentalItemId == Guid.Empty) return Error.Validation("INVALID_RENTAL_ITEM_ID", "RentalItemId cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.OperationKey)) return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");

        var envelope = new KafkaMessageEnvelope<MarkAssetsHandedOverCommandPayload>(
            MessageId: OutboxMessageIdGenerator.ComputeDeterministicId(KafkaContractSpec.Producers.RentalService, KafkaContractSpec.CommandTypes.MarkAssetsHandedOver, command.OperationKey),
            MessageType: KafkaContractSpec.CommandTypes.MarkAssetsHandedOver,
            Version: KafkaContractSpec.CurrentEnvelopeVersion,
            Producer: KafkaContractSpec.Producers.RentalService,
            AggregateId: command.RentalOrderId.ToString("D"),
            OccurredAt: _timeProvider.GetUtcNow(),
            CorrelationId: string.IsNullOrWhiteSpace(correlationId) ? command.RentalOrderId.ToString("D") : correlationId,
            CausationId: causationId,
            Payload: command);

        return await _outboxWriter.EnqueueAsync(
            _kafkaOptions.InventoryCommandsTopic,
            command.RentalOrderId.ToString("D"),
            envelope,
            cancellationToken);
    }

}
