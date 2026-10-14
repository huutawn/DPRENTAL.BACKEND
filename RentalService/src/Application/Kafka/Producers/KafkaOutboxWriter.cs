using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Kafka.Producers;

public sealed class KafkaOutboxWriter : IKafkaOutboxWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IOutboxRepository _outboxRepository;
    private readonly ILogger<KafkaOutboxWriter> _logger;

    public KafkaOutboxWriter(
        IOutboxRepository outboxRepository,
        ILogger<KafkaOutboxWriter>? logger = null)
    {
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KafkaOutboxWriter>.Instance;
    }

    public async Task<Result<ProducerEnqueueResult>> EnqueueAsync<TPayload>(
        string topic,
        string partitionKey,
        KafkaMessageEnvelope<TPayload> candidateEnvelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateEnvelope);
        if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("Topic cannot be empty.", nameof(topic));
        if (string.IsNullOrWhiteSpace(partitionKey)) throw new ArgumentException("PartitionKey cannot be empty.", nameof(partitionKey));

        string candidateEnvelopeJson = JsonSerializer.Serialize(candidateEnvelope, JsonOptions);
        var candidateMessage = new RentalOutboxMessage(
            candidateEnvelope.MessageId,
            topic,
            partitionKey,
            candidateEnvelope.MessageType,
            candidateEnvelopeJson,
            candidateEnvelope.OccurredAt);

        var enqueueResult = await _outboxRepository.EnqueueAtomicAsync(candidateMessage, cancellationToken);
        if (enqueueResult.IsFailure)
        {
            return enqueueResult.Error;
        }

        if (!enqueueResult.Value.IsReplay)
        {
            return new ProducerEnqueueResult(
                candidateEnvelope.MessageId,
                IsReplay: false,
                candidateEnvelope.MessageType,
                topic,
                partitionKey);
        }

        var stored = enqueueResult.Value.Message;

        if (!string.Equals(stored.Topic, topic, StringComparison.Ordinal) ||
            !string.Equals(stored.PartitionKey, partitionKey, StringComparison.Ordinal) ||
            !string.Equals(stored.MessageType, candidateEnvelope.MessageType, StringComparison.Ordinal))
        {
            return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                $"MessageId '{candidateEnvelope.MessageId}' đã tồn tại với topic, partitionKey hoặc messageType mâu thuẫn.");
        }

        try
        {
            using var storedDoc = JsonDocument.Parse(stored.Payload);
            using var candidateDoc = JsonDocument.Parse(candidateEnvelopeJson);

            var storedRoot = storedDoc.RootElement;
            var candidateRoot = candidateDoc.RootElement;

            int storedVersion = storedRoot.TryGetProperty("version", out var vProp) && vProp.TryGetInt32(out var v) ? v : 0;
            if (storedVersion != candidateEnvelope.Version)
            {
                return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                    $"MessageId '{candidateEnvelope.MessageId}' đã tồn tại với version mâu thuẫn (stored: {storedVersion}, candidate: {candidateEnvelope.Version}).");
            }

            string storedProducer = storedRoot.TryGetProperty("producer", out var pProp) ? pProp.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(storedProducer, candidateEnvelope.Producer, StringComparison.Ordinal))
            {
                return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                    $"MessageId '{candidateEnvelope.MessageId}' đã tồn tại với producer mâu thuẫn (stored: '{storedProducer}', candidate: '{candidateEnvelope.Producer}').");
            }

            string storedAggregateId = storedRoot.TryGetProperty("aggregateId", out var aProp) ? aProp.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(storedAggregateId, candidateEnvelope.AggregateId, StringComparison.Ordinal))
            {
                return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                    $"MessageId '{candidateEnvelope.MessageId}' đã tồn tại với aggregateId mâu thuẫn (stored: '{storedAggregateId}', candidate: '{candidateEnvelope.AggregateId}').");
            }

            if (!storedRoot.TryGetProperty("payload", out var storedPayloadProp) ||
                !candidateRoot.TryGetProperty("payload", out var candidatePayloadProp))
            {
                return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                    $"MessageId '{candidateEnvelope.MessageId}' thiếu trường payload.");
            }

            if (string.Equals(candidateEnvelope.MessageType, KafkaContractSpec.EventTypes.DeadLetter, StringComparison.OrdinalIgnoreCase))
            {
                string storedOriginal = storedPayloadProp.TryGetProperty("originalPayload", out var sop) ? sop.GetString() ?? string.Empty : string.Empty;
                string candidateOriginal = candidatePayloadProp.TryGetProperty("originalPayload", out var cop) ? cop.GetString() ?? string.Empty : string.Empty;

                string storedReason = storedPayloadProp.TryGetProperty("reason", out var srp) ? srp.GetString() ?? string.Empty : string.Empty;
                string candidateReason = candidatePayloadProp.TryGetProperty("reason", out var crp) ? crp.GetString() ?? string.Empty : string.Empty;

                if (!string.Equals(storedOriginal, candidateOriginal, StringComparison.Ordinal) ||
                    !string.Equals(storedReason, candidateReason, StringComparison.Ordinal))
                {
                    return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                        $"DLQ MessageId '{candidateEnvelope.MessageId}' đã tồn tại với original content hoặc reason mâu thuẫn.");
                }
            }
            else
            {
                string storedPayloadJson = storedPayloadProp.GetRawText();
                string candidatePayloadJson = candidatePayloadProp.GetRawText();

                if (!CanonicalJson.AreEquivalent(storedPayloadJson, candidatePayloadJson))
                {
                    return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                        $"MessageId '{candidateEnvelope.MessageId}' đã tồn tại với nội dung business payload mâu thuẫn.");
                }
            }
        }
        catch (Exception ex)
        {
            return Error.Conflict("OUTBOX_MESSAGE_CONFLICT",
                $"MessageId '{candidateEnvelope.MessageId}' không thể so sánh payload: {ex.Message}");
        }

        return new ProducerEnqueueResult(
            candidateEnvelope.MessageId,
            IsReplay: true,
            candidateEnvelope.MessageType,
            topic,
            partitionKey);
    }
}
