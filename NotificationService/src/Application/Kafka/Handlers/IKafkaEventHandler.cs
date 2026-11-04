using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Kafka.Handlers;

public sealed record NotificationEventIntent(
    string EventType,
    Guid? OrderId,
    string ResourceType,
    string ResourceId,
    string? WatermarkScope,
    Guid? WatermarkId,
    long OrderVersion,
    bool IsTerminal,
    string? TerminalState,
    Guid? RenterId,
    Guid? SupplierId,
    Guid? ReturnRequestId,
    Dictionary<string, object?> RenderData,
    bool IsPaid = false,
    Guid? FinancialId = null,
    Guid? RentalItemId = null);

public interface IKafkaEventHandler
{
    string EventType { get; }
    Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope);
}
