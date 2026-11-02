using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Consumers;

public interface IPaymentEventsDispatcher
{
    /// <summary>
    /// Tiếp nhận JSON từ broker, kiểm tra envelope/proof và chống trùng bằng JSON chuẩn hóa.
    /// Lưu Inbox hoặc DLQ bền vững; loại sự kiện không hỗ trợ trả Ignored.
    /// Nếu persistence lỗi phải trả Failure để consumer không commit offset.
    /// </summary>
    Task<Result<KafkaIntakeDisposition>> ReceiveMessageAsync(
        string messageJson,
        string? topic = null,
        int? partition = null,
        long? offset = null,
        string? publicationProof = null,
        string? messageKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Đọc lại Inbox dưới khóa order, phân phối sự kiện Payment hoặc kết quả Inventory tới handler.
    /// Lưu trạng thái nghiệp vụ, Inbox và Outbox phát sinh trong cùng transaction.
    /// </summary>
    Task<Result> ProcessInboxMessageAsync(
        Guid inboxMessageId,
        CancellationToken cancellationToken = default);

}
