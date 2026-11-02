using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Kafka.Validation;

public static class KafkaEnvelopeValidator
{
    public const string ExpectedProducer = "RentalService";
    public const int ExpectedVersion = 1;

    public static Result Validate(KafkaMessageEnvelope? envelope)
    {
        if (envelope == null)
        {
            return Result.Failure(Error.Validation("NULL_ENVELOPE", "Kafka message envelope không được để trống."));
        }

        if (string.IsNullOrWhiteSpace(envelope.EventId) || !Guid.TryParse(envelope.EventId, out var eventGuid) || eventGuid == Guid.Empty)
        {
            return Result.Failure(Error.Validation("INVALID_EVENT_ID", "EventId phải là GUID hợp lệ và khác rỗng."));
        }

        if (!string.Equals(envelope.Producer, ExpectedProducer, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(Error.Validation("INVALID_PRODUCER", $"Producer không hợp lệ: '{envelope.Producer}'. Kỳ vọng: '{ExpectedProducer}'."));
        }

        if (envelope.Version != ExpectedVersion)
        {
            return Result.Failure(Error.Validation("UNSUPPORTED_VERSION", $"Version '{envelope.Version}' không được hỗ trợ. Kỳ vọng version: {ExpectedVersion}."));
        }

        if (string.IsNullOrWhiteSpace(envelope.EventType))
        {
            return Result.Failure(Error.Validation("MISSING_EVENT_TYPE", "EventType không được để trống."));
        }

        if (envelope.OccurredAt == default)
        {
            return Result.Failure(Error.Validation("INVALID_OCCURRED_AT", "OccurredAt không hợp lệ."));
        }

        return Result.Success();
    }
}
