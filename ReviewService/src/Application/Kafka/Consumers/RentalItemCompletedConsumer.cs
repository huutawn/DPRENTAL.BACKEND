using System.Text.Json;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.Kafka.Contracts;
using Dprental.ReviewService.Application.Kafka.Producers;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Kafka.Consumers;

/// <summary>
/// Consumer tiếp nhận và xử lý sự kiện RentalItemCompleted từ topic rental.events.v1.
/// Tuân thủ quy trình Transactional Inbox, Hydration an toàn ngoài giao dịch DB, xác nhận DLQ sau broker ACK và commit offset an toàn.
/// </summary>
public sealed class RentalItemCompletedConsumer : IRentalItemCompletedConsumer
{
    public const string Topic = "rental.events.v1";
    public const string ConsumerGroup = "review-service-rental-events";
    public const string DlqTopic = "rental.events.review.dlq";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IInboxRepository _inboxRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRentalServiceClient _rentalClient;
    private readonly IReviewEligibilityService _eligibilityService;
    private readonly IKafkaProducer _kafkaProducer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalItemCompletedConsumer>? _logger;

    public RentalItemCompletedConsumer(
        IInboxRepository inboxRepository,
        IUnitOfWork unitOfWork,
        IRentalServiceClient rentalClient,
        IReviewEligibilityService eligibilityService,
        IKafkaProducer kafkaProducer,
        TimeProvider? timeProvider = null,
        ILogger<RentalItemCompletedConsumer>? logger = null)
    {
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _rentalClient = rentalClient ?? throw new ArgumentNullException(nameof(rentalClient));
        _eligibilityService = eligibilityService ?? throw new ArgumentNullException(nameof(eligibilityService));
        _kafkaProducer = kafkaProducer ?? throw new ArgumentNullException(nameof(kafkaProducer));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<ProcessOutcome> ProcessMessageAsync(
        KafkaMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string rawJson = context.RawPayload;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            _logger?.LogWarning("Bản tin rỗng trên topic {Topic}, partition {Partition}, offset {Offset}",
                context.Topic, context.Partition, context.Offset);
            bool dlqAck = await PublishDlqAsync(context, "EMPTY_MESSAGE", null, cancellationToken);
            return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Malformed JSON trên topic {Topic}, partition {Partition}, offset {Offset}",
                context.Topic, context.Partition, context.Offset);
            bool dlqAck = await PublishDlqAsync(context, "MALFORMED_JSON", null, cancellationToken);
            return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // 1. Kiểm tra messageType trên topic dùng chung (shared topic)
            string? messageType = null;
            if (root.TryGetProperty("messageType", out var mtProp) || root.TryGetProperty("MessageType", out mtProp))
            {
                messageType = mtProp.GetString();
            }

            if (!string.Equals(messageType, "RentalItemCompleted", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation("Event type '{MessageType}' trên topic {Topic} được bỏ qua bởi review consumer.",
                    messageType, context.Topic);
                return ProcessOutcome.Ignored;
            }

            // 2. Kiểm tra envelope trước khi dùng payload
            Guid messageId = Guid.Empty;
            if (root.TryGetProperty("messageId", out var midProp) || root.TryGetProperty("MessageId", out midProp))
            {
                midProp.TryGetGuid(out messageId);
            }

            int version = 0;
            if (root.TryGetProperty("version", out var vProp) || root.TryGetProperty("Version", out vProp))
            {
                vProp.TryGetInt32(out version);
            }

            string? producer = null;
            if (root.TryGetProperty("producer", out var pProp) || root.TryGetProperty("Producer", out pProp))
            {
                producer = pProp.GetString();
            }

            if (messageId == Guid.Empty || version != 1 || string.IsNullOrWhiteSpace(producer))
            {
                _logger?.LogWarning("Envelope validation không hợp lệ trên topic {Topic}, offset {Offset}. MessageId: {MessageId}, Version: {Version}, Producer: {Producer}",
                    context.Topic, context.Offset, messageId, version, producer);
                bool dlqAck = await PublishDlqAsync(context, "INVALID_ENVELOPE", messageId == Guid.Empty ? null : messageId, cancellationToken);
                return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
            }

            // 3. Payload validation
            if (!root.TryGetProperty("payload", out var payloadElement) && !root.TryGetProperty("Payload", out payloadElement))
            {
                _logger?.LogWarning("Bản tin {MessageId} thiếu trường payload.", messageId);
                bool dlqAck = await PublishDlqAsync(context, "MISSING_PAYLOAD", messageId, cancellationToken);
                return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
            }

            RentalItemCompletedPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<RentalItemCompletedPayload>(payloadElement.GetRawText(), JsonOptions);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi deserialize payload cho bản tin {MessageId}.", messageId);
                bool dlqAck = await PublishDlqAsync(context, "MALFORMED_PAYLOAD", messageId, cancellationToken);
                return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
            }

            if (payload == null || payload.RentalOrderId == Guid.Empty || payload.RentalItemId == Guid.Empty)
            {
                _logger?.LogWarning("Payload thiếu mã OrderId hoặc ItemId bắt buộc cho bản tin {MessageId}.", messageId);
                bool dlqAck = await PublishDlqAsync(context, "INVALID_PAYLOAD_IDENTIFIERS", messageId, cancellationToken);
                return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
            }

            // 4. Kiểm tra chống trùng lặp (Transactional Inbox)
            bool alreadyProcessed = await _inboxRepository.ExistsAsync(messageId, ConsumerGroup, cancellationToken);
            if (alreadyProcessed)
            {
                _logger?.LogInformation("Bản tin {MessageId} đã tồn tại trong inbox của group {Group}. Trả về Handled (idempotent).",
                    messageId, ConsumerGroup);
                return ProcessOutcome.Handled;
            }

            // 5. Hydrate dữ liệu từ RentalService qua HTTP TUYỆT ĐỐI NGOÀI DB TRANSACTION
            // Lỗi hydrate tạm thời retry 3 lần sau lần đầu với backoff 1/2/4s
            Result<RentalItemReviewSnapshotDto> snapshotResult = null!;
            var backoffs = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

            for (int attempt = 0; attempt <= backoffs.Length; attempt++)
            {
                snapshotResult = await _rentalClient.GetReviewSnapshotAsync(
                    payload.RentalOrderId,
                    payload.RentalItemId,
                    cancellationToken);

                if (snapshotResult.IsSuccess)
                {
                    break;
                }

                // Nếu lỗi vĩnh viễn (404 Không tìm thấy hoặc 400 Không hợp lệ) -> đi thẳng tới DLQ
                if (snapshotResult.Error.Code is "RENTAL_ITEM_NOT_FOUND" or "NOT_FOUND" or "INVALID_REQUEST" or "ITEM_NOT_COMPLETED")
                {
                    _logger?.LogWarning("Lỗi vĩnh viễn khi hydrate snapshot mục {ItemId}: {Code}. Đẩy vào DLQ.",
                        payload.RentalItemId, snapshotResult.Error.Code);
                    bool dlqAck = await PublishDlqAsync(context, snapshotResult.Error.Code, messageId, cancellationToken);
                    return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
                }

                if (attempt < backoffs.Length)
                {
                    _logger?.LogWarning("Lỗi tạm thời khi hydrate snapshot (lần thử {Attempt}): {Message}. Đang thử lại...",
                        attempt + 1, snapshotResult.Error.Message);
                    await Task.Delay(backoffs[attempt], cancellationToken);
                }
            }

            if (snapshotResult == null || snapshotResult.IsFailure)
            {
                _logger?.LogError("Đã thử lại 3 lần nhưng không thể hydrate snapshot cho mục {ItemId}. Đẩy vào DLQ.",
                    payload.RentalItemId);
                bool dlqAck = await PublishDlqAsync(context, snapshotResult?.Error.Code ?? "HYDRATION_RETRY_EXHAUSTED", messageId, cancellationToken);
                return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
            }

            // 6. Cam kết nguyên tử Projection và Inbox Receipt trong cùng một Transaction
            await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                var upsertRes = await _eligibilityService.UpsertEligibilityAsync(snapshotResult.Value, cancellationToken);
                if (upsertRes.IsFailure)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    bool dlqAck = await PublishDlqAsync(context, upsertRes.Error.Code, messageId, cancellationToken);
                    return dlqAck ? ProcessOutcome.DeadLettered : ProcessOutcome.Retry;
                }

                var nowUtc = _timeProvider.GetUtcNow();
                var inboxReceipt = new InboxMessage(
                    messageId,
                    ConsumerGroup,
                    "RentalItemCompleted",
                    nowUtc,
                    nowUtc);

                await _inboxRepository.AddAsync(inboxReceipt, cancellationToken);

                await _unitOfWork.CommitAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return ProcessOutcome.Handled;
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();

                // Xử lý race condition cạnh tranh giữa 2 worker: kiểm tra lại xem inbox đã được worker khác commit hay chưa
                bool exists = await _inboxRepository.ExistsAsync(messageId, ConsumerGroup, cancellationToken);
                if (exists)
                {
                    _logger?.LogInformation("Bản tin {MessageId} đã được commit bởi worker cạnh tranh. Trả về Handled.", messageId);
                    return ProcessOutcome.Handled;
                }

                _logger?.LogWarning(ex, "DbUpdateException khi lưu inbox/projection cho bản tin {MessageId}. Yêu cầu Retry.", messageId);
                return ProcessOutcome.Retry;
            }
        }
    }

    private async Task<bool> PublishDlqAsync(
        KafkaMessageContext context,
        string errorCode,
        Guid? messageId,
        CancellationToken cancellationToken)
    {
        string dlqKey = $"{context.Topic}:{context.Partition}:{context.Offset}";
        var dlqEnvelope = new
        {
            sourceTopic = context.Topic,
            sourcePartition = context.Partition,
            sourceOffset = context.Offset,
            sourceKey = context.Key,
            errorCode = errorCode,
            messageId = messageId,
            quarantinedAt = _timeProvider.GetUtcNow(),
            rawPayload = context.RawPayload
        };

        string payloadJson = JsonSerializer.Serialize(dlqEnvelope, JsonOptions);

        try
        {
            return await _kafkaProducer.ProduceAsync(DlqTopic, dlqKey, payloadJson, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Thất bại khi gửi vào DLQ topic {DlqTopic} với khóa {Key}.", DlqTopic, dlqKey);
            return false;
        }
    }

    public async Task<Result> ConsumeRawAsync(
        string messageJson,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default)
    {
        var context = new KafkaMessageContext(Topic, 0, 0, null, messageJson ?? string.Empty);
        var outcome = await ProcessMessageAsync(context, cancellationToken);

        if (outcome is ProcessOutcome.Handled or ProcessOutcome.Ignored or ProcessOutcome.DeadLettered)
        {
            if (commitOffsetAsync != null)
            {
                await commitOffsetAsync();
            }

            return outcome == ProcessOutcome.DeadLettered
                ? Result.Failure(Error.Validation("DEAD_LETTERED", "Bản tin đã được đưa vào DLQ."))
                : Result.Success();
        }

        return Result.Failure(Error.Failure("RETRY_REQUIRED", "Xử lý bản tin thất bại, yêu cầu retry."));
    }

    public async Task<Result> ConsumeAsync(
        KafkaMessageEnvelope<RentalItemCompletedPayload> envelope,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        return await ConsumeRawAsync(json, commitOffsetAsync, cancellationToken);
    }
}
