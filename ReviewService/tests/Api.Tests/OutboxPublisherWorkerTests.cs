using Dprental.ReviewService.Application.Kafka.Producers;
using Dprental.ReviewService.Application.Kafka.Workers;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Kiểm thử cho OutboxPublisherWorker: Claim lease, Broker ACK, Backoff và Parked state.
/// </summary>
public sealed class OutboxPublisherWorkerTests
{
    private static (ReviewServiceDbContext Context, OutboxPublisherWorker Worker, FakeKafkaProducer Producer, TestTimeProvider TimeProvider) CreateWorker()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<ReviewServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var context = new ReviewServiceDbContext(options);

        var services = new ServiceCollection();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped(_ => context);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var producer = new FakeKafkaProducer();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

        var worker = new OutboxPublisherWorker(
            scopeFactory,
            producer,
            timeProvider,
            NullLogger<OutboxPublisherWorker>.Instance);

        return (context, worker, producer, timeProvider);
    }

    [Fact]
    public async Task ProcessBatch_ClaimsAndPublishesMessages_MarksProcessed()
    {
        var (context, worker, producer, timeProvider) = CreateWorker();

        var msg = new OutboxMessage(
            Guid.NewGuid(),
            "review.events.v1",
            "ReviewCreated",
            "key-1",
            "{\"test\":true}",
            timeProvider.GetUtcNow());

        await context.OutboxMessages.AddAsync(msg);
        await context.SaveChangesAsync();

        int processed = await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Single(producer.PublishedMessages);
        Assert.Equal("Published", msg.DeliveryState);
        Assert.NotNull(msg.ProcessedAt);
    }

    [Fact]
    public async Task ProcessBatch_WhenBrokerFails_IncrementsAttemptsAndSchedulesBackoff()
    {
        var (context, worker, producer, timeProvider) = CreateWorker();
        producer.ShouldSucceed = false;

        var msg = new OutboxMessage(
            Guid.NewGuid(),
            "review.events.v1",
            "ReviewCreated",
            "key-2",
            "{\"test\":true}",
            timeProvider.GetUtcNow());

        await context.OutboxMessages.AddAsync(msg);
        await context.SaveChangesAsync();

        int processed = await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal("Pending", msg.DeliveryState);
        Assert.Equal(1, msg.Attempts);
        Assert.NotNull(msg.NextAttemptAt);
        Assert.True(msg.NextAttemptAt > timeProvider.GetUtcNow());
    }

    [Fact]
    public async Task ProcessBatch_WhenAttemptsExceedLimit_TransitionsToParked()
    {
        var (context, worker, producer, timeProvider) = CreateWorker();
        producer.ShouldThrow = true;

        var msg = new OutboxMessage(
            Guid.NewGuid(),
            "review.events.v1",
            "ReviewCreated",
            "key-3",
            "{\"test\":true}",
            timeProvider.GetUtcNow());

        // Simulate 4 prior failures
        for (int i = 0; i < 4; i++)
        {
            msg.FailAttempt("PREVIOUS_ERROR", timeProvider.GetUtcNow());
        }

        await context.OutboxMessages.AddAsync(msg);
        await context.SaveChangesAsync();

        int processed = await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal("Parked", msg.DeliveryState);
        Assert.Equal(5, msg.Attempts);
    }

    private sealed class FakeKafkaProducer : IKafkaProducer
    {
        public bool ShouldSucceed { get; set; } = true;
        public bool ShouldThrow { get; set; }
        public List<(string Topic, string Key, string Payload)> PublishedMessages { get; } = new();

        public Task<bool> ProduceAsync(string topic, string partitionKey, string payload, CancellationToken cancellationToken = default)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated broker network connection failure.");
            }

            if (!ShouldSucceed)
            {
                return Task.FromResult(false);
            }

            PublishedMessages.Add((topic, partitionKey, payload));
            return Task.FromResult(true);
        }
    }
}
