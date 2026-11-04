using Dprental.ReviewService.Application.Kafka.Contracts;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Kafka.Consumers;

/// <summary>
/// Kết quả xử lý bản tin Kafka bởi consumer handler.
/// </summary>
public enum ProcessOutcome
{
    /// <summary>Đã xử lý thành công và lưu trữ nguyên tử (hoặc là duplicate idempotently handled).</summary>
    Handled,
    /// <summary>Event type không thuộc diện xử lý của consumer trên topic dùng chung, an toàn để commit offset.</summary>
    Ignored,
    /// <summary>Bản tin lỗi vĩnh viễn đã được gửi thành công vào DLQ kèm broker ACK, an toàn để commit offset.</summary>
    DeadLettered,
    /// <summary>Lỗi tạm thời hoặc DLQ publish chưa nhận được ACK; worker giữ nguyên offset và retry sau backoff.</summary>
    Retry
}

/// <summary>
/// Ngữ cảnh bản tin Kafka tiếp nhận từ transport worker.
/// </summary>
public sealed record KafkaMessageContext(
    string Topic,
    int Partition,
    long Offset,
    string? Key,
    string RawPayload);

/// <summary>
/// Hợp đồng consumer tiếp nhận sự kiện RentalItemCompleted từ topic rental.events.v1.
/// </summary>
public interface IRentalItemCompletedConsumer
{
    /// <summary>
    /// Xử lý bản tin với ngữ cảnh transport cụ thể, trả về ProcessOutcome rõ ràng.
    /// </summary>
    Task<ProcessOutcome> ProcessMessageAsync(
        KafkaMessageContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xử lý bản tin envelope đã deserialize (adapter hỗ trợ unit/acceptance tests).
    /// </summary>
    Task<Result> ConsumeAsync(
        KafkaMessageEnvelope<RentalItemCompletedPayload> envelope,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xử lý chuỗi JSON thô từ Kafka broker (adapter hỗ trợ unit/acceptance tests).
    /// </summary>
    Task<Result> ConsumeRawAsync(
        string messageJson,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default);
}
