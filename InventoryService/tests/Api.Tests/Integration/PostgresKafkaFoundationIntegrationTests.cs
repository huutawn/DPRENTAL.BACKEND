using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class PostgresKafkaFoundationIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_fixture.ConnectionString);

        // Đăng ký Kafka options & registry
        services.Configure<KafkaOptions>(opt =>
        {
            opt.BootstrapServers = "127.0.0.1:29092";
            opt.ClientId = "inventory-integration-tests";
        });

        var registry = new KafkaHandlerRegistry();
        services.AddSingleton(registry);

        // Dummy producer for Outbox/Inbox store tests
        services.AddSingleton<IKafkaProducer, InMemoryTestKafkaProducer>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task InboxExecutionBoundary_RollsBackBusinessAndOutbox_OnFailure()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        var writeTx = scope.ServiceProvider.GetRequiredService<IInventoryWriteTransaction>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        var assetId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var asset = InventoryAsset.Create(assetId, ownerId, typeId, "Sony FX3", 1, "{}", now).Value;
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var messageId = Guid.NewGuid().ToString("D");
        var handlerName = "TestFailingHandler";
        var outboxId = Guid.NewGuid();

        // Thực thi handler bị lỗi bên trong Inbox boundary
        var result = await inboxStore.ExecuteInTransactionAsync<bool>(
            handlerName,
            messageId,
            now,
            async ct =>
            {
                // Gọi write transaction tham gia transaction của Inbox
                var writeResult = await writeTx.ExecuteAsync(assetId, (ctx, innerCt) =>
                {
                    var period = new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 5));
                    var alloc = AssetAllocation.CreateManual(Guid.NewGuid(), assetId, period, now, "OPER-FAIL").Value;
                    ctx.Allocations.Add(alloc);

                    ctx.StageOutbox(OutboxMessage.Create(
                        outboxId,
                        "inventory.allocation.events.v1",
                        $"{alloc.Id}",
                        "{}",
                        now));

                    return Task.FromResult(Result.Success(true));
                }, ct);

                Assert.True(writeResult.IsSuccess);

                // Giả lập lỗi nghiệp vụ sau khi đã thay đổi business & outbox
                return Error.Validation("BUSINESS_REJECTED", "Không đủ điều kiện tiếp tục.");
            });

        Assert.Equal(InboxProcessingStatus.TransientFailure, result.Status);
        Assert.Equal("BUSINESS_REJECTED", result.Error?.Code);

        // Kiểm tra cơ sở dữ liệu: không có Allocation, không có Outbox message nào được lưu
        await using var verifyDb = new InventoryServiceDbContext(
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options);

        Assert.Empty(await verifyDb.AssetAllocations.Where(a => a.AssetId == assetId).ToListAsync());
        Assert.Null(await verifyDb.OutboxMessages.FindAsync(outboxId));

        // Bản ghi Inbox được lưu với status Failed và retry_count = 1
        var inboxRecord = await verifyDb.InboxMessages.SingleOrDefaultAsync(m => m.Handler == handlerName && m.MessageId == messageId);
        Assert.NotNull(inboxRecord);
        Assert.Equal(InboxMessageStatus.Failed, inboxRecord.Status);
        Assert.Equal(1, inboxRecord.RetryCount);
        Assert.NotNull(inboxRecord.NextAttemptAt);
    }

    [Fact]
    public async Task InboxExecutionBoundary_ConcurrentDuplicates_OnlyOneSucceeds()
    {
        using var sp = BuildServiceProvider();

        var messageId = Guid.NewGuid().ToString("D");
        var handlerName = "ConcurrentDedupHandler";
        var executionCount = 0;

        Task<InboxExecutionResult<int>> RunTaskAsync() => Task.Run(async () =>
        {
            using var scope = sp.CreateScope();
            var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            return await inboxStore.ExecuteInTransactionAsync(
                handlerName,
                messageId,
                DateTimeOffset.UtcNow,
                async ct =>
                {
                    await Task.Delay(50, ct);
                    Interlocked.Increment(ref executionCount);
                    return Result.Success(100);
                });
        });

        var results = await Task.WhenAll(RunTaskAsync(), RunTaskAsync());

        var successCount = results.Count(r => r.Status == InboxProcessingStatus.Success);
        var skippedCount = results.Count(r => r.Status == InboxProcessingStatus.AlreadyProcessed);

        // Chỉ có đúng 1 lần thực thi nghiệp vụ thành công
        Assert.Equal(1, executionCount);
        Assert.Equal(1, successCount);
        Assert.Equal(1, skippedCount);
    }

    [Fact]
    public async Task Outbox_LeaseRecovery_And_StaleWorkerUpdatesRejected()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var msg = OutboxMessage.Create(messageId, "test.topic", "key1", "{}", now);
        outboxStore.Add(msg);
        await db.SaveChangesAsync();

        // Worker 1 claim với lease ngắn 1 giây
        var lease1 = Guid.NewGuid();
        var claimed1 = await outboxStore.ClaimPendingBatchAsync(10, lease1, TimeSpan.FromSeconds(1), now);
        Assert.Single(claimed1);

        // Giả lập lease 1 hết hạn (now + 2s), Worker 2 claim thành công với lease 2
        var lease2 = Guid.NewGuid();
        var claimed2 = await outboxStore.ClaimPendingBatchAsync(10, lease2, TimeSpan.FromSeconds(30), now.AddSeconds(2));
        Assert.Single(claimed2);

        // Worker 1 (đã mất lease) cố gắng publish -> Bị từ chối
        var worker1Update = await outboxStore.MarkAsPublishedAsync(messageId, lease1, DateTimeOffset.UtcNow);
        Assert.False(worker1Update);

        // Worker 2 (nắm giữ lease 2 hợp lệ) publish -> Thành công
        var worker2Update = await outboxStore.MarkAsPublishedAsync(messageId, lease2, DateTimeOffset.UtcNow);
        Assert.True(worker2Update);

        // Kiểm tra database: message đã được đánh dấu published
        var persisted = await outboxStore.GetByIdAsync(messageId);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.PublishedAt);
        Assert.Null(persisted.LockLeaseToken);
    }

    [Fact]
    public async Task InboxStore_RecordFailure_DoesNotOverwriteProcessedState()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();

        var handler = "ProtectProcessedHandler";
        var messageId = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;

        // 1. Thông điệp xử lý thành công lần đầu
        var firstResult = await inboxStore.ExecuteInTransactionAsync(
            handler,
            messageId,
            now,
            ct => Task.FromResult(Result.Success(true)));

        Assert.Equal(InboxProcessingStatus.Success, firstResult.Status);

        var recordAfterSuccess = await inboxStore.GetAsync(handler, messageId);
        Assert.NotNull(recordAfterSuccess);
        Assert.Equal(InboxMessageStatus.Processed, recordAfterSuccess.Status);
        Assert.NotNull(recordAfterSuccess.ProcessedAt);

        // 2. Tác vụ muộn / cạnh tranh thất bại gọi lại cùng receipt
        var lateResult = await inboxStore.ExecuteInTransactionAsync<bool>(
            handler,
            messageId,
            now,
            ct => Task.FromResult<Result<bool>>(Error.Failure("LATE_FAILURE", "Giả lập lỗi muộn")));

        // Phải nhận được AlreadyProcessed và KHÔNG được đảo Processed -> Failed
        Assert.Equal(InboxProcessingStatus.AlreadyProcessed, lateResult.Status);

        await using var verifyDb = new InventoryServiceDbContext(
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName)).Options);
        var recordAfterLate = await verifyDb.InboxMessages.AsNoTracking()
            .SingleAsync(m => m.Handler == handler && m.MessageId == messageId);

        Assert.Equal(InboxMessageStatus.Processed, recordAfterLate.Status);
        Assert.NotNull(recordAfterLate.ProcessedAt);
    }

    private sealed class InMemoryTestKafkaProducer : IKafkaProducer
    {
        public Task ProduceRawAsync(string topic, string kafkaKey, string payloadJson, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ProduceEnvelopeAsync<T>(string topic, MessageEnvelope<T> envelope, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
