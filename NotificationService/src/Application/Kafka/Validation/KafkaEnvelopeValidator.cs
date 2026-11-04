using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Kafka.Validation;

public static class KafkaEnvelopeValidator
{
    private static readonly HashSet<string> AllowedProducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "RentalService",
        "rental-service",
        "PaymentService",
        "payment-service",
        "ReviewService",
        "review-service"
    };

    public const int ExpectedVersion = 1;

    public static Result Validate(KafkaMessageEnvelope? envelope)
    {
        if (envelope == null)
        {
            return Result.Failure(Error.Validation("NULL_ENVELOPE", "Kafka message envelope không được để trống."));
        }

        if (envelope.MessageId == Guid.Empty)
        {
            return Result.Failure(Error.Validation("INVALID_MESSAGE_ID", "MessageId phải là GUID hợp lệ và khác Guid.Empty."));
        }

        if (string.IsNullOrWhiteSpace(envelope.MessageType))
        {
            return Result.Failure(Error.Validation("MISSING_MESSAGE_TYPE", "MessageType không được để trống."));
        }

        if (string.IsNullOrWhiteSpace(envelope.Producer) || !AllowedProducers.Contains(envelope.Producer))
        {
            return Result.Failure(Error.Validation("INVALID_PRODUCER", $"Producer không hợp lệ: '{envelope.Producer}'. Phải thuộc danh sách producer được ủy quyền."));
        }

        if (envelope.Version <= 0)
        {
            return Result.Failure(Error.Validation("UNSUPPORTED_VERSION", $"Version '{envelope.Version}' không được hỗ trợ. Version phải > 0."));
        }

        if (envelope.OccurredAt == default)
        {
            return Result.Failure(Error.Validation("INVALID_OCCURRED_AT", "OccurredAt không hợp lệ."));
        }

        return Result.Success();
    }
}
