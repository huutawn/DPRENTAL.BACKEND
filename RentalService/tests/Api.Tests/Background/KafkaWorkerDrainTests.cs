using System.Collections.Concurrent;
using Confluent.Kafka;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Background;

// Hàng đợi lớn hơn một batch phải được xử lý hết liên tục, không chờ thời gian nghỉ khi rảnh.
public sealed class KafkaWorkerDrainTests
{
    private const int MessageCount = 60;      // lớn hơn batch 50 bản tin
    private const int IdleDelaySeconds = 30;  // code cũ sẽ ngủ 30s giữa hai batch
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InboxWorker_BacklogLargerThanBatch_DrainsWithoutIdleDelay()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using (var db = CreateDbContext(dbName))
        {
            for (int i = 0; i < MessageCount; i++)
            {
                db.RentalInboxMessages.Add(new RentalInboxMessage(
                    Guid.NewGuid(), Guid.NewGuid(), "TestEvent", "payment-service", Guid.NewGuid().ToString("D"),
                    null, null, "{}", DateTimeOffset.UtcNow.AddSeconds(i - MessageCount)));
            }
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddRentalServiceDomainRepositories("Host=localhost;Database=test;Username=test");
        services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
        services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddScoped<IPaymentEventsDispatcher, MarkProcessedDispatcher>();
        var sp = services.BuildServiceProvider();

        var worker = new InboxProcessingWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new KafkaOptions { Enabled = true, InboxPollingIntervalSeconds = IdleDelaySeconds }),
            NullLogger<InboxProcessingWorker>.Instance);

        bool drained = await RunUntilAsync(worker, async () =>
        {
            await using var verifyDb = CreateDbContext(dbName);
            int processed = await verifyDb.RentalInboxMessages
                .CountAsync(m => m.Status == RentalInboxMessage.StatusProcessed);
            return processed == MessageCount;
        });

        Assert.True(drained, $"Inbox chưa xử lý hết {MessageCount} bản tin trong {DrainTimeout.TotalSeconds}s.");
    }

    [Fact]
    public async Task OutboxWorker_BacklogLargerThanBatch_DrainsWithoutIdleDelay()
    {
        var repository = new QueueOutboxRepository(MessageCount);
        var transport = new CountingProducerTransport();
        var services = new ServiceCollection();
        services.AddScoped<IOutboxRepository>(_ => repository);
        var sp = services.BuildServiceProvider();

        var worker = new OutboxPublisherWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new KafkaOptions { Enabled = true, OutboxPollingIntervalSeconds = IdleDelaySeconds }),
            NullLogger<OutboxPublisherWorker>.Instance,
            transport);

        bool drained = await RunUntilAsync(worker, () => Task.FromResult(transport.ProduceCallCount == MessageCount));

        Assert.True(drained, $"Outbox chưa gửi hết {MessageCount} bản tin trong {DrainTimeout.TotalSeconds}s.");
    }

    // Chạy worker như host thật và chờ điều kiện; luôn dừng worker kể cả khi điều kiện không đạt.
    private static async Task<bool> RunUntilAsync(BackgroundService worker, Func<Task<bool>> isDone)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow + DrainTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await isDone())
                {
                    return true;
                }
                await Task.Delay(100);
            }
            return await isDone();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static RentalServiceDbContext CreateDbContext(string dbName) =>
        new(new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(dbName).Options);

    private sealed class MarkProcessedDispatcher(RentalServiceDbContext db) : IPaymentEventsDispatcher
    {
        public Task<Result<KafkaIntakeDisposition>> ReceiveMessageAsync(
            string messageJson, string? topic = null, int? partition = null, long? offset = null,
            string? publicationProof = null, string? messageKey = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<Result> ProcessInboxMessageAsync(Guid inboxMessageId, CancellationToken cancellationToken = default)
        {
            var message = await db.RentalInboxMessages.SingleAsync(m => m.Id == inboxMessageId, cancellationToken);
            message.MarkProcessed(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
    }

    private sealed class QueueOutboxRepository(int count) : IOutboxRepository
    {
        private readonly ConcurrentQueue<RentalOutboxMessage> _pending = new(
            Enumerable.Range(0, count).Select(i => new RentalOutboxMessage(
                Guid.NewGuid(), "rental.events.v1", $"key-{i}", "TestEvent", "{}", DateTimeOffset.UtcNow)));

        public Task<RentalOutboxMessage?> ClaimNextPendingAsync(
            string claimToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            if (!_pending.TryDequeue(out var message))
            {
                return Task.FromResult<RentalOutboxMessage?>(null);
            }
            message.Claim(claimToken, DateTimeOffset.UtcNow + leaseDuration);
            return Task.FromResult<RentalOutboxMessage?>(message);
        }

        public Task<Result<OutboxEnqueueResult>> EnqueueAtomicAsync(
            RentalOutboxMessage message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AddAsync(RentalOutboxMessage message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> MarkPublishedAsync(
            Guid id, string claimToken, DateTimeOffset publishedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RecordFailureAsync(
            Guid id, string claimToken, string errorMessage, DateTimeOffset attemptedAt,
            DateTimeOffset? nextScheduledAt = null, string? targetStatus = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class CountingProducerTransport : IKafkaProducerTransport
    {
        private int _produceCallCount;

        public int ProduceCallCount => Volatile.Read(ref _produceCallCount);

        public Task<DeliveryResult<string, string>> ProduceAsync(
            string topic, string partitionKey, string payload, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _produceCallCount);
            return Task.FromResult(new DeliveryResult<string, string>
            {
                Topic = topic,
                Partition = 0,
                Offset = 0,
                Status = PersistenceStatus.Persisted
            });
        }

        public void Flush(TimeSpan timeout) { }
        public void Dispose() { }
    }
}