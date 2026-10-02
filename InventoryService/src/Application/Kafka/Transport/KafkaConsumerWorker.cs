using System.Collections.Concurrent;
using System.Text.Json;
using Confluent.Kafka;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Kafka.Transport;

/// <summary>
/// Worker lắng nghe và điều phối các Consumer Groups của Kafka.
/// Tuân thủ:
/// - Tắt auto-commit và auto-offset-store.
/// - Mỗi partition chỉ có một message đang xử lý; pause partition khi xử lý/retry, tiếp tục polling partition khác.
/// - Hủy tác vụ khi mất partition assignment và không commit offset của partition đã mất.
/// - Chỉ subscribe khi có handler đăng ký.
/// - Atomic DLQ và Inbox terminal state trước khi commit offset.
/// - Log IDs, trạng thái và mã lỗi đã kiểm soát; không log payload hay credentials.
/// </summary>
public sealed class KafkaConsumerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KafkaHandlerRegistry _registry;
    private readonly KafkaOptions _options;
    private readonly IKafkaProducer _producer;
    private readonly ILogger<KafkaConsumerWorker>? _logger;

    public KafkaConsumerWorker(
        IServiceScopeFactory scopeFactory,
        KafkaHandlerRegistry registry,
        IOptions<KafkaOptions> options,
        IKafkaProducer producer,
        ILogger<KafkaConsumerWorker>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _options = options.Value;
        _producer = producer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var groupIds = _registry.GetDistinctGroupIds();
        if (groupIds.Count == 0)
        {
            _logger?.LogInformation("Không có message handler nào được đăng ký. Bỏ qua khởi tạo Kafka consumer.");
            return;
        }

        _logger?.LogInformation("Khởi động KafkaConsumerWorker cho {Count} consumer groups: [{Groups}]",
            groupIds.Count, string.Join(", ", groupIds));

        var consumerTasks = groupIds.Select(groupId => RunConsumerGroupLoopAsync(groupId, stoppingToken)).ToList();
        await Task.WhenAll(consumerTasks);
    }

    private async Task RunConsumerGroupLoopAsync(string groupId, CancellationToken stoppingToken)
    {
        var registrations = _registry.GetByGroupId(groupId);
        var topicsToSubscribe = registrations.Select(r => r.Topic).Distinct(StringComparer.Ordinal).ToList();

        if (topicsToSubscribe.Count == 0)
        {
            return;
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = groupId,
            ClientId = $"{_options.ClientId}-{groupId}",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            SessionTimeoutMs = _options.SessionTimeoutMs,
            MaxPollIntervalMs = _options.MaxPollIntervalMs,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        };

        // Quản lý partition lifecycle và cancellation khi bị revoke / rebalance
        var activePartitions = new ConcurrentDictionary<TopicPartition, CancellationTokenSource>();
        var activeProcessingTasks = new ConcurrentDictionary<TopicPartition, Task>();

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetPartitionsAssignedHandler((c, partitions) =>
            {
                foreach (var p in partitions)
                {
                    activePartitions[p] = new CancellationTokenSource();
                }

                _logger?.LogInformation(
                    "Consumer group {GroupId} được gán {Count} partitions: [{Partitions}]",
                    groupId,
                    partitions.Count,
                    string.Join(", ", partitions.Select(p => $"{p.Topic}:{p.Partition.Value}")));
            })
            .SetPartitionsRevokedHandler((c, partitionOffsets) =>
            {
                foreach (var po in partitionOffsets)
                {
                    if (activePartitions.TryRemove(po.TopicPartition, out var cts))
                    {
                        try
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Lỗi khi hủy token của revoked partition {Partition}", po.TopicPartition);
                        }
                    }
                }

                _logger?.LogInformation(
                    "Consumer group {GroupId} bị thu hồi {Count} partitions. Đã hủy các tác vụ đang chạy trên các partitions này.",
                    groupId,
                    partitionOffsets.Count);
            })
            .SetPartitionsLostHandler((c, partitionOffsets) =>
            {
                foreach (var po in partitionOffsets)
                {
                    if (activePartitions.TryRemove(po.TopicPartition, out var cts))
                    {
                        try
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Lỗi khi hủy token của lost partition {Partition}", po.TopicPartition);
                        }
                    }
                }

                _logger?.LogWarning("Consumer group {GroupId} bị mất kết nối partitions (Partitions Lost).", groupId);
            })
            .Build();

        consumer.Subscribe(topicsToSubscribe);
        _logger?.LogInformation("Consumer group {GroupId} đã subscribe vào topics: [{Topics}]", groupId, string.Join(", ", topicsToSubscribe));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Polling không chặn lâu để duy trì heartbeat và kiểm tra cancellation
                    var consumeResult = consumer.Consume(TimeSpan.FromMilliseconds(200));
                    if (consumeResult is null || consumeResult.IsPartitionEOF)
                    {
                        await Task.Delay(10, stoppingToken);
                        continue;
                    }

                    var tp = consumeResult.TopicPartition;
                    if (!activePartitions.TryGetValue(tp, out var partitionCts) || partitionCts.IsCancellationRequested)
                    {
                        // Partition không còn thuộc quyền sở hữu của consumer này -> Bỏ qua
                        continue;
                    }

                    // TẠM DỪNG (PAUSE) partition đang xử lý: đảm bảo mỗi partition chỉ có một message đang xử lý tại một thời điểm
                    consumer.Pause([tp]);

                    // Xử lý message bất đồng bộ để vòng lặp consumer tiếp tục polling các partition khác
                    var processTask = ProcessMessageAsync(consumer, groupId, consumeResult, partitionCts.Token, stoppingToken);
                    activeProcessingTasks[tp] = processTask;

                    // Dọn dẹp các task đã hoàn tất
                    _ = processTask.ContinueWith(_ => { activeProcessingTasks.TryRemove(tp, out Task? _); }, TaskScheduler.Default);
                }
                catch (ConsumeException ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "Consumer group {GroupId} gặp lỗi khi consume: {Code} - {Reason}",
                        groupId,
                        ex.Error.Code,
                        ex.Error.Reason);

                    await Task.Delay(1000, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Lỗi ngoài dự kiến trong consumer loop của group {GroupId}", groupId);
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
        finally
        {
            try
            {
                // Chờ các tác vụ đang chạy kết thúc hoặc hủy
                foreach (var cts in activePartitions.Values)
                {
                    cts.Cancel();
                }

                await Task.WhenAll(activeProcessingTasks.Values.ToArray());
                consumer.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Lỗi khi đóng consumer group {GroupId}", groupId);
            }
        }
    }

    private async Task ProcessMessageAsync(
        IConsumer<string, string> consumer,
        string groupId,
        ConsumeResult<string, string> consumeResult,
        CancellationToken partitionToken,
        CancellationToken stoppingToken)
    {
        var tp = consumeResult.TopicPartition;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(partitionToken, stoppingToken);
        var ct = linkedCts.Token;

        try
        {
            var rawJson = consumeResult.Message.Value;

            // 1. Trích xuất metadata envelope
            EnvelopeMetadata metadata;
            try
            {
                metadata = KafkaEnvelopeSerializer.ExtractMetadata(rawJson);
            }
            catch (Exception ex)
            {
                // Wire JSON không hợp lệ -> Chuyển trực tiếp DLQ
                _logger?.LogWarning(ex, "Message tại {Topic}[{Partition}@{Offset}] có wire JSON không hợp lệ. Chuyển vào DLQ.",
                    consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value);

                await RouteToDlqViaOutboxAsync(consumeResult, "InvalidFormatHandler", 1, "MALFORMED_JSON", ex.Message, ct);

                if (!partitionToken.IsCancellationRequested)
                {
                    consumer.Commit(consumeResult);
                    consumer.Resume([tp]);
                }
                return;
            }

            // 2. Kiểm tra tính hợp lệ cơ bản của envelope
            if (!metadata.IsValid)
            {
                _logger?.LogWarning(
                    "Envelope tại {Topic}[{Partition}@{Offset}] thiếu thuộc tính bắt buộc (MessageId: {MessageId}). Chuyển vào DLQ.",
                    consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, metadata.MessageId);

                await RouteToDlqViaOutboxAsync(consumeResult, "ValidationHandler", 1, "INVALID_ENVELOPE_METADATA", "Envelope metadata is incomplete.", ct);

                if (!partitionToken.IsCancellationRequested)
                {
                    consumer.Commit(consumeResult);
                    consumer.Resume([tp]);
                }
                return;
            }

            // 3. Tìm HandlerRegistration tương ứng
            var registration = _registry.Find(groupId, consumeResult.Topic, metadata.MessageType, metadata.Version);
            if (registration is null)
            {
                _logger?.LogWarning(
                    "Không tìm thấy handler cho {Topic}::{MessageType}::v{Version}. Chuyển vào DLQ.",
                    consumeResult.Topic, metadata.MessageType, metadata.Version);

                await RouteToDlqViaOutboxAsync(consumeResult, "UnroutedHandler", 1, "HANDLER_NOT_FOUND", $"No handler registered in group {groupId} for {metadata.MessageType} v{metadata.Version}", ct);

                if (!partitionToken.IsCancellationRequested)
                {
                    consumer.Commit(consumeResult);
                    consumer.Resume([tp]);
                }
                return;
            }

            // 4. Thực thi handler trong Inbox execution boundary
            await using var scope = _scopeFactory.CreateAsyncScope();
            var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            Func<int, string, OutboxMessage> dlqFactory = (retryCount, errorCode) =>
            {
                var dlqTopic = KafkaTopics.GetDlqTopic(consumeResult.Topic);
                var dlqRecord = new DlqRecord
                {
                    RawMessage = rawJson,
                    SourceTopic = consumeResult.Topic,
                    SourcePartition = consumeResult.Partition.Value,
                    SourceOffset = consumeResult.Offset.Value,
                    Handler = registration.HandlerName,
                    Attempts = retryCount,
                    ErrorCode = errorCode,
                    ErrorMessage = "Retry budget exhausted or unrecoverable error.",
                    FailedAt = DateTimeOffset.UtcNow
                };

                var dlqPayloadJson = JsonSerializer.Serialize(dlqRecord, KafkaEnvelopeSerializer.DefaultOptions);
                var dlqKey = string.IsNullOrWhiteSpace(consumeResult.Message.Key) ? Guid.NewGuid().ToString("D") : consumeResult.Message.Key;

                return OutboxMessage.Create(
                    Guid.NewGuid(),
                    dlqTopic,
                    dlqKey,
                    dlqPayloadJson,
                    DateTimeOffset.UtcNow);
            };

            var executionResult = await inboxStore.ExecuteInTransactionAsync<bool>(
                registration.HandlerName,
                metadata.MessageId.ToString("D"),
                DateTimeOffset.UtcNow,
                async innerCt =>
                {
                    // Deserialize và Validate typed envelope đầy đủ (bao gồm kiểm tra Payload không được null)
                    object typedEnvelope;
                    try
                    {
                        typedEnvelope = KafkaEnvelopeSerializer.DeserializeAndValidate(rawJson, registration.PayloadType);
                    }
                    catch (Exception valEx)
                    {
                        return Dprental.InventoryService.Common.Error.Validation("INVALID_ENVELOPE", valEx.Message);
                    }

                    var envelopeType = typeof(MessageEnvelope<>).MakeGenericType(registration.PayloadType);
                    var handlerInstance = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
                    var handleMethod = registration.HandlerType.GetMethod("HandleAsync", [envelopeType, typeof(CancellationToken)]);
                    if (handleMethod is null)
                    {
                        return Dprental.InventoryService.Common.Error.Failure("HANDLER_METHOD_MISSING", "Handler không có phương thức HandleAsync.");
                    }

                    var taskResult = (Task<Result>)handleMethod.Invoke(handlerInstance, [typedEnvelope, innerCt])!;
                    var handleResult = await taskResult;
                    return handleResult.IsSuccess ? Result.Success(true) : handleResult.Error;
                },
                dlqFactory,
                ct);

            // 5. Đánh giá kết quả thực thi
            if (executionResult.Status is InboxProcessingStatus.Success or InboxProcessingStatus.AlreadyProcessed)
            {
                _logger?.LogInformation(
                    "Đã xử lý thông điệp {MessageId} ({MessageType}) thành công bởi {Handler}.",
                    metadata.MessageId, metadata.MessageType, registration.HandlerName);

                // Commit offset sau khi xử lý thành công
                if (!partitionToken.IsCancellationRequested)
                {
                    consumer.Commit(consumeResult);
                    consumer.Resume([tp]);
                }
            }
            else if (executionResult.Status is InboxProcessingStatus.DeadLettered)
            {
                _logger?.LogWarning(
                    "Thông điệp {MessageId} đã chuyển vào DLQ sau {Attempts} lần thử. Mã lỗi: {ErrorCode}.",
                    metadata.MessageId, executionResult.RetryCount, executionResult.Error?.Code);

                // Commit offset sau khi đã commit DLQ atomically vào database
                if (!partitionToken.IsCancellationRequested)
                {
                    consumer.Commit(consumeResult);
                    consumer.Resume([tp]);
                }
            }
            else if (executionResult.Status is InboxProcessingStatus.TransientFailure)
            {
                _logger?.LogWarning(
                    "Xử lý thất bại thông điệp {MessageId} (Lần thử {Attempts}/5). Mã lỗi: {ErrorCode}. Giữ partition tạm dừng để retry theo backoff.",
                    metadata.MessageId, executionResult.RetryCount, executionResult.Error?.Code);

                // Chờ khoảng thời gian backoff trước khi resume partition để poll lại và retry
                var delay = RetryBackoffCalculator.CalculateDelay(executionResult.RetryCount);
                await Task.Delay(delay, ct);

                if (!partitionToken.IsCancellationRequested)
                    // Seek lại đúng failed offset trước khi resume partition
                    consumer.Seek(consumeResult.TopicPartitionOffset);
                consumer.Resume([tp]);
            }
        }
        catch (OperationCanceledException) when (partitionToken.IsCancellationRequested)
        {
            _logger?.LogInformation("Tác vụ xử lý message tại {TopicPartition} bị hủy do partition rebalance / revoke. Không commit offset.", tp);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Lỗi không xử lý được khi xử lý message tại {TopicPartition}. Seek lại offset và thử lại.", tp);
            if (!partitionToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    consumer.Seek(consumeResult.TopicPartitionOffset);
                    consumer.Resume([tp]);
                }
                catch
                {
                    // Partition có thể đã bị revoke
                }
            }
        }
    }

    private async Task RouteToDlqViaOutboxAsync(
        ConsumeResult<string, string> consumeResult,
        string handler,
        int attempts,
        string errorCode,
        string? errorMessage,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        var dlqTopic = KafkaTopics.GetDlqTopic(consumeResult.Topic);
        var dlqRecord = new DlqRecord
        {
            RawMessage = consumeResult.Message.Value ?? string.Empty,
            SourceTopic = consumeResult.Topic,
            SourcePartition = consumeResult.Partition.Value,
            SourceOffset = consumeResult.Offset.Value,
            Handler = handler,
            Attempts = attempts,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            FailedAt = DateTimeOffset.UtcNow
        };

        var dlqPayloadJson = JsonSerializer.Serialize(dlqRecord, KafkaEnvelopeSerializer.DefaultOptions);
        var dlqKey = string.IsNullOrWhiteSpace(consumeResult.Message.Key) ? Guid.NewGuid().ToString("D") : consumeResult.Message.Key;

        var outboxMsg = OutboxMessage.Create(
            Guid.NewGuid(),
            dlqTopic,
            dlqKey,
            dlqPayloadJson,
            DateTimeOffset.UtcNow);

        outboxStore.Add(outboxMsg);
        await db.SaveChangesAsync(ct);
    }
}
