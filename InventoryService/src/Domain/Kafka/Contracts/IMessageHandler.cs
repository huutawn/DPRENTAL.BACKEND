using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.Kafka.Contracts;

/// <summary>
/// Hợp đồng xử lý thông điệp MessageEnvelope nhận được từ Kafka topic.
/// Không chứa bất kỳ kiểu dữ liệu nào của Confluent.Kafka.
/// </summary>
public interface IMessageHandler<T>
{
    /// <summary>
    /// Thực thi xử lý logic nghiệp vụ cho thông điệp đã deserialize.
    /// </summary>
    Task<Result> HandleAsync(MessageEnvelope<T> envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Hợp đồng nội bộ để dispatch thông điệp từ wire JSON tới handler tương ứng.
/// </summary>
public interface IUntypedMessageHandler
{
    Task<Result> HandleUntypedAsync(string wireJson, CancellationToken cancellationToken);
}
