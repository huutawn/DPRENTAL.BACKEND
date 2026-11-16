using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search.Workers;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Kiểm thử toàn diện cơ chế PostgreSQL Notification, In-memory signal coalescing,
/// truy vấn GetNextWakeUpTime, và các kịch bản tối ưu CPU nền trên PostgreSQL thật.
/// </summary>
public sealed class QueueNotificationAndCpuOptimizationTests : IClassFixture<PostgresFixture>
{
    private readonly string _connectionString;

    public QueueNotificationAndCpuOptimizationTests(PostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_connectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    [Fact]
    public async Task PostgresTrigger_CommitEmitsNotification_RollbackDoesNotEmit()
    {
        await using var listenConn = new NpgsqlConnection(_connectionString);
        await listenConn.OpenAsync();

        var receivedPayloads = new List<string>();
        listenConn.Notification += (_, e) =>
        {
            if (e.Payload != null) receivedPayloads.Add(e.Payload);
        };

        await using (var cmd = new NpgsqlCommand($"LISTEN {PostgresQueueNotificationListener.ChannelName};", listenConn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // 1. Transaction bị Rollback -> Không được phát notification
        await using (var writeConn = new NpgsqlConnection(_connectionString))
        {
            await writeConn.OpenAsync();
            await using var tx = await writeConn.BeginTransactionAsync();

            await using var insertCmd = new NpgsqlCommand("""
                INSERT INTO inventory.outbox_messages (id, topic, kafka_key, payload, created_at, retry_count)
                VALUES (gen_random_uuid(), 'test.topic', 'key-rollback', '{}', now(), 0);
                """, writeConn, tx);
            await insertCmd.ExecuteNonQueryAsync();

            await tx.RollbackAsync();
        }

        using var ctsRollback = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            while (!ctsRollback.IsCancellationRequested)
            {
                await listenConn.WaitAsync(ctsRollback.Token);
            }
        }
        catch (OperationCanceledException) { }

        Assert.Empty(receivedPayloads);

        // 2. Transaction được Commit -> Bắt buộc phát notification
        await using (var writeConn = new NpgsqlConnection(_connectionString))
        {
            await writeConn.OpenAsync();
            await using var tx = await writeConn.BeginTransactionAsync();

            await using var insertCmd = new NpgsqlCommand("""
                INSERT INTO inventory.outbox_messages (id, topic, kafka_key, payload, created_at, retry_count)
                VALUES (gen_random_uuid(), 'test.topic', 'key-commit', '{}', now(), 0);
                """, writeConn, tx);
            await insertCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }

        using var ctsCommit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (receivedPayloads.Count == 0 && !ctsCommit.IsCancellationRequested)
        {
            await listenConn.WaitAsync(ctsCommit.Token);
        }

        Assert.Contains(PostgresQueueNotificationListener.PayloadOutbox, receivedPayloads);
    }

    [Fact]
    public async Task PostgresTrigger_QueueSpecificPayloads_And_LeaseRenewalSuppression()
    {
        await using var listenConn = new NpgsqlConnection(_connectionString);
        await listenConn.OpenAsync();

        var notifications = new List<string>();
        listenConn.Notification += (_, e) =>
        {
            if (e.Payload != null) notifications.Add(e.Payload);
        };

        await using (var cmd = new NpgsqlCommand($"LISTEN {PostgresQueueNotificationListener.ChannelName};", listenConn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = CreateDbContext();
        var outboxStore = new OutboxStore(db);
        var cacheStore = new CacheInvalidationStore(db);
        var searchStore = new SearchProjectionStore(db);

        // A. Outbox: INSERT phát tín hiệu; Claim và Renew không phát; Requeue phát tín hiệu
        var outboxId = Guid.NewGuid();
        var outboxMsg = OutboxMessage.Create(outboxId, "test.topic", "key-lease-test", "{}", DateTimeOffset.UtcNow, null);
        outboxStore.Add(outboxMsg);
        await outboxStore.SaveChangesAsync();

        // Chờ nhận notification của INSERT
        await WaitForNotificationAsync(listenConn, notifications, PostgresQueueNotificationListener.PayloadOutbox);
        notifications.Clear();

        // Claim batch (chỉ gán lease)
        var leaseToken = Guid.NewGuid();
        var claimed = await outboxStore.ClaimPendingBatchAsync(1, leaseToken, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
        Assert.NotEmpty(claimed);

        // Kiểm tra Claim không phát notification thừa
        await AssertNoNotificationAsync(listenConn, notifications);

        // Requeue -> phải phát notification outbox
        await outboxStore.RequeueAsync(outboxMsg.Id, DateTimeOffset.UtcNow);
        await WaitForNotificationAsync(listenConn, notifications, PostgresQueueNotificationListener.PayloadOutbox);
        notifications.Clear();

        // B. Cache Invalidation: INSERT phát tín hiệu; Claim không phát; Checkpoint fanout phát tín hiệu
        var jobId = Guid.NewGuid();
        var job = CacheInvalidationJob.Create(jobId, ["scope:1"], DateTimeOffset.UtcNow);
        db.CacheInvalidationJobs.Add(job);
        await db.SaveChangesAsync();

        await WaitForNotificationAsync(listenConn, notifications, PostgresQueueNotificationListener.PayloadCacheInvalidation);
        notifications.Clear();

        var claimedJobs = await cacheStore.ClaimEligibleJobsAsync(1, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
        Assert.NotEmpty(claimedJobs);
        await AssertNoNotificationAsync(listenConn, notifications);

        // Checkpoint fanout -> chuyển lại Pending -> phải phát tín hiệu
        await cacheStore.CheckpointFanoutAsync(jobId, claimedJobs[0].LockLeaseToken!.Value, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await WaitForNotificationAsync(listenConn, notifications, PostgresQueueNotificationListener.PayloadCacheInvalidation);
        notifications.Clear();

        // C. Search Generation thay đổi -> phát tín hiệu search_projection
        var gen = await searchStore.GetGenerationAsync();
        if (gen is null)
        {
            gen = SearchGeneration.CreateInitial("pub", "own_list", "own_asset", DateTimeOffset.UtcNow);
            await searchStore.SaveGenerationAsync(gen);
        }
        else
        {
            gen.MarkReady(DateTimeOffset.UtcNow);
            await searchStore.SaveGenerationAsync(gen);
        }
        await WaitForNotificationAsync(listenConn, notifications, PostgresQueueNotificationListener.PayloadSearchProjection);
    }

    [Fact]
    public async Task Inactivity_CoalescingSignals_And_PreWaitNotificationHandling()
    {
        var listener = new PostgresQueueNotificationListener(null);

        // 1. Tín hiệu đến trước khi worker chờ (Pre-wait signal)
        listener.TriggerWakeUp(QueueWorkKind.Outbox);

        // Worker gọi WaitForWorkAsync sau khi signal đã đến -> phải trả về true ngay lập tức
        var receivedImmediate = await listener.WaitForWorkAsync(QueueWorkKind.Outbox, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(receivedImmediate);

        // 2. Coalescing (Gộp tín hiệu trùng lặp)
        // Phát 20 tín hiệu liên tục cho CacheInvalidation
        for (int i = 0; i < 20; i++)
        {
            listener.TriggerWakeUp(QueueWorkKind.CacheInvalidation);
        }

        // Lần đầu consume signal đã gộp
        var firstWait = await listener.WaitForWorkAsync(QueueWorkKind.CacheInvalidation, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(firstWait);

        // Lần chờ tiếp theo phải timeout vì tín hiệu đã được gộp và tiêu thụ hết
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var secondWait = await listener.WaitForWorkAsync(QueueWorkKind.CacheInvalidation, TimeSpan.FromMilliseconds(100), cts.Token);
        Assert.False(secondWait);
    }

    [Fact]
    public async Task GetNextWakeUpTime_CalculatesCorrectly_And_IgnoresTerminalAndBlockedMessages()
    {
        await using var db = CreateDbContext();
        var store = new OutboxStore(db);
        var now = DateTimeOffset.UtcNow;

        // Dọn sạch bảng outbox trong database test
        await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory.outbox_messages;");

        // Trường hợp 1: Không có message nào -> trả về null
        var emptyWakeUp = await store.GetNextWakeUpTimeAsync(now);
        Assert.Null(emptyWakeUp);

        // Trường hợp 2: Message A (key "K1") có retry_count = 10 (terminal failed)
        // Message B (key "K1") có retry_count = 0 nhưng bị blocked bởi A
        var terminalMsg = OutboxMessage.Create(Guid.NewGuid(), "test.topic", "K1", "{}", now.AddMinutes(-1), null);
        db.OutboxMessages.Add(terminalMsg);
        await db.SaveChangesAsync();

        // Gán retry_count = 10 cho A (terminal)
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.outbox_messages
            SET retry_count = 10, next_attempt_at = {now.AddSeconds(5)}
            WHERE id = {terminalMsg.Id}
            """);

        var blockedMsg = OutboxMessage.Create(Guid.NewGuid(), "test.topic", "K1", "{}", now, null);
        db.OutboxMessages.Add(blockedMsg);
        await db.SaveChangesAsync();

        // Vì A đã terminal (retry >= 10) và B bị blocked cùng key, không có message nào khả dĩ -> trả về null!
        var terminalWakeUp = await store.GetNextWakeUpTimeAsync(now);
        Assert.Null(terminalWakeUp);

        // Trường hợp 3: Thêm message C (key "K2") với retry_count = 1, next_attempt_at = now + 15s
        var futureRetryMsg = OutboxMessage.Create(Guid.NewGuid(), "test.topic", "K2", "{}", now, null);
        db.OutboxMessages.Add(futureRetryMsg);
        await db.SaveChangesAsync();

        var expectedWakeUp = now.AddSeconds(15);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.outbox_messages
            SET retry_count = 1, next_attempt_at = {expectedWakeUp}
            WHERE id = {futureRetryMsg.Id}
            """);

        var nextWakeUp = await store.GetNextWakeUpTimeAsync(now);
        Assert.NotNull(nextWakeUp);

        // Sai lệch không quá 1 giây do làm tròn timestamp
        Assert.True(Math.Abs((nextWakeUp.Value - expectedWakeUp).TotalSeconds) < 2.0);
    }

    [Fact]
    public async Task CacheAndSearchStore_GetNextWakeUpTime_IgnoresCompletedAndTerminal()
    {
        await using var db = CreateDbContext();
        var cacheStore = new CacheInvalidationStore(db);
        var searchStore = new SearchProjectionStore(db);
        var now = DateTimeOffset.UtcNow;

        // 1. Cache Invalidation Store
        await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory.cache_invalidation_jobs;");

        // Job Completed và Failed -> phải trả về null
        var completedJob = CacheInvalidationJob.Create(Guid.NewGuid(), ["scope:done"], now.AddMinutes(-5));
        await db.CacheInvalidationJobs.AddAsync(completedJob);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.cache_invalidation_jobs SET status = 'Completed', processed_at = {now}
            WHERE id = {completedJob.Id}
            """);

        Assert.Null(await cacheStore.GetNextWakeUpTimeAsync(now));

        // Thêm Pending job trong tương lai
        var futureCacheJob = CacheInvalidationJob.Create(Guid.NewGuid(), ["scope:future"], now);
        await db.CacheInvalidationJobs.AddAsync(futureCacheJob);
        await db.SaveChangesAsync();
        var cacheFutureTime = now.AddSeconds(20);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE inventory.cache_invalidation_jobs SET next_attempt_at = {cacheFutureTime}
            WHERE id = {futureCacheJob.Id}
            """);

        var cacheWakeUp = await cacheStore.GetNextWakeUpTimeAsync(now);
        Assert.NotNull(cacheWakeUp);
        Assert.True(Math.Abs((cacheWakeUp.Value - cacheFutureTime).TotalSeconds) < 2.0);

        // 2. Search Projection Store
        await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory.search_projection_jobs;");

        // Job Failed terminal (retry_count = 10) -> phải trả về null
        var terminalSearchId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.search_projection_jobs (
                id, entity_type, entity_id, action, desired_revision, applied_revision,
                needs_asset_fanout, fanout_completed, fanout_requested_revision, fanout_applied_revision,
                submission_started, submission_uncertain, submission_complete,
                status, retry_count, pending_task_uids, created_at, updated_at
            ) VALUES (
                {terminalSearchId}, 'EquipmentListing', {Guid.NewGuid()}, 'Upsert', 1, 0,
                FALSE, TRUE, 0, 0, FALSE, FALSE, FALSE,
                'Failed', 10, ARRAY[]::integer[], {now}, {now}
            );
            """);

        Assert.Null(await searchStore.GetNextWakeUpTimeAsync(now));

        // Thêm job Pending với retry_count = 1 trong tương lai
        var futureSearchId = Guid.NewGuid();
        var searchFutureTime = now.AddSeconds(12);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.search_projection_jobs (
                id, entity_type, entity_id, action, desired_revision, applied_revision,
                needs_asset_fanout, fanout_completed, fanout_requested_revision, fanout_applied_revision,
                submission_started, submission_uncertain, submission_complete,
                status, retry_count, next_attempt_at, pending_task_uids, created_at, updated_at
            ) VALUES (
                {futureSearchId}, 'EquipmentListing', {Guid.NewGuid()}, 'Upsert', 1, 0,
                FALSE, TRUE, 0, 0, FALSE, FALSE, FALSE,
                'Pending', 1, {searchFutureTime}, ARRAY[]::integer[], {now}, {now}
            );
            """);

        var searchWakeUp = await searchStore.GetNextWakeUpTimeAsync(now);
        Assert.NotNull(searchWakeUp);
        Assert.True(Math.Abs((searchWakeUp.Value - searchFutureTime).TotalSeconds) < 2.0);
    }

    [Fact]
    public async Task Worker_ProcessBatch_HandlesSingleMessageImmediately_AndDrainsBacklog()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_connectionString);
        var sp = services.BuildServiceProvider();

        await using (var db = CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM inventory.outbox_messages;");
            // Insert 2 messages
            var msg1 = OutboxMessage.Create(Guid.NewGuid(), "test.topic", "K1", "{}", DateTimeOffset.UtcNow, null);
            var msg2 = OutboxMessage.Create(Guid.NewGuid(), "test.topic", "K2", "{}", DateTimeOffset.UtcNow, null);
            db.OutboxMessages.AddRange(msg1, msg2);
            await db.SaveChangesAsync();
        }

        var listener = new PostgresQueueNotificationListener(null);
        var worker = new OutboxPublisherWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new DummyProducer(),
            listener)
        {
            BatchSize = 1 // Giả lập batch nhỏ để kiểm tra drain
        };

        // Lần đầu: claim 1 message
        var count1 = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(1, count1);

        // Lần hai: claim tiếp message thứ 2 (drain backlog)
        var count2 = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(1, count2);

        // Lần ba: hết backlog
        var count3 = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(0, count3);
    }

    [Fact]
    public async Task Worker_GracefulShutdown_CancelsImmediately()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_connectionString);
        var sp = services.BuildServiceProvider();

        var listener = new PostgresQueueNotificationListener(null);
        var worker = new OutboxPublisherWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new DummyProducer(),
            listener);

        using var cts = new CancellationTokenSource();

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(100);

        // Shutdown bằng cancel token
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
        await workerTask;

        Assert.True(workerTask.IsCompleted);
    }

    private static async Task WaitForNotificationAsync(
        NpgsqlConnection conn,
        List<string> notifications,
        string expectedPayload)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!notifications.Contains(expectedPayload) && !cts.IsCancellationRequested)
        {
            try
            {
                await conn.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) { break; }
        }
        Assert.Contains(expectedPayload, notifications);
    }

    private static async Task AssertNoNotificationAsync(
        NpgsqlConnection conn,
        List<string> notifications)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await conn.WaitAsync(cts.Token);
            }
        }
        catch (OperationCanceledException) { }

        Assert.Empty(notifications);
    }

    private sealed class DummyProducer : IKafkaProducer
    {
        public Task ProduceRawAsync(string topic, string kafkaKey, string payloadJson, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ProduceEnvelopeAsync<T>(string topic, MessageEnvelope<T> envelope, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
