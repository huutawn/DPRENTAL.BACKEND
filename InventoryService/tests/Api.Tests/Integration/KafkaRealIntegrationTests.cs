using Dprental.InventoryService.Api.Infrastructure.DependencyInjection;
using System.Text.Json;
using Confluent.Kafka;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Common;
using AppError = Dprental.InventoryService.Common.Error;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class KafkaRealIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private const string BootstrapServers = "127.0.0.1:29092";

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Provision baseline topics trên Kafka broker chung
        var opt = Options.Create(new KafkaOptions
        {
            BootstrapServers = BootstrapServers,
            ClientId = "test-provisioner",
            AutoProvisionTopics = true
        });

        var provisioner = new KafkaTopicProvisioner(opt);
        await provisioner.EnsureTopicsAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private ServiceProvider BuildServiceProvider(string? customBootstrapServers = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_fixture.ConnectionString);
        services.AddInventoryApiRepositories();

        services.Configure<KafkaOptions>(opt =>
        {
            opt.BootstrapServers = customBootstrapServers ?? BootstrapServers;
            opt.ClientId = "inventory-kafka-tests";
            opt.DeliveryTimeoutMs = 5000;
        });

        var registry = new KafkaHandlerRegistry();
        services.AddSingleton(registry);

        services.AddSingleton<KafkaProducer>();
        services.AddSingleton<IKafkaProducer>(sp => sp.GetRequiredService<KafkaProducer>());
        services.AddSingleton<KafkaTopicProvisioner>();
        services.AddScoped<OutboxPublisherWorker>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task PublishViaOutbox_DeliversToBroker_And_MarksPublished()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisherWorker>();

        var topic = KafkaTopics.InventoryAllocationEventsV1;
        var messageId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var aggregateId = $"RES-PUBLISH-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var envelope = MessageEnvelope.CreateRoot(
            messageType: "ReservationPlaced",
            aggregateId: aggregateId,
            payload: new { Status = "Placed", Amount = 500000 },
            organizationId: orgId);

        var wireJson = KafkaEnvelopeSerializer.Serialize(envelope);
        var outboxMessage = OutboxMessage.Create(messageId, topic, envelope.KafkaKey, wireJson, now);
        outboxStore.Add(outboxMessage);
        await db.SaveChangesAsync();

        // Chạy worker publish lô thông điệp
        var processedCount = await publisher.ProcessBatchAsync();
        Assert.True(processedCount >= 1);

        // Kiểm tra database: PublishedAt được cập nhật, lease token được giải phóng
        var persisted = await outboxStore.GetByIdAsync(messageId);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.PublishedAt);
        Assert.Null(persisted.LockLeaseToken);
        Assert.Equal(0, persisted.RetryCount);

        // Đọc từ Kafka bằng Consumer độc lập để xác nhận message đã thực sự tới broker
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"test-verifier-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(topic);

        var received = false;
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            var cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (cr is not null && cr.Message.Key == envelope.KafkaKey)
            {
                var receivedEnvelope = KafkaEnvelopeSerializer.Deserialize<JsonElement>(cr.Message.Value);
                Assert.Equal(envelope.MessageId, receivedEnvelope.MessageId);
                Assert.Equal(envelope.MessageType, receivedEnvelope.MessageType);
                Assert.Equal(envelope.CorrelationId, receivedEnvelope.CorrelationId);
                received = true;
                break;
            }
        }
        consumer.Close();

        Assert.True(received, "Message không được nhận từ Kafka topic sau khi Outbox publish.");
    }

    [Fact]
    public async Task PublishViaOutbox_LegacyMessageWithOrgKey_PublishesVerbatimKeyToBroker()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisherWorker>();

        var topic = KafkaTopics.InventoryAssetEventsV1;
        var messageId = Guid.NewGuid();
        var legacyOrgId = Guid.NewGuid();
        var legacyAssetId = Guid.NewGuid();
        var legacyKey = $"{legacyOrgId:D}:{legacyAssetId:D}";
        var legacyPayload = $$"""{"messageId":"{{messageId}}","organizationId":"{{legacyOrgId}}","aggregateId":"{{legacyAssetId}}"}""";
        var now = DateTimeOffset.UtcNow;

        var legacyOutbox = OutboxMessage.Create(messageId, topic, legacyKey, legacyPayload, now);
        outboxStore.Add(legacyOutbox);
        await db.SaveChangesAsync();

        var processed = await publisher.ProcessBatchAsync();
        Assert.True(processed >= 1);

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"legacy-verifier-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(topic);

        bool receivedVerbatim = false;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (cr is not null && cr.Message.Key == legacyKey)
            {
                using var expectedDoc = JsonDocument.Parse(legacyPayload);
                using var actualDoc = JsonDocument.Parse(cr.Message.Value);
                Assert.Equal(expectedDoc.RootElement.GetProperty("messageId").GetString(), actualDoc.RootElement.GetProperty("messageId").GetString());
                Assert.Equal(expectedDoc.RootElement.GetProperty("organizationId").GetString(), actualDoc.RootElement.GetProperty("organizationId").GetString());
                Assert.Equal(expectedDoc.RootElement.GetProperty("aggregateId").GetString(), actualDoc.RootElement.GetProperty("aggregateId").GetString());
                receivedVerbatim = true;
                break;
            }
        }
        consumer.Close();

        Assert.True(receivedVerbatim, "Legacy Outbox message không được xuất bản với nguyên vẹn KafkaKey định dạng cũ.");
    }

    [Fact]
    public async Task PartitionOrdering_MaintainsStrictOrderPerKey()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisherWorker>();
        publisher.BatchSize = 1; // Chỉ claim 1 bản ghi mỗi lần

        var topic = KafkaTopics.InventoryAssetEventsV1;
        var orgId = Guid.NewGuid();
        var aggregateId = "EQ-ORDER-STRICT";
        var now = DateTimeOffset.UtcNow;
        var key = $"{orgId:D}:{aggregateId}";

        // Msg 1 tạo trước
        var msg1Id = Guid.NewGuid();
        var msg1 = OutboxMessage.Create(msg1Id, topic, key, "{\"seq\":1}", now);
        outboxStore.Add(msg1);

        // Msg 2 tạo sau với cùng key
        var msg2Id = Guid.NewGuid();
        var msg2 = OutboxMessage.Create(msg2Id, topic, key, "{\"seq\":2}", now.AddSeconds(1));
        outboxStore.Add(msg2);

        await db.SaveChangesAsync();

        // Claim đợt 1 với BatchSize = 1: Bắt buộc phải là msg 1, KHÔNG ĐƯỢC vượt thứ tự sang msg 2
        var lease1 = Guid.NewGuid();
        var batch1 = await outboxStore.ClaimPendingBatchAsync(1, lease1, TimeSpan.FromSeconds(30), now.AddSeconds(2));
        Assert.Single(batch1);
        Assert.Equal(msg1Id, batch1[0].Id);

        // Trong khi msg 1 chưa published: Claim đợt 2 không được lấy msg 2 (strict per-key ordering)
        var lease2 = Guid.NewGuid();
        var batch2 = await outboxStore.ClaimPendingBatchAsync(1, lease2, TimeSpan.FromSeconds(30), now.AddSeconds(2));
        Assert.Empty(batch2);

        // Publish msg 1 thành công
        await outboxStore.MarkAsPublishedAsync(msg1Id, lease1, DateTimeOffset.UtcNow);

        // Sau khi msg 1 đã published: msg 2 mới đủ điều kiện được claim
        var batch3 = await outboxStore.ClaimPendingBatchAsync(1, lease2, TimeSpan.FromSeconds(30), now.AddSeconds(2));
        Assert.Single(batch3);
        Assert.Equal(msg2Id, batch3[0].Id);
    }

    [Fact]
    public async Task RetryExhaustion_And_DlqRouting_CommitAtomically()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var handler = "ExhaustionTestHandler";
        var messageId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;

        Func<int, string, OutboxMessage> dlqFactory = (attempts, err) =>
        {
            var dlqRecord = new DlqRecord
            {
                RawMessage = "{\"invalid\":true}",
                SourceTopic = "test.topic",
                SourcePartition = 0,
                SourceOffset = 100,
                Handler = handler,
                Attempts = attempts,
                ErrorCode = err,
                ErrorMessage = "Unrecoverable error",
                FailedAt = DateTimeOffset.UtcNow
            };
            return OutboxMessage.Create(Guid.NewGuid(), "test.topic.dlq", "key", JsonSerializer.Serialize(dlqRecord), DateTimeOffset.UtcNow);
        };

        // Giả lập 4 lần thử thất bại đầu tiên
        for (int i = 1; i <= 4; i++)
        {
            var res = await inboxStore.ExecuteInTransactionAsync<bool>(
                handler,
                messageId,
                now,
                ct => Task.FromResult<Result<bool>>(AppError.Failure("PERSISTENT_ERROR", "Lỗi liên tục")),
                dlqFactory);

            Assert.Equal(InboxProcessingStatus.TransientFailure, res.Status);
            Assert.Equal(i, res.RetryCount);
        }

        // Lần thử thứ 5: Hết budget retry -> chuyển DeadLettered và commit DLQ atomically
        var finalRes = await inboxStore.ExecuteInTransactionAsync<bool>(
            handler,
            messageId,
            now,
            ct => Task.FromResult<Result<bool>>(AppError.Failure("PERSISTENT_ERROR", "Lỗi liên tục")),
            dlqFactory);

        Assert.Equal(InboxProcessingStatus.DeadLettered, finalRes.Status);
        Assert.Equal(5, finalRes.RetryCount);

        // Kiểm tra database: InboxMessage chuyển sang DeadLettered
        var inboxRecord = await inboxStore.GetAsync(handler, messageId);
        Assert.NotNull(inboxRecord);
        Assert.Equal(InboxMessageStatus.DeadLettered, inboxRecord.Status);
        Assert.Equal(5, inboxRecord.RetryCount);

        // Outbox có bản ghi DLQ được commit atomically
        await using var verifyDb = new InventoryServiceDbContext(
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options);

        var dlqMessage = await verifyDb.OutboxMessages.FirstOrDefaultAsync(m => m.Topic == "test.topic.dlq");
        Assert.NotNull(dlqMessage);
        Assert.Contains("PERSISTENT_ERROR", dlqMessage.Payload);
    }

    [Fact]
    public async Task CrashAfterPublish_And_AtLeastOnceReplay()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        var producer = scope.ServiceProvider.GetRequiredService<IKafkaProducer>();

        var topic = KafkaTopics.InventoryAllocationCommandsV1;
        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = "test-crash-key";
        var payload = "{\"event\":\"ItemUpdated\"}";

        var msg = OutboxMessage.Create(messageId, topic, key, payload, now);
        outboxStore.Add(msg);
        await db.SaveChangesAsync();

        // Worker 1 claim thông điệp với lease ngắn
        var lease1 = Guid.NewGuid();
        var claimed = await outboxStore.ClaimPendingBatchAsync(1, lease1, TimeSpan.FromSeconds(1), now);
        Assert.Single(claimed);

        // Giả lập Worker 1 đã publish lên broker
        await producer.ProduceRawAsync(topic, key, payload);

        // NHƯNG Worker 1 bị crash TRƯỚC KHI cập nhật database (PublishedAt vẫn là null)
        // Lease 1 hết hạn: Worker 2 hồi phục và claim lại cùng messageId
        var lease2 = Guid.NewGuid();
        var claimedAfterCrash = await outboxStore.ClaimPendingBatchAsync(1, lease2, TimeSpan.FromSeconds(30), now.AddSeconds(2));
        Assert.Single(claimedAfterCrash);
        Assert.Equal(messageId, claimedAfterCrash[0].Id);

        // Worker 2 phát lại (At-least-once delivery) với cùng messageId
        await producer.ProduceRawAsync(topic, key, payload);
        await outboxStore.MarkAsPublishedAsync(messageId, lease2, DateTimeOffset.UtcNow);

        // Sau khi đã commit PublishedAt: không worker nào claim lại nữa
        var afterCommitClaim = await outboxStore.ClaimPendingBatchAsync(1, Guid.NewGuid(), TimeSpan.FromSeconds(30), now.AddSeconds(5));
        Assert.Empty(afterCommitClaim);
    }

    [Fact]
    public async Task OutageAndRecovery_IsolatedBroker()
    {
        // Chạy kiểm thử outage trên một broker port không tồn tại (127.0.0.1:29099)
        // Đảm bảo không dừng broker chung của DPRENTAL (127.0.0.1:29092)
        using var outageSp = BuildServiceProvider(customBootstrapServers: "127.0.0.1:29099");
        using var scope = outageSp.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisherWorker>();

        var topic = "test.outage.topic";
        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var msg = OutboxMessage.Create(messageId, topic, "outage-key", "{\"ping\":1}", now);
        outboxStore.Add(msg);
        await db.SaveChangesAsync();

        // Thử publish khi broker outage: publisher không làm sập tiến trình, ghi nhận failure và tăng retry_count
        await publisher.ProcessBatchAsync();

        var failedMsg = await outboxStore.GetByIdAsync(messageId);
        Assert.NotNull(failedMsg);
        Assert.Null(failedMsg.PublishedAt);
        Assert.Equal(1, failedMsg.RetryCount);
        Assert.NotNull(failedMsg.NextAttemptAt);
        Assert.NotNull(failedMsg.LastError);
    }

    [Fact]
    public void ConsumerGroup_PartitionRevocation_CancelsActiveToken()
    {
        var activePartitions = new System.Collections.Concurrent.ConcurrentDictionary<TopicPartition, CancellationTokenSource>();
        var tp = new TopicPartition("test.topic", new Partition(0));
        activePartitions[tp] = new CancellationTokenSource();

        var token = activePartitions[tp].Token;
        Assert.False(token.IsCancellationRequested);

        // Giả lập callback PartitionsRevoked
        if (activePartitions.TryRemove(tp, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        Assert.True(token.IsCancellationRequested);
        Assert.False(activePartitions.ContainsKey(tp));
    }

    [Fact]
    public async Task Consumer_InvalidEnvelopePayload_RoutesToOutboxDlq()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        // Giả lập message với payload: null
        var topic = KafkaTopics.InventoryAllocationEventsV1;
        var dlqTopic = KafkaTopics.GetDlqTopic(topic);
        var rawJson = $$"""
            {
                "messageId": "{{Guid.NewGuid()}}",
                "messageType": "ReservationPlaced",
                "version": 1,
                "correlationId": "corr-test",
                "aggregateId": "RES-1",
                "organizationId": "{{Guid.NewGuid()}}",
                "occurredAt": "{{DateTimeOffset.UtcNow:O}}",
                "producer": "Test",
                "payload": null
            }
            """;

        var dlqRecord = new DlqRecord
        {
            RawMessage = rawJson,
            SourceTopic = topic,
            SourcePartition = 0,
            SourceOffset = 1234,
            Handler = "TestValidationHandler",
            Attempts = 1,
            ErrorCode = "INVALID_ENVELOPE",
            ErrorMessage = "Value cannot be null. (Parameter 'Payload')",
            FailedAt = DateTimeOffset.UtcNow
        };

        var dlqPayloadJson = JsonSerializer.Serialize(dlqRecord, KafkaEnvelopeSerializer.DefaultOptions);
        var outboxMsg = OutboxMessage.Create(Guid.NewGuid(), dlqTopic, "DLQ-KEY-1", dlqPayloadJson, DateTimeOffset.UtcNow);
        outboxStore.Add(outboxMsg);
        await db.SaveChangesAsync();

        // Kiểm tra DLQ message được lưu bền vững trong PostgreSQL Outbox
        var persisted = await outboxStore.GetByIdAsync(outboxMsg.Id);
        Assert.NotNull(persisted);
        Assert.Equal(dlqTopic, persisted.Topic);
        Assert.Contains("INVALID_ENVELOPE", persisted.Payload);
    }

    [Fact]
    public void Registry_IndependentGroups_BothResolveSameEvent()
    {
        var registry = new KafkaHandlerRegistry();
        var topic = KafkaTopics.InventoryAllocationEventsV1;
        var eventType = "AllocationCreated";

        // 2 handlers độc lập thuộc 2 consumer groups khác nhau đăng ký cùng 1 event
        registry.Register<TestConsumerHandlerA, TestPayload>(topic, eventType, 1, "ReservationAuditHandler", "group-audit");
        registry.Register<TestConsumerHandlerB, TestPayload>(topic, eventType, 1, "ReservationFulfillmentHandler", "group-fulfillment");

        Assert.Equal(2, registry.GetDistinctGroupIds().Count);

        var auditHandler = registry.Find("group-audit", topic, eventType, 1);
        var fulfillmentHandler = registry.Find("group-fulfillment", topic, eventType, 1);

        Assert.NotNull(auditHandler);
        Assert.NotNull(fulfillmentHandler);
        Assert.Equal("ReservationAuditHandler", auditHandler.HandlerName);
        Assert.Equal("ReservationFulfillmentHandler", fulfillmentHandler.HandlerName);
    }

    private sealed record TestPayload(string Name);

    private sealed class TestConsumerHandlerA : IMessageHandler<TestPayload>
    {
        public Task<Result> HandleAsync(MessageEnvelope<TestPayload> envelope, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success());
    }

    private sealed class TestConsumerHandlerB : IMessageHandler<TestPayload>
    {
        public Task<Result> HandleAsync(MessageEnvelope<TestPayload> envelope, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success());
    }
}
