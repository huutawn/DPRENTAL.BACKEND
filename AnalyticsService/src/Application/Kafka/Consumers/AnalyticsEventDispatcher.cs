using Dprental.AnalyticsService.Application.Kafka.Contracts;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Payment;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;
using Dprental.AnalyticsService.Application.Services.Projections;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>
/// Điều phối một message: ① đọc phong bì → ② inbox chống trùng → ③ định tuyến theo messageType
/// → ④ đọc payload + gọi projector → ⑤ lưu facts và inbox trong MỘT lần SaveChanges (một transaction).
/// Lỗi dữ liệu → dead letter; lỗi tạm thời (DB) → RetryLater để worker thử lại.
/// </summary>
public sealed partial class AnalyticsEventDispatcher : IAnalyticsEventDispatcher
{
    private readonly IInboxRepository _inbox;
    private readonly IDeadLetterRepository _deadLetters;
    private readonly IAnalyticsUnitOfWork _unitOfWork;
    private readonly RentalOrderProjector _orders;
    private readonly RentalItemProjector _items;
    private readonly FinancialProjector _financial;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnalyticsEventDispatcher> _logger;

    public AnalyticsEventDispatcher(
        IInboxRepository inbox,
        IDeadLetterRepository deadLetters,
        IAnalyticsUnitOfWork unitOfWork,
        RentalOrderProjector orders,
        RentalItemProjector items,
        FinancialProjector financial,
        TimeProvider timeProvider,
        ILogger<AnalyticsEventDispatcher> logger)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _financial = financial ?? throw new ArgumentNullException(nameof(financial));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DispatchResult> DispatchAsync(KafkaRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            // ① Phong bì hỏng → không biết là gì, ghi dead letter
            Result<KafkaEnvelope> envelopeResult = KafkaMessageReader.ReadEnvelope(record.Value);
            if (envelopeResult.IsFailure)
            {
                return await DeadLetterAsync(record, envelope: null, envelopeResult.Error, cancellationToken);
            }

            KafkaEnvelope envelope = envelopeResult.Value;

            // ② Đã xử lý message này rồi (Kafka gửi lại) → bỏ qua
            if (await _inbox.ExistsAsync(envelope.MessageId, cancellationToken))
            {
                return new DispatchResult(DispatchOutcome.Duplicate, envelope.MessageType);
            }

            // ③ Loại event không dùng → bỏ qua, không ghi DB (tránh inbox phình to)
            Task<Result>? projection = Route(envelope, cancellationToken);
            if (projection is null)
            {
                return new DispatchResult(DispatchOutcome.Skipped, envelope.MessageType);
            }

            // ④ Đọc payload + projector. Lỗi dữ liệu → bỏ mọi thay đổi, ghi dead letter
            Result projected = await projection;
            if (projected.IsFailure)
            {
                return await DeadLetterAsync(record, envelope, projected.Error, cancellationToken);
            }

            // ⑤ Facts (projector đã sửa trong bộ nhớ) + inbox → một lần SaveChanges = một transaction
            _inbox.Add(new AnalyticsInboxMessage
            {
                MessageId = envelope.MessageId,
                MessageType = envelope.MessageType,
                Topic = record.Topic,
                Partition = record.Partition,
                Offset = record.Offset,
                ProcessedAt = _timeProvider.GetUtcNow()
            });
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return new DispatchResult(DispatchOutcome.Processed, envelope.MessageType);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Lỗi tạm thời (mất kết nối DB, deadlock, xung đột xmin...): không ghi dead letter,
            // bỏ thay đổi trong bộ nhớ để lần thử lại bắt đầu sạch.
            _unitOfWork.DiscardChanges();
            LogRetry(_logger, exception, record.Topic, record.Partition, record.Offset);
            return new DispatchResult(DispatchOutcome.RetryLater);
        }
    }

    /// <summary>
    /// Bảng định tuyến: nguồn duy nhất quyết định Analytics xử lý event nào.
    /// Trả null nếu messageType không nằm trong danh sách.
    /// </summary>
    private Task<Result>? Route(KafkaEnvelope envelope, CancellationToken cancellationToken) => envelope.MessageType switch
    {
        // rental.events.v1 — cấp đơn
        AnalyticsMessageTypes.RentalOrderCreated => Project<RentalOrderCreatedPayload>(envelope, p => _orders.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalOrderPaid => Project<RentalOrderPaidPayload>(envelope, p => _orders.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalOrderCompleted => Project<RentalOrderCompletedPayload>(envelope, p => _orders.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalOrderCancelled => Project<RentalOrderCancelledPayload>(envelope, p => _orders.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalOrderExpired => Project<RentalOrderExpiredPayload>(envelope, p => _orders.ApplyAsync(p, cancellationToken)),

        // rental.events.v1 — cấp item
        AnalyticsMessageTypes.RentalItemConfirmed => Project<RentalItemConfirmedPayload>(envelope, p => _items.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalItemRejected => Project<RentalItemRejectedPayload>(envelope, p => _items.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalItemExpired => Project<RentalItemExpiredPayload>(envelope, p => _items.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalItemCancelled => Project<RentalItemCancelledPayload>(envelope, p => _items.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalItemHandoverCompleted => Project<RentalItemHandoverCompletedPayload>(envelope, p => _items.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RentalItemReturned => Project<RentalItemReturnedPayload>(envelope, p => _items.ApplyAsync(p, cancellationToken)),
        // Payload chưa có completedAt (chờ R2) → truyền occurredAt của phong bì
        AnalyticsMessageTypes.RentalItemCompleted => Project<RentalItemCompletedPayload>(envelope, p => _items.ApplyAsync(p, envelope.OccurredAt, cancellationToken)),

        // payment.events.v1
        AnalyticsMessageTypes.RentalSettled => Project<RentalSettledPayload>(envelope, p => _financial.ApplyAsync(p, cancellationToken)),
        AnalyticsMessageTypes.RefundCompleted => Project<RefundCompletedPayload>(envelope, p => _financial.ApplyAsync(p, cancellationToken)),

        _ => null
    };

    // Đọc payload bằng đúng DTO; đọc lỗi thì trả lỗi (→ dead letter), không gọi projector
    private static async Task<Result> Project<TPayload>(KafkaEnvelope envelope, Func<TPayload, Task<Result>> apply)
        where TPayload : class
    {
        Result<TPayload> payload = KafkaMessageReader.ReadPayload<TPayload>(envelope);
        return payload.IsFailure ? Result.Failure(payload.Error) : await apply(payload.Value);
    }

    private async Task<DispatchResult> DeadLetterAsync(
        KafkaRecord record, KafkaEnvelope? envelope, Error error, CancellationToken cancellationToken)
    {
        // Không lưu bất kỳ thay đổi nào projector đã làm dở
        _unitOfWork.DiscardChanges();

        // Worker sập rồi thử lại cùng message → vị trí đã ghi thì không ghi thêm
        if (!await _deadLetters.ExistsAsync(record.Topic, record.Partition, record.Offset, cancellationToken))
        {
            _deadLetters.Add(new AnalyticsDeadLetter
            {
                Id = Guid.NewGuid(),
                Topic = record.Topic,
                Partition = record.Partition,
                Offset = record.Offset,
                MessageId = envelope?.MessageId,
                MessageType = envelope?.MessageType,
                ErrorCode = error.Code,
                FailedAt = _timeProvider.GetUtcNow()
            });
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Chỉ log vị trí + mã lỗi, không log nội dung message
        LogDeadLettered(_logger, envelope?.MessageType, record.Topic, record.Partition, record.Offset, error.Code);
        return new DispatchResult(DispatchOutcome.DeadLettered, envelope?.MessageType, error.Code);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Dead-lettered {MessageType} at {Topic}[{Partition}]@{Offset}: {ErrorCode}")]
    private static partial void LogDeadLettered(
        ILogger logger, string? messageType, string topic, int partition, long offset, string errorCode);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Transient failure at {Topic}[{Partition}]@{Offset}; message will be retried")]
    private static partial void LogRetry(ILogger logger, Exception exception, string topic, int partition, long offset);
}
