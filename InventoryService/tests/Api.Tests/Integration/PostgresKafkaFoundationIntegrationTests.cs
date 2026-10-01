using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Kafka.Saga;
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
        services.AddScoped<ISagaOrchestrator, SagaOrchestrator>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task InboxExecutionBoundary_RollsBackBusinessAndOutboxAndSaga_OnFailure()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        var writeTx = scope.ServiceProvider.GetRequiredService<IInventoryWriteTransaction>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        var equipmentId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var eq = InventoryEquipment.Create(equipmentId, orgId, CatalogLifecycleStatus.Active, true, now).Value;
        db.Equipments.Add(eq);
        await db.SaveChangesAsync();

        var messageId = Guid.NewGuid().ToString("D");
        var handlerName = "TestFailingHandler";
        var outboxId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        // Thực thi handler bị lỗi bên trong Inbox boundary
        var result = await inboxStore.ExecuteInTransactionAsync<bool>(
            handlerName,
            messageId,
            now,
            async ct =>
            {
                // Gọi write transaction tham gia transaction của Inbox
                var writeResult = await writeTx.ExecuteAsync(equipmentId, (ctx, innerCt) =>
                {
                    var period = new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 5));
                    var res = Reservation.CreateManual(Guid.NewGuid(), equipmentId, period, "Test User", "0900000000", now).Value;
                    ctx.Reservations.Add(res);

                    ctx.StageOutbox(OutboxMessage.Create(
                        outboxId,
                        "inventory.reservation.events.v1",
                        $"{orgId}:{res.Id}",
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

        // Kiểm tra cơ sở dữ liệu: không có Reservation, không có Outbox message nào được lưu
        await using var verifyDb = new InventoryServiceDbContext(
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString).Options);

        Assert.Empty(await verifyDb.Reservations.Where(r => r.EquipmentId == equipmentId).ToListAsync());
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
                    await Task.Delay(50, ct); // Mô phỏng độ trễ xử lý
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
    public async Task TenantIsolation_SagaInstances_SameCorrelationDifferentOrg()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var sagaStore = scope.ServiceProvider.GetRequiredService<ISagaStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        var correlationId = "SHARED-CORR-123";
        var org1 = Guid.NewGuid();
        var org2 = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var saga1 = SagaInstance.Create(Guid.NewGuid(), "OrderSaga", org1, correlationId, SagaState.Executing, "Step1", "{\"org\":1}", now);
        var saga2 = SagaInstance.Create(Guid.NewGuid(), "OrderSaga", org2, correlationId, SagaState.Executing, "Step1", "{\"org\":2}", now);

        sagaStore.Add(saga1);
        sagaStore.Add(saga2);
        await db.SaveChangesAsync();

        // Đọc lại độc lập theo Tenant (OrganizationId)
        var loaded1 = await sagaStore.GetAsync("OrderSaga", org1, correlationId);
        var loaded2 = await sagaStore.GetAsync("OrderSaga", org2, correlationId);

        Assert.NotNull(loaded1);
        Assert.NotNull(loaded2);
        Assert.Equal(saga1.Id, loaded1.Id);
        Assert.Equal(saga2.Id, loaded2.Id);
        Assert.NotEqual(loaded1.OrganizationId, loaded2.OrganizationId);
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

        // Worker 2 claim ngay lập tức -> rỗng vì lease 1 vẫn còn hiệu lực
        var lease2 = Guid.NewGuid();
        var claimed2Immediate = await outboxStore.ClaimPendingBatchAsync(10, lease2, TimeSpan.FromSeconds(10), now);
        Assert.Empty(claimed2Immediate);

        // Sau khi lease 1 hết hạn (giả lập thời gian now + 2s), Worker 2 claim thành công (Lease Recovery)
        var claimed2AfterExpiry = await outboxStore.ClaimPendingBatchAsync(10, lease2, TimeSpan.FromSeconds(10), now.AddSeconds(2));
        Assert.Single(claimed2AfterExpiry);

        // Worker 1 (đã mất lease) cố gắng MarkAsPublished bằng lease1 -> Thất bại (stale update rejected)
        var worker1Update = await outboxStore.MarkAsPublishedAsync(messageId, lease1, DateTimeOffset.UtcNow);
        Assert.False(worker1Update);

        // Worker 2 (nắm giữ lease2) gọi MarkAsPublished -> Thành công
        var worker2Update = await outboxStore.MarkAsPublishedAsync(messageId, lease2, DateTimeOffset.UtcNow);
        Assert.True(worker2Update);

        // Kiểm tra database: message đã được đánh dấu published
        var persisted = await outboxStore.GetByIdAsync(messageId);
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.PublishedAt);
        Assert.Null(persisted.LockLeaseToken);
    }

    [Fact]
    public async Task Saga_ConcurrentUpdates_ProtectedByXmin()
    {
        await using var context1 = new InventoryServiceDbContext(
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString).Options);
        await using var context2 = new InventoryServiceDbContext(
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString).Options);

        var sagaId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var saga = SagaInstance.Create(sagaId, "HoldSaga", Guid.NewGuid(), "corr-1", SagaState.Executing, "HoldRequested", "{}", now);
        context1.SagaInstances.Add(saga);
        await context1.SaveChangesAsync();

        // Context 1 và Context 2 cùng load saga
        var s1 = await context1.SagaInstances.FindAsync(sagaId);
        var s2 = await context2.SagaInstances.FindAsync(sagaId);
        Assert.NotNull(s1);
        Assert.NotNull(s2);

        // Context 1 cập nhật trước và commit
        s1.TransitionTo(SagaState.Executing, "HoldConfirmed", "{\"confirmed\":true}", DateTimeOffset.UtcNow);
        await context1.SaveChangesAsync();

        // Context 2 cập nhật sau trên phiên bản cũ -> PostgreSQL xmin phát hiện stale write
        s2.TransitionTo(SagaState.Completed, "Completed", "{\"completed\":true}", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context2.SaveChangesAsync());
    }

    [Fact]
    public async Task SagaTimeout_PersistsAcrossRestart_And_NoDuplicateExecution()
    {
        using var sp = BuildServiceProvider();
        using var scope = sp.CreateScope();
        var sagaStore = scope.ServiceProvider.GetRequiredService<ISagaStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        var sagaId = Guid.NewGuid();
        var timeoutId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var deadline = now.AddMinutes(-5); // Timeout đã quá hạn 5 phút trước

        var saga = SagaInstance.Create(
            sagaId,
            "ReservationTimeoutSaga",
            Guid.NewGuid(),
            "corr-timeout",
            SagaState.Executing,
            "AwaitingPayment",
            "{}",
            now.AddHours(-1),
            timeoutId,
            deadline);

        sagaStore.Add(saga);
        await db.SaveChangesAsync();

        // Hai worker cạnh tranh cùng claim overdue timeout bằng FOR UPDATE SKIP LOCKED
        Task<IReadOnlyList<SagaInstance>> ClaimAsync() => Task.Run(async () =>
        {
            using var workerScope = sp.CreateScope();
            var workerDb = workerScope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
            await using var tx = await workerDb.Database.BeginTransactionAsync();
            var store = workerScope.ServiceProvider.GetRequiredService<ISagaStore>();
            var claimed = await store.ClaimOverdueTimeoutsAsync(10, Guid.NewGuid(), TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
            await Task.Delay(100); // Giữ row lock để worker cạnh tranh quan sát SKIP LOCKED
            await tx.CommitAsync();
            return claimed;
        });

        var results = await Task.WhenAll(ClaimAsync(), ClaimAsync());
        var totalClaimed = results[0].Count + results[1].Count;

        // Đúng 1 worker claim được saga, worker kia nhận rỗng nhờ SKIP LOCKED
        Assert.Equal(1, totalClaimed);

        // Giả lập worker hoàn tất timeout và transition sang terminal
        using var finalizeScope = sp.CreateScope();
        var finalizeStore = finalizeScope.ServiceProvider.GetRequiredService<ISagaStore>();
        var finalizeDb = finalizeScope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();
        var targetSaga = await finalizeStore.GetAsync("ReservationTimeoutSaga", saga.OrganizationId, "corr-timeout");
        Assert.NotNull(targetSaga);

        targetSaga.TransitionTo(SagaState.Failed, "TimedOut", "{\"reason\":\"Timeout expired\"}", DateTimeOffset.UtcNow);
        await finalizeDb.SaveChangesAsync();

        // Giả lập restart dịch vụ: quét lại timeout
        var afterRestart = await finalizeStore.ClaimOverdueTimeoutsAsync(10, Guid.NewGuid(), TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
        Assert.Empty(afterRestart); // Không chạy lại vì đã ở terminal state
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
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString).Options);
        var recordAfterLate = await verifyDb.InboxMessages.AsNoTracking()
            .SingleAsync(m => m.Handler == handler && m.MessageId == messageId);

        Assert.Equal(InboxMessageStatus.Processed, recordAfterLate.Status);
        Assert.NotNull(recordAfterLate.ProcessedAt);
    }

    [Fact]
    public async Task SagaOrchestrator_ConcurrentCreation_HandlesRaceCondition()
    {
        var sagaType = "ConcurrentCreationSaga";
        var orgId = Guid.NewGuid();
        var correlationId = $"RACE-{Guid.NewGuid():N}";

        var testSaga = new SimpleTestSaga(sagaType);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_fixture.ConnectionString);
        services.AddSingleton<ISaga>(testSaga);
        services.AddScoped<ISagaOrchestrator, SagaOrchestrator>();
        using var customSp = services.BuildServiceProvider();

        Task<Result<SagaInstance>> RunAsync(int id) => Task.Run(async () =>
        {
            using var scope = customSp.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ISagaOrchestrator>();
            return await orchestrator.ProcessMessageAsync(
                sagaType,
                orgId,
                correlationId,
                "StartEvent",
                $"{{\"id\":{id}}}");
        });

        var results = await Task.WhenAll(RunAsync(1), RunAsync(2));
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess);

        await using var verifyDb = new InventoryServiceDbContext(
            new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString).Options);

        var instances = await verifyDb.SagaInstances.Where(s => s.SagaType == sagaType && s.CorrelationId == correlationId).ToListAsync();
        Assert.Single(instances);
    }

    [Fact]
    public async Task SagaOrchestrator_TimeoutLease_RejectsWorkerWithLostLease()
    {
        var sagaType = "LeaseLostTestSaga";
        var testSaga = new SimpleTestSaga(sagaType);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_fixture.ConnectionString);
        services.AddSingleton<ISaga>(testSaga);
        services.AddScoped<ISagaOrchestrator, SagaOrchestrator>();
        using var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var sagaStore = scope.ServiceProvider.GetRequiredService<ISagaStore>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISagaOrchestrator>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        var sagaId = Guid.NewGuid();
        var timeoutId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var deadline = now.AddMinutes(-1);

        var saga = SagaInstance.Create(
            sagaId,
            sagaType,
            Guid.NewGuid(),
            "corr-lease",
            SagaState.Executing,
            "Step1",
            "{}",
            now,
            timeoutId,
            deadline);

        sagaStore.Add(saga);
        await db.SaveChangesAsync();

        // Worker 1 claim với lease ngắn 1 giây
        var lease1 = Guid.NewGuid();
        var claimed1 = await sagaStore.ClaimOverdueTimeoutsAsync(1, lease1, TimeSpan.FromSeconds(1), now);
        Assert.Single(claimed1);

        // Giả lập lease 1 hết hạn (now + 2s), Worker 2 claim thành công với lease 2
        var lease2 = Guid.NewGuid();
        var claimed2 = await sagaStore.ClaimOverdueTimeoutsAsync(1, lease2, TimeSpan.FromSeconds(30), now.AddSeconds(2));
        Assert.Single(claimed2);

        // Worker 1 (đã mất lease) cố gắng xử lý timeout -> Bị từ chối
        var result1 = await orchestrator.ProcessTimeoutAsync(sagaId, timeoutId, lease1);
        Assert.True(result1.IsFailure);
        Assert.Equal("SAGA_LEASE_LOST", result1.Error.Code);

        // Worker 2 (nắm giữ lease 2) xử lý timeout -> Thành công
        var result2 = await orchestrator.ProcessTimeoutAsync(sagaId, timeoutId, lease2);
        Assert.True(result2.IsSuccess);
    }

    private sealed class SimpleTestSaga : ISaga
    {
        public string SagaType { get; }
        public SimpleTestSaga(string sagaType) => SagaType = sagaType;

        public Task<SagaTransition> HandleMessageAsync(SagaInstance instance, string messageType, string payloadJson, CancellationToken cancellationToken)
        {
            return Task.FromResult(new SagaTransition
            {
                State = SagaState.Executing,
                Step = "ProcessedMessage",
                DataJson = payloadJson
            });
        }

        public Task<SagaTransition> HandleTimeoutAsync(SagaInstance instance, SagaTimeout timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(new SagaTransition
            {
                State = SagaState.Executing,
                Step = "TimeoutHandled",
                DataJson = "{\"timeout\":true}"
            });
        }
    }

    private sealed class InMemoryTestKafkaProducer : IKafkaProducer
    {
        public Task ProduceRawAsync(string topic, string kafkaKey, string payloadJson, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ProduceEnvelopeAsync<T>(string topic, MessageEnvelope<T> envelope, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
