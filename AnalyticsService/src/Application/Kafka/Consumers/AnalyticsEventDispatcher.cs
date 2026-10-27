using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Events.Payment;
using Dprental.AnalyticsService.Application.Events.Rental;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Application.Projections;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>
/// Xử lý trọn vẹn MỘT message:
/// - rental.events.v1: phong bì → inbox (processed_rental_events) → định tuyến → projector → lưu facts + inbox một lần;
/// - payment.analytics.v1: proof → phong bì → định tuyến → projector (chống trùng bằng messageId/LogicalEventId) → lưu.
/// Lỗi dữ liệu → dead letter rồi đi tiếp; lỗi tạm thời (DB, thiếu key proof) → RetryLater.
/// </summary>
public sealed partial class AnalyticsEventDispatcher : IAnalyticsEventDispatcher
{
    private readonly AnalyticsKafkaOptions _options;
    private readonly PaymentProofVerifier _proofVerifier;
    private readonly IProcessedRentalEventRepository _processed;
    private readonly IDeadLetterRepository _deadLetters;
    private readonly IAnalyticsUnitOfWork _unitOfWork;
    private readonly RentalOrderProjector _orders;
    private readonly RentalItemProjector _items;
    private readonly PaymentFinancialProjector _financial;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnalyticsEventDispatcher> _logger;

    public AnalyticsEventDispatcher(
        IOptions<AnalyticsKafkaOptions> options,
        PaymentProofVerifier proofVerifier,
        IProcessedRentalEventRepository processed,
        IDeadLetterRepository deadLetters,
        IAnalyticsUnitOfWork unitOfWork,
        RentalOrderProjector orders,
        RentalItemProjector items,
        PaymentFinancialProjector financial,
        TimeProvider timeProvider,
        ILogger<AnalyticsEventDispatcher> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _proofVerifier = proofVerifier ?? throw new ArgumentNullException(nameof(proofVerifier));
        _processed = processed ?? throw new ArgumentNullException(nameof(processed));
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
            if (string.Equals(record.Topic, _options.RentalTopic, StringComparison.Ordinal))
            {
                return await DispatchRentalAsync(record, cancellationToken);
            }

            if (string.Equals(record.Topic, _options.PaymentTopic, StringComparison.Ordinal))
            {
                return await DispatchPaymentAsync(record, cancellationToken);
            }

            return new DispatchResult(DispatchOutcome.Skipped);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Lỗi tạm thời (mất kết nối DB, deadlock, trùng khóa do hai lần giao đồng thời...):
            // không ghi dead letter, bỏ thay đổi trong bộ nhớ để lần thử lại bắt đầu sạch.
            _unitOfWork.DiscardChanges();
            LogRetry(_logger, exception, record.Topic, record.Partition, record.Offset);
            return new DispatchResult(DispatchOutcome.RetryLater);
        }
    }

    private async Task<DispatchResult> DispatchRentalAsync(KafkaRecord record, CancellationToken cancellationToken)
    {
        Result<KafkaEnvelope> envelopeResult = KafkaMessageReader.ReadEnvelope(record.Value);
        if (envelopeResult.IsFailure)
        {
            return await DeadLetterAsync(record, null, envelopeResult.Error, cancellationToken);
        }

        KafkaEnvelope envelope = envelopeResult.Value;
        if (await _processed.ExistsAsync(envelope.MessageId, cancellationToken))
        {
            return new DispatchResult(DispatchOutcome.Duplicate, envelope.MessageType);
        }

        // Loại event không dùng → bỏ qua, không ghi DB
        Task<Result>? projection = RouteRental(envelope, cancellationToken);
        if (projection is null)
        {
            return new DispatchResult(DispatchOutcome.Skipped, envelope.MessageType);
        }

        Result projected = await projection;
        if (projected.IsFailure)
        {
            return await DeadLetterAsync(record, envelope, projected.Error, cancellationToken);
        }

        // Facts (projector đã sửa trong bộ nhớ) + inbox → một lần SaveChanges = một transaction
        _processed.Add(new ProcessedRentalEvent { MessageId = envelope.MessageId, ProcessedAt = _timeProvider.GetUtcNow() });
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new DispatchResult(DispatchOutcome.Processed, envelope.MessageType);
    }

    private async Task<DispatchResult> DispatchPaymentAsync(KafkaRecord record, CancellationToken cancellationToken)
    {
        Result proof = _proofVerifier.Verify(record);
        if (proof.IsFailure)
        {
            if (proof.Error.Code == PaymentProofVerifier.ProofKeyUnknownCode)
            {
                LogProofKeyUnknown(_logger, record.Topic, record.Partition, record.Offset);
                return new DispatchResult(DispatchOutcome.RetryLater, ErrorCode: proof.Error.Code);
            }

            return await DeadLetterAsync(record, null, proof.Error, cancellationToken);
        }

        Result<KafkaEnvelope> envelopeResult = KafkaMessageReader.ReadEnvelope(record.Value);
        if (envelopeResult.IsFailure)
        {
            return await DeadLetterAsync(record, null, envelopeResult.Error, cancellationToken);
        }

        KafkaEnvelope envelope = envelopeResult.Value;
        Task<Result<bool>>? projection = RoutePayment(envelope, cancellationToken);
        if (projection is null)
        {
            return new DispatchResult(DispatchOutcome.Skipped, envelope.MessageType);
        }

        Result<bool> recorded = await projection;
        if (recorded.IsFailure)
        {
            return await DeadLetterAsync(record, envelope, recorded.Error, cancellationToken);
        }

        if (!recorded.Value)
        {
            return new DispatchResult(DispatchOutcome.Duplicate, envelope.MessageType);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new DispatchResult(DispatchOutcome.Processed, envelope.MessageType);
    }

    /// <summary>Bảng định tuyến rental: nguồn duy nhất quyết định event nào được xử lý.</summary>
    private Task<Result>? RouteRental(KafkaEnvelope envelope, CancellationToken ct) => envelope.MessageType switch
    {
        RentalAnalyticsEventTypes.RentalOrderCreated => Project<RentalOrderCreatedPayload>(envelope, p => _orders.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalOrderPaid => Project<RentalOrderPaidPayload>(envelope, p => _orders.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalOrderCompleted => Project<RentalOrderCompletedPayload>(envelope, p => _orders.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalOrderCancelled => Project<RentalOrderCancelledPayload>(envelope, p => _orders.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalOrderExpired => Project<RentalOrderExpiredPayload>(envelope, p => _orders.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalOrderRejected => Project<RentalOrderRejectedPayload>(envelope, p => _orders.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalItemConfirmed => Project<RentalItemConfirmedPayload>(envelope, p => _items.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalItemRejected => Project<RentalItemRejectedPayload>(envelope, p => _items.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalItemExpired => Project<RentalItemExpiredPayload>(envelope, p => _items.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalItemCancelled => Project<RentalItemCancelledPayload>(envelope, p => _items.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalItemHandoverCompleted => Project<RentalItemHandoverCompletedPayload>(envelope, p => _items.ApplyAsync(p, ct)),
        RentalAnalyticsEventTypes.RentalItemReturned => Project<RentalItemReturnedPayload>(envelope, p => _items.ApplyAsync(p, ct)),
        // Payload chưa có completedAt (chờ R2) → dùng occurredAt của phong bì
        RentalAnalyticsEventTypes.RentalItemCompleted => Project<RentalItemCompletedPayload>(envelope, p => _items.ApplyAsync(p, envelope.OccurredAt, ct)),
        _ => null
    };

    /// <summary>Bảng định tuyến payment.analytics.v1. Loại lạ → bỏ qua (Bảo cũ ném lỗi → kẹt partition).</summary>
    private Task<Result<bool>>? RoutePayment(KafkaEnvelope envelope, CancellationToken ct) => envelope.MessageType switch
    {
        PaymentAnalyticsEventTypes.InvoicePaidFinancial => Project<InvoicePaidFinancialPayload>(envelope, p => _financial.ApplyAsync(envelope, p, ct)),
        PaymentAnalyticsEventTypes.RentalItemFinancialSettled => Project<RentalItemFinancialSettledPayload>(envelope, p => _financial.ApplyAsync(envelope, p, ct)),
        PaymentAnalyticsEventTypes.RefundRequestedFinancial => Project<RefundRequestedFinancialPayload>(envelope, p => _financial.ApplyAsync(envelope, p, ct)),
        PaymentAnalyticsEventTypes.RefundCompletedFinancial => Project<RefundCompletedFinancialPayload>(envelope, p => _financial.ApplyAsync(envelope, p, ct)),
        PaymentAnalyticsEventTypes.SupplierPayoutCompleted => Project<SupplierPayoutCompletedPayload>(envelope, p => _financial.ApplyAsync(envelope, p, ct)),
        _ => null
    };

    // Đọc payload bằng đúng DTO; đọc lỗi thì trả lỗi (→ dead letter), không gọi projector
    private static async Task<Result> Project<TPayload>(KafkaEnvelope envelope, Func<TPayload, Task<Result>> apply)
        where TPayload : class
    {
        Result<TPayload> payload = KafkaMessageReader.ReadPayload<TPayload>(envelope);
        return payload.IsFailure ? Result.Failure(payload.Error) : await apply(payload.Value);
    }

    private static async Task<Result<bool>> Project<TPayload>(KafkaEnvelope envelope, Func<TPayload, Task<Result<bool>>> apply)
        where TPayload : class
    {
        Result<TPayload> payload = KafkaMessageReader.ReadPayload<TPayload>(envelope);
        return payload.IsFailure ? Result.Failure<bool>(payload.Error) : await apply(payload.Value);
    }

    private async Task<DispatchResult> DeadLetterAsync(
        KafkaRecord record, KafkaEnvelope? envelope, Error error, CancellationToken cancellationToken)
    {
        // Không lưu bất kỳ thay đổi nào projector đã làm dở
        _unitOfWork.DiscardChanges();

        // Worker sập rồi đọc lại cùng vị trí → không ghi thêm dòng thứ hai
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dead-lettered {MessageType} at {Topic}[{Partition}]@{Offset}: {ErrorCode}")]
    private static partial void LogDeadLettered(ILogger logger, string? messageType, string topic, int partition, long offset, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Transient failure at {Topic}[{Partition}]@{Offset}; message will be retried")]
    private static partial void LogRetry(ILogger logger, Exception exception, string topic, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment proof key not configured for {Topic}[{Partition}]@{Offset}; retrying until Kafka:ProofPublicKeys is updated")]
    private static partial void LogProofKeyUnknown(ILogger logger, string topic, int partition, long offset);
}
