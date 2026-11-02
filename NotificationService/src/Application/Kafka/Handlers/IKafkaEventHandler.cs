using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Kafka.Handlers;

public sealed record NotificationEventIntent(
    string EventType,
    Guid AggregateId,
    long OrderVersion,
    bool IsTerminal,
    string? TerminalState,
    Guid? RenterId,
    Guid? SupplierId,
    Guid? ReturnRequestId,
    Dictionary<string, object?> RenderData);

public interface IKafkaEventHandler
{
    string EventType { get; }
    Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope);
}
