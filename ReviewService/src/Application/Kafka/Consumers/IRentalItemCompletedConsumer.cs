using Dprental.ReviewService.Application.Kafka.Contracts;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Kafka.Consumers;

/// <summary>
/// Hợp đồng consumer tiếp nhận sự kiện RentalItemCompleted từ topic rental.events.v1.
/// </summary>
public interface IRentalItemCompletedConsumer
{
    /// <summary>
    /// Xử lý bản tin envelope đã deserialize.
    /// </summary>
    Task<Result> ConsumeAsync(
        KafkaMessageEnvelope<RentalItemCompletedPayload> envelope,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xử lý chuỗi JSON thô từ Kafka broker.
    /// </summary>
    Task<Result> ConsumeRawAsync(
        string messageJson,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default);
}
