using Dprental.NotificationService.Application.Kafka.Handlers;

namespace Dprental.NotificationService.Application.Kafka.Contracts;

public enum ContractResolutionStatus
{
    Handled,
    Ignored,
    Quarantined
}

public sealed record ContractResolution(
    ContractResolutionStatus Status,
    IKafkaEventHandler? Handler = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static ContractResolution HandledResult(IKafkaEventHandler handler) =>
        new(ContractResolutionStatus.Handled, Handler: handler);

    public static ContractResolution IgnoredResult() =>
        new(ContractResolutionStatus.Ignored);

    public static ContractResolution QuarantinedResult(string errorCode, string errorMessage) =>
        new(ContractResolutionStatus.Quarantined, ErrorCode: errorCode, ErrorMessage: errorMessage);
}

public interface IKafkaContractRegistry
{
    bool IsAllowedTopic(string topic);
    bool IsAllowedProducer(string producer);
    bool IsTopicProducerAllowed(string topic, string producer);
    bool IsVersionSupported(int version);
    IKafkaEventHandler? GetHandler(string topic, string producer, string messageType, int version);
    ContractResolution Resolve(string topic, KafkaMessageEnvelope envelope);
}

public sealed class KafkaContractRegistry : IKafkaContractRegistry
{
    public const string RentalEventsTopic = "rental.events.v1";
    public const string PaymentAnalyticsTopic = "payment.analytics.v1";
    public const int SupportedVersion = 1;

    private static readonly HashSet<string> AllowedTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        RentalEventsTopic,
        PaymentAnalyticsTopic
    };

    private static readonly HashSet<string> RentalProducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "RentalService",
        "rental-service"
    };

    private static readonly HashSet<string> PaymentProducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "PaymentService",
        "payment-service"
    };

    private static readonly HashSet<string> PaymentAnalyticsMessageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RefundCompletedFinancial",
        "SupplierPayoutCompleted"
    };

    // Key: (Topic, Producer, MessageType, Version)
    private readonly Dictionary<(string Topic, string Producer, string MessageType, int Version), IKafkaEventHandler> _bindings
        = new(new ContractKeyComparer());

    public KafkaContractRegistry(IEnumerable<IKafkaEventHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        foreach (var handler in handlers)
        {
            if (PaymentAnalyticsMessageTypes.Contains(handler.EventType))
            {
                // Payment analytics contracts strictly on payment.analytics.v1
                _bindings[(PaymentAnalyticsTopic, "PaymentService", handler.EventType, SupportedVersion)] = handler;
                _bindings[(PaymentAnalyticsTopic, "payment-service", handler.EventType, SupportedVersion)] = handler;
            }
            else
            {
                // Rental events contracts strictly on rental.events.v1
                _bindings[(RentalEventsTopic, "RentalService", handler.EventType, SupportedVersion)] = handler;
                _bindings[(RentalEventsTopic, "rental-service", handler.EventType, SupportedVersion)] = handler;
            }
        }
    }

    public bool IsAllowedTopic(string topic) =>
        !string.IsNullOrWhiteSpace(topic) && AllowedTopics.Contains(topic);

    public bool IsAllowedProducer(string producer) =>
        !string.IsNullOrWhiteSpace(producer) && (RentalProducers.Contains(producer) || PaymentProducers.Contains(producer));

    public bool IsTopicProducerAllowed(string topic, string producer)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(producer))
        {
            return false;
        }

        if (string.Equals(topic, RentalEventsTopic, StringComparison.OrdinalIgnoreCase))
        {
            return RentalProducers.Contains(producer);
        }

        if (string.Equals(topic, PaymentAnalyticsTopic, StringComparison.OrdinalIgnoreCase))
        {
            return PaymentProducers.Contains(producer);
        }

        return false;
    }

    public bool IsVersionSupported(int version) => version == SupportedVersion;

    public IKafkaEventHandler? GetHandler(string topic, string producer, string messageType, int version)
    {
        if (!IsTopicProducerAllowed(topic, producer) || !IsVersionSupported(version) || string.IsNullOrWhiteSpace(messageType))
        {
            return null;
        }

        _bindings.TryGetValue((topic, producer, messageType, version), out var handler);
        return handler;
    }

    public ContractResolution Resolve(string topic, KafkaMessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Version != SupportedVersion)
        {
            return ContractResolution.QuarantinedResult("UNSUPPORTED_VERSION",
                $"Version '{envelope.Version}' không được hỗ trợ. Version phải = {SupportedVersion}.");
        }

        if (!IsAllowedTopic(topic))
        {
            return ContractResolution.QuarantinedResult("INVALID_TOPIC",
                $"Topic '{topic}' không thuộc danh sách topic được ủy quyền.");
        }

        if (!IsAllowedProducer(envelope.Producer))
        {
            return ContractResolution.QuarantinedResult("INVALID_PRODUCER",
                $"Producer '{envelope.Producer}' không thuộc danh sách producer được ủy quyền.");
        }

        if (!IsTopicProducerAllowed(topic, envelope.Producer))
        {
            return ContractResolution.QuarantinedResult("INVALID_PRODUCER_FOR_TOPIC",
                $"Producer '{envelope.Producer}' không được phép phát hành bản tin trên topic '{topic}'.");
        }

        if (string.IsNullOrWhiteSpace(envelope.MessageType))
        {
            return ContractResolution.QuarantinedResult("MISSING_MESSAGE_TYPE",
                "MessageType không được để trống.");
        }

        var handler = GetHandler(topic, envelope.Producer, envelope.MessageType, envelope.Version);
        if (handler != null)
        {
            return ContractResolution.HandledResult(handler);
        }

        // Valid topic and producer, but unhandled messageType
        return ContractResolution.IgnoredResult();
    }

    private sealed class ContractKeyComparer : IEqualityComparer<(string Topic, string Producer, string MessageType, int Version)>
    {
        public bool Equals((string Topic, string Producer, string MessageType, int Version) x, (string Topic, string Producer, string MessageType, int Version) y) =>
            string.Equals(x.Topic, y.Topic, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Producer, y.Producer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.MessageType, y.MessageType, StringComparison.OrdinalIgnoreCase) &&
            x.Version == y.Version;

        public int GetHashCode((string Topic, string Producer, string MessageType, int Version) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Topic),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Producer),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MessageType),
                obj.Version);
    }
}
