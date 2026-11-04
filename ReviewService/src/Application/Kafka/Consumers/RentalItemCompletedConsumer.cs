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
/// Tuân thủ quy trình Transactional Inbox, Hydration an toàn ngoài giao dịch DB và commit offset sau khi lưu trữ thành công.
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

    public async Task<Result> ConsumeRawAsync(
        string messageJson,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return Result.Failure(Error.Validation("EMPTY_MESSAGE", "Nội dung bản tin Kafka rỗng."));
        }

        KafkaMessageEnvelope<RentalItemCompletedPayload>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<KafkaMessageEnvelope<RentalItemCompletedPayload>>(messageJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Không thể deserialize bản tin Kafka RentalItemCompleted.");
            // Đẩy vào DLQ do lỗi định dạng nghiêm trọng và commit offset
            await _kafkaProducer.ProduceAsync(DlqTopic, Guid.NewGuid().ToString("D"), messageJson, cancellationToken);
            if (commitOffsetAsync != null)
            {
                await commitOffsetAsync();
            }
            return Result.Failure(Error.Validation("MALFORMED_JSON", "Bản tin không đúng định dạng envelope v1."));
        }

        if (envelope == null || envelope.Payload == null)
        {
            await _kafkaProducer.ProduceAsync(DlqTopic, Guid.NewGuid().ToString("D"), messageJson, cancellationToken);
            if (commitOffsetAsync != null)
            {
                await commitOffsetAsync();
            }
            return Result.Failure(Error.Validation("INVALID_ENVELOPE", "Envelope hoặc payload của bản tin rỗng."));
        }

        return await ConsumeAsync(envelope, commitOffsetAsync, cancellationToken);
    }

    public async Task<Result> ConsumeAsync(
        KafkaMessageEnvelope<RentalItemCompletedPayload> envelope,
        Func<Task>? commitOffsetAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var payload = envelope.Payload;
        if (payload == null || payload.RentalItemId == Guid.Empty || payload.RentalOrderId == Guid.Empty)
        {
            _logger?.LogWarning("Bản tin {MessageId} thiếu thông tin rentalOrderId hoặc rentalItemId bắt buộc.", envelope.MessageId);
            await PublishDlqAsync(envelope, cancellationToken);
            if (commitOffsetAsync != null)
            {
                await commitOffsetAsync();
            }
            return Result.Failure(Error.Validation("INVALID_PAYLOAD", "Payload thiếu rentalOrderId hoặc rentalItemId."));
        }

        // 1. Kiểm tra chống trùng lặp (Inbox Pattern)
        bool alreadyProcessed = await _inboxRepository
            .ExistsAsync(envelope.MessageId, ConsumerGroup, cancellationToken);

        if (alreadyProcessed)
        {
            _logger?.LogInformation("Bản tin {MessageId} đã được xử lý bởi group {Group}. Commit offset và bỏ qua.",
                envelope.MessageId, ConsumerGroup);
            if (commitOffsetAsync != null)
            {
                await commitOffsetAsync();
            }
            return Result.Success();
        }

        // 2. Hydrate dữ liệu từ RentalService qua HTTP TUYỆT ĐỐI NGOÀI DB TRANSACTION
        Result<RentalItemReviewSnapshotDto> snapshotResult = null!;
        int maxRetries = 3;
        TimeSpan delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            snapshotResult = await _rentalClient.GetReviewSnapshotAsync(
                payload.RentalOrderId,
                payload.RentalItemId,
                cancellationToken);

            if (snapshotResult.IsSuccess) break;

            if (snapshotResult.Error.Code == "RENTAL_ITEM_NOT_FOUND")
            {
                // Lỗi vĩnh viễn không cần retry
                break;
            }

            if (attempt < maxRetries)
            {
                _logger?.LogWarning("Lỗi tạm thời khi hydrate snapshot ({Error}). Retry lần {Attempt}/{MaxRetries} sau {Delay}s.",
                    snapshotResult.Error.Message, attempt + 1, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
            }
        }

        if (snapshotResult.IsFailure)
        {
            _logger?.LogError("Thất bại khi hydrate snapshot cho đơn {OrderId}, mục {ItemId}: {Error}",
                payload.RentalOrderId, payload.RentalItemId, snapshotResult.Error.Message);

            if (snapshotResult.Error.Code == "RENTAL_ITEM_NOT_FOUND")
            {
                // Đẩy vào DLQ và commit offset khi bản tin tham chiếu mục không tồn tại
                await PublishDlqAsync(envelope, cancellationToken);
                if (commitOffsetAsync != null)
                {
                    await commitOffsetAsync();
                }
            }

            return Result.Failure(snapshotResult.Error);
        }

        var snapshot = snapshotResult.Value;

        // 3. Mở Database Transaction: Cập nhật review_projections và ghi inbox_messages nguyên tử
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        var upsertResult = await _eligibilityService.UpsertEligibilityAsync(snapshot, cancellationToken);
        if (upsertResult.IsFailure)
        {
            await tx.RollbackAsync(cancellationToken);
            return upsertResult;
        }

        var inboxReceipt = new InboxMessage(
            envelope.MessageId,
            ConsumerGroup,
            envelope.MessageType,
            envelope.OccurredAt,
            _timeProvider.GetUtcNow());

        await _inboxRepository.AddAsync(inboxReceipt, cancellationToken);

        await tx.CommitAsync(cancellationToken);

        // 4. Chỉ sau khi DB Transaction commit thành công mới commit Kafka offset về broker
        if (commitOffsetAsync != null)
        {
            await commitOffsetAsync();
        }

        return Result.Success();
    }

    private Task<bool> PublishDlqAsync(
        KafkaMessageEnvelope<RentalItemCompletedPayload> envelope,
        CancellationToken cancellationToken)
    {
        string key = envelope.Payload?.RentalItemId != null && envelope.Payload.RentalItemId != Guid.Empty
            ? envelope.Payload.RentalItemId.ToString("D")
            : envelope.MessageId.ToString("D");

        string payloadJson = JsonSerializer.Serialize(envelope);
        return _kafkaProducer.ProduceAsync(DlqTopic, key, payloadJson, cancellationToken);
    }
}
