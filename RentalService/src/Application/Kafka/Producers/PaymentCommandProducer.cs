#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Producers;

public sealed class PaymentCommandProducer : IPaymentCommandProducer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IOutboxRepository _outboxRepository;
    private readonly KafkaOptions _kafkaOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaymentCommandProducer> _logger;

    public PaymentCommandProducer(
        IOutboxRepository outboxRepository,
        IOptions<KafkaOptions> kafkaOptions,
        TimeProvider? timeProvider = null,
        ILogger<PaymentCommandProducer>? logger = null)
    {
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _kafkaOptions = kafkaOptions?.Value ?? new KafkaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentCommandProducer>.Instance;
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueCreateInvoiceAsync(
        CreateInvoiceCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default)
    {
        if (command == null)
        {
            return Error.Validation("INVALID_COMMAND", "CreateInvoiceCommandPayload cannot be null.");
        }

        if (command.RentalOrderId == Guid.Empty)
        {
            return Error.Validation("INVALID_RENTAL_ORDER_ID", "RentalOrderId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(command.OperationKey))
        {
            return Error.Validation("INVALID_OPERATION_KEY", "OperationKey cannot be empty.");
        }

        if (command.Request == null)
        {
            return Error.Validation("INVALID_REQUEST", "CreateInvoiceRequest cannot be null.");
        }

        if (command.Request.RentalOrderId != command.RentalOrderId)
        {
            return Error.Validation("MISMATCHED_ORDER_ID", "Request RentalOrderId does not match command RentalOrderId.");
        }

        var requestValidation = command.Request.Validate();
        if (requestValidation.IsFailure)
        {
            return requestValidation.Error;
        }

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.CommandTypes.CreateInvoice,
            command.OperationKey);

        string topic = _kafkaOptions.PaymentCommandsTopic;
        string partitionKey = command.RentalOrderId.ToString("D");

        var envelope = new KafkaMessageEnvelope<CreateInvoiceCommandPayload>(
            messageId,
            KafkaContractSpec.CommandTypes.CreateInvoice,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.RentalService,
            partitionKey,
            utcNow,
            correlationId,
            causationId,
            command);

        string payloadJson = JsonSerializer.Serialize(envelope, JsonOptions);

        var outboxMessage = new RentalOutboxMessage(
            messageId,
            topic,
            partitionKey,
            KafkaContractSpec.CommandTypes.CreateInvoice,
            payloadJson,
            utcNow);

        var enqueueResult = await _outboxRepository.EnqueueAtomicAsync(outboxMessage, cancellationToken);
        if (enqueueResult.IsFailure)
        {
            _logger.LogWarning("Failed to enqueue CreateInvoice command {MessageId}: {Error}", messageId, enqueueResult.Error.Message);
            return enqueueResult.Error;
        }

        _logger.LogInformation("Enqueued CreateInvoice command {MessageId} for Order {OrderId} (Replay={IsReplay})",
            messageId, command.RentalOrderId, enqueueResult.Value.IsReplay);

        return new ProducerEnqueueResult(
            messageId,
            enqueueResult.Value.IsReplay,
            KafkaContractSpec.CommandTypes.CreateInvoice,
            topic,
            partitionKey);
    }
}
