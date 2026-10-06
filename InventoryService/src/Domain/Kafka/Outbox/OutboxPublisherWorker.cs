using System.Text.Json;
using Dprental.InventoryService.Domain.Caching;
using Dprental.InventoryService.Domain.Kafka.Contracts;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Domain.Kafka.Outbox;

/// <summary>
/// Background worker quét và xuất bản các thông điệp Transactional Outbox lên Kafka broker.
/// - Claim bản ghi bằng transaction ngắn với FOR UPDATE SKIP LOCKED.
/// - Xuất bản ra Kafka ngoài database transaction.
/// - Cập nhật kết quả kèm lease token (chống worker mất lease ghi đè).
/// - Strict ordering: không bao giờ publish vượt bản ghi pending trước đó cùng partition key.
/// - Ghi PublishedAt sau khi broker acknowledgement.
/// - Exponential backoff từ 1s, cap 60s, jitter 20%; tối đa 10 lần retry.
/// - Giữ lại bản ghi failed khi cạn budget để phục hồi thủ công.
/// </summary>
public sealed class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKafkaProducer _producer;
    private readonly IInventoryAvailabilityCache _cache;
    private readonly ILogger<OutboxPublisherWorker>? _logger;
    public int BatchSize { get; set; } = 20;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    public OutboxPublisherWorker(
        IServiceScopeFactory scopeFactory,
        IKafkaProducer producer,
        IInventoryAvailabilityCache? cache = null,
        ILogger<OutboxPublisherWorker>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _cache = cache ?? NoOpInventoryAvailabilityCache.Instance;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("OutboxPublisherWorker đã khởi động.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedCount = await ProcessBatchAsync(stoppingToken);

                if (processedCount == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
                else if (processedCount < BatchSize)
                {
                    // Lô chưa đầy, nghỉ ngắn để nhường tài nguyên
                    await Task.Delay(TimeSpan.FromMilliseconds(20), stoppingToken);
                }
                // Nếu processedCount == BatchSize, tiếp tục ngay lập tức để drain backlog
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi ngoài dự kiến trong OutboxPublisherWorker loop.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger?.LogInformation("OutboxPublisherWorker đã dừng.");
    }

    /// <summary>
    /// Claim một lô thông điệp và publish ra Kafka ngoài transaction.
    /// Trả về số lượng thông điệp đã claim và xử lý.
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var leaseToken = Guid.NewGuid();
        IReadOnlyList<OutboxMessage> claimedMessages;

        await using (var claimScope = _scopeFactory.CreateAsyncScope())
        {
            var store = claimScope.ServiceProvider.GetRequiredService<IOutboxStore>();
            claimedMessages = await store.ClaimPendingBatchAsync(
                BatchSize,
                leaseToken,
                LeaseDuration,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        if (claimedMessages.Count == 0)
        {
            return 0;
        }

        foreach (var message in claimedMessages)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await PublishSingleMessageAsync(message, leaseToken, cancellationToken);
        }

        return claimedMessages.Count;
    }

    private async Task PublishSingleMessageAsync(
        OutboxMessage message,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        await using var updateScope = _scopeFactory.CreateAsyncScope();
        var store = updateScope.ServiceProvider.GetRequiredService<IOutboxStore>();

        try
        {
            // Kiểm tra lease còn hiệu lực trước khi phát ra broker (tránh phát khi lease đã hết hạn do broker chậm)
            if (message.LockLeaseExpiresAt is not null && DateTimeOffset.UtcNow >= message.LockLeaseExpiresAt.Value.AddSeconds(-2))
            {
                _logger?.LogWarning(
                    "Lease của Outbox message {MessageId} đã hoặc sắp hết hạn trước khi xuất bản ({Expiry}). Dừng phát đợt này.",
                    message.Id, message.LockLeaseExpiresAt);
                return;
            }

            IReadOnlyDictionary<string, string>? headers = null;
            if (!string.IsNullOrWhiteSpace(message.Headers))
            {
                try
                {
                    headers = JsonSerializer.Deserialize<Dictionary<string, string>>(message.Headers);
                }
                catch
                {
                    // Header parse failure không chặn xuất bản payload chính
                }
            }

            // DPR-150: Invalidate cache của tài sản ngoài transaction trước khi publish ra Kafka
            if (TryExtractAvailabilityChangedAssetId(message, out var assetId))
            {
                try
                {
                    await _cache.InvalidateAssetAsync(assetId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "Lỗi invalidate cache cho tài sản {AssetId} trước khi publish Outbox message {MessageId}; tiếp tục xuất bản.",
                        assetId,
                        message.Id);
                }
            }

            // Publish ngoài transaction lên Kafka broker và chờ ACK
            await _producer.ProduceRawAsync(
                message.Topic,
                message.KafkaKey,
                message.Payload,
                headers,
                cancellationToken);

            // Broker acknowledgement thành công -> ghi nhận PublishedAt
            var now = DateTimeOffset.UtcNow;
            var success = await store.MarkAsPublishedAsync(message.Id, leaseToken, now, cancellationToken);

            if (!success)
            {
                _logger?.LogWarning(
                    "Outbox message {MessageId} đã publish nhưng mất lease token. Worker khác có thể đã claim lại.",
                    message.Id);
            }
            else
            {
                _logger?.LogDebug(
                    "Đã publish thành công Outbox message {MessageId} lên topic {Topic}.",
                    message.Id,
                    message.Topic);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var nextAttemptAt = RetryBackoffCalculator.CalculateNextAttemptAt(DateTimeOffset.UtcNow, message.RetryCount + 1);

            _logger?.LogWarning(
                ex,
                "Lỗi publish Outbox message {MessageId} lên topic {Topic} (Lần thử {Attempt}/10). Lên lịch thử lại lúc {NextAttemptAt}.",
                message.Id,
                message.Topic,
                message.RetryCount + 1,
                nextAttemptAt);

            await store.RecordFailureAsync(
                message.Id,
                leaseToken,
                ex.Message,
                nextAttemptAt,
                null,
                cancellationToken);
        }
    }

    private static bool TryExtractAvailabilityChangedAssetId(OutboxMessage message, out Guid assetId)
    {
        assetId = Guid.Empty;
        if (message.Topic != KafkaTopics.InventoryAssetEventsV1)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(message.Payload);
            if (doc.RootElement.TryGetProperty("messageType", out var typeElem) &&
                typeElem.ValueEquals(InventoryAssetAvailabilityEvents.MessageType))
            {
                if (doc.RootElement.TryGetProperty("aggregateId", out var aggElem) &&
                    Guid.TryParse(aggElem.GetString(), out assetId))
                {
                    return true;
                }

                if (Guid.TryParse(message.KafkaKey, out assetId))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Payload parse failure bỏ qua
        }

        return false;
    }
}
