using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.Kafka.Consumers;
using Dprental.ReviewService.Application.Kafka.Contracts;
using Dprental.ReviewService.Application.Kafka.Producers;
using Dprental.ReviewService.Application.Kafka.Workers;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class EligibilityConsumerAndSweepWorkerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private static ReviewServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ReviewServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString("N"))
            .Options;
        return new ReviewServiceDbContext(options);
    }

    private sealed class SpyKafkaProducer : IKafkaProducer
    {
        public List<(string Topic, string PartitionKey, string Payload)> Messages { get; } = [];

        public Task<bool> ProduceAsync(string topic, string partitionKey, string payload, CancellationToken cancellationToken = default)
        {
            Messages.Add((topic, partitionKey, payload));
            return Task.FromResult(true);
        }
    }

    private sealed class MockRentalClient : IRentalServiceClient
    {
        public Func<Guid, Guid, Task<Result<RentalItemReviewSnapshotDto>>>? SnapshotHandler { get; set; }
        public Func<string?, int, Task<Result<ReconciliationBatchDto>>>? BatchHandler { get; set; }

        public Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(Guid orderId, Guid itemId, CancellationToken cancellationToken = default)
        {
            if (SnapshotHandler != null) return SnapshotHandler(orderId, itemId);
            return Task.FromResult(Result.Failure<RentalItemReviewSnapshotDto>(Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Not found")));
        }

        public Task<Result<ReconciliationBatchDto>> GetReconciliationBatchAsync(string? cursor = null, int limit = 100, CancellationToken cancellationToken = default)
        {
            if (BatchHandler != null) return BatchHandler(cursor, limit);
            return Task.FromResult(Result.Success(new ReconciliationBatchDto([], null, false)));
        }
    }

    [Fact]
    public async Task ReviewProjectionRepository_UpsertAsync_NullCompletedAt_ThrowsArgumentException()
    {
        using var db = CreateDbContext();
        var repo = new ReviewProjectionRepository(db);

        var projection = new ReviewProjection(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, FixedNow.AddDays(30), true, FixedNow);

        // Verify fix: does NOT default to UtcNow; rejects invalid timestamp
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertAsync(projection));
        Assert.Contains("CompletedAt", ex.Message);
    }

    [Fact]
    public async Task ReviewProjectionRepository_UpsertAsync_NullDeadlineAt_ThrowsArgumentException()
    {
        using var db = CreateDbContext();
        var repo = new ReviewProjectionRepository(db);

        var projection = new ReviewProjection(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            FixedNow, null, true, FixedNow);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertAsync(projection));
        Assert.Contains("DeadlineAt", ex.Message);
    }

    [Fact]
    public async Task ReviewEligibilityService_UpsertEligibilityAsync_NewSnapshot_CreatesProjection()
    {
        using var db = CreateDbContext();
        var projectionRepo = new ReviewProjectionRepository(db);
        var timeProvider = new TestTimeProvider(FixedNow);
        var service = new ReviewEligibilityService(projectionRepo, timeProvider);

        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var completedAt = FixedNow.AddDays(-2);

        var snapshot = new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", completedAt, 1);

        var result = await service.UpsertEligibilityAsync(snapshot);
        Assert.True(result.IsSuccess);

        var saved = await projectionRepo.GetByItemIdAsync(itemId);
        Assert.NotNull(saved);
        Assert.Equal(orderId, saved.OrderId);
        Assert.Equal(itemId, saved.ItemId);
        Assert.Equal(renterId, saved.RenterId);
        Assert.Equal(supplierId, saved.SupplierId);
        Assert.Equal(listingId, saved.EquipmentListingId);
        Assert.Equal(completedAt, saved.CompletedAt);
        Assert.Equal(completedAt.AddDays(30), saved.DeadlineAt);
        Assert.True(saved.IsEligible);
    }

    [Fact]
    public async Task ReviewEligibilityService_UpsertEligibilityAsync_DoesNotOverwriteCompletedAtTimestamp()
    {
        using var db = CreateDbContext();
        var projectionRepo = new ReviewProjectionRepository(db);
        var timeProvider = new TestTimeProvider(FixedNow);
        var service = new ReviewEligibilityService(projectionRepo, timeProvider);

        var itemId = Guid.NewGuid();
        var initialCompletedAt = FixedNow.AddDays(-5);
        var conflictingCompletedAt = FixedNow.AddDays(-1);

        var snapshot1 = new RentalItemReviewSnapshotDto(
            Guid.NewGuid(), itemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Completed", initialCompletedAt, 1);
        await service.UpsertEligibilityAsync(snapshot1);

        // Later conflicting event with different timestamp must NOT overwrite locked completedAt
        var snapshot2 = new RentalItemReviewSnapshotDto(
            snapshot1.OrderId, itemId, snapshot1.RenterId, snapshot1.SupplierId, snapshot1.EquipmentListingId,
            "Completed", conflictingCompletedAt, 2);
        var result2 = await service.UpsertEligibilityAsync(snapshot2);
        Assert.True(result2.IsSuccess);

        var saved = await projectionRepo.GetByItemIdAsync(itemId);
        Assert.NotNull(saved);
        Assert.Equal(initialCompletedAt, saved.CompletedAt);
        Assert.NotEqual(conflictingCompletedAt, saved.CompletedAt);
    }

    [Fact]
    public async Task RentalItemCompletedConsumer_NewEvent_HydratesOutsideTransaction_AtomicallyPersists_CommitsOffset()
    {
        using var db = CreateDbContext();
        var inboxRepo = new InboxRepository(db);
        var projectionRepo = new ReviewProjectionRepository(db);
        var timeProvider = new TestTimeProvider(FixedNow);
        var eligibilityService = new ReviewEligibilityService(projectionRepo, timeProvider);
        var spyProducer = new SpyKafkaProducer();
        var mockClient = new MockRentalClient();

        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var completedAt = FixedNow.AddDays(-1);

        mockClient.SnapshotHandler = (oId, iId) =>
            Task.FromResult(Result.Success(new RentalItemReviewSnapshotDto(
                orderId, itemId, renterId, supplierId, listingId, "Completed", completedAt, 3)));

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, mockClient, eligibilityService, spyProducer, timeProvider);

        var messageId = Guid.NewGuid();
        var envelope = new KafkaMessageEnvelope<RentalItemCompletedPayload>(
            messageId,
            "RentalItemCompleted",
            1,
            "RentalService",
            orderId.ToString("D"),
            FixedNow,
            "corr-1",
            null,
            new RentalItemCompletedPayload(renterId, listingId, null, orderId, itemId, 3, completedAt));

        bool offsetCommitted = false;
        var result = await consumer.ConsumeAsync(envelope, () =>
        {
            offsetCommitted = true;
            return Task.CompletedTask;
        });

        Assert.True(result.IsSuccess);
        Assert.True(offsetCommitted, "Kafka offset must be committed after successful persistence.");

        // Verify inbox record persisted
        bool inInbox = await inboxRepo.ExistsAsync(messageId, RentalItemCompletedConsumer.ConsumerGroup);
        Assert.True(inInbox);

        // Verify projection persisted
        var projection = await projectionRepo.GetByItemIdAsync(itemId);
        Assert.NotNull(projection);
        Assert.Equal(supplierId, projection.SupplierId);
        Assert.True(projection.IsEligible);
    }

    [Fact]
    public async Task RentalItemCompletedConsumer_DuplicateEvent_CommitsOffsetImmediatelyWithoutReprocessing()
    {
        using var db = CreateDbContext();
        var inboxRepo = new InboxRepository(db);
        var projectionRepo = new ReviewProjectionRepository(db);
        var timeProvider = new TestTimeProvider(FixedNow);
        var eligibilityService = new ReviewEligibilityService(projectionRepo, timeProvider);
        var spyProducer = new SpyKafkaProducer();
        var mockClient = new MockRentalClient();

        var messageId = Guid.NewGuid();
        // Seed inbox record as already processed
        await inboxRepo.AddAsync(new InboxMessage(messageId, RentalItemCompletedConsumer.ConsumerGroup, "RentalItemCompleted", FixedNow, FixedNow));

        int clientCallCount = 0;
        mockClient.SnapshotHandler = (_, _) =>
        {
            clientCallCount++;
            return Task.FromResult(Result.Success(new RentalItemReviewSnapshotDto()));
        };

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, mockClient, eligibilityService, spyProducer, timeProvider);

        var envelope = new KafkaMessageEnvelope<RentalItemCompletedPayload>(
            messageId,
            "RentalItemCompleted",
            1,
            "RentalService",
            Guid.NewGuid().ToString("D"),
            FixedNow,
            null,
            null,
            new RentalItemCompletedPayload(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), 1, FixedNow));

        bool offsetCommitted = false;
        var result = await consumer.ConsumeAsync(envelope, () =>
        {
            offsetCommitted = true;
            return Task.CompletedTask;
        });

        Assert.True(result.IsSuccess);
        Assert.True(offsetCommitted, "Duplicate messages must commit offset to avoid re-delivery.");
        Assert.Equal(0, clientCallCount); // RentalClient should NOT have been called
    }

    [Fact]
    public async Task RentalItemCompletedConsumer_NotFoundSnapshot_RoutesToDlqAndCommitsOffset()
    {
        using var db = CreateDbContext();
        var inboxRepo = new InboxRepository(db);
        var projectionRepo = new ReviewProjectionRepository(db);
        var timeProvider = new TestTimeProvider(FixedNow);
        var eligibilityService = new ReviewEligibilityService(projectionRepo, timeProvider);
        var spyProducer = new SpyKafkaProducer();
        var mockClient = new MockRentalClient();

        mockClient.SnapshotHandler = (_, _) =>
            Task.FromResult(Result.Failure<RentalItemReviewSnapshotDto>(Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Item not found")));

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, mockClient, eligibilityService, spyProducer, timeProvider);

        var messageId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var envelope = new KafkaMessageEnvelope<RentalItemCompletedPayload>(
            messageId,
            "RentalItemCompleted",
            1,
            "RentalService",
            Guid.NewGuid().ToString("D"),
            FixedNow,
            null,
            null,
            new RentalItemCompletedPayload(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), itemId, 1, FixedNow));

        bool offsetCommitted = false;
        var result = await consumer.ConsumeAsync(envelope, () =>
        {
            offsetCommitted = true;
            return Task.CompletedTask;
        });

        Assert.True(result.IsFailure);
        Assert.True(offsetCommitted, "Poison/404 messages routed to DLQ must commit source offset.");
        Assert.Single(spyProducer.Messages);
        Assert.Equal(RentalItemCompletedConsumer.DlqTopic, spyProducer.Messages[0].Topic);
    }

    [Fact]
    public async Task RentalReconciliationSweepWorker_ResumesFromCrashUsingUncompletedCheckpoint()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString("N");
        services.AddDbContext<ReviewServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddReviewServiceRepositories();
        services.AddScoped<IReviewEligibilityService, ReviewEligibilityService>();

        var mockClient = new MockRentalClient();
        services.AddSingleton<IRentalServiceClient>(mockClient);
        var timeProvider = new TestTimeProvider(FixedNow);
        services.AddSingleton<TimeProvider>(timeProvider);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // Seed an uncompleted checkpoint as if previous run crashed midway
        var crashedSweepId = "sweep-crashed-001";
        var crashedLastItemId = Guid.NewGuid();
        var windowStart = FixedNow.AddDays(-30);

        using (var seedScope = scopeFactory.CreateScope())
        {
            var reconRepo = seedScope.ServiceProvider.GetRequiredService<IReconciliationRepository>();
            await reconRepo.AddAsync(new ReconciliationCheckpoint(
                crashedSweepId, windowStart, crashedLastItemId, false, FixedNow.AddMinutes(-10), FixedNow.AddMinutes(-5)));
        }

        string? receivedCursor = null;
        var newItemId = Guid.NewGuid();
        mockClient.BatchHandler = (cursor, limit) =>
        {
            receivedCursor = cursor;
            var item = new RentalItemReviewSnapshotDto(
                Guid.NewGuid(), newItemId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Completed", FixedNow.AddDays(-3), 1);
            return Task.FromResult(Result.Success(new ReconciliationBatchDto([item], null, false)));
        };

        var worker = new RentalReconciliationSweepWorker(scopeFactory, timeProvider);
        int processed = await worker.RunSweepCycleAsync();

        Assert.Equal(1, processed);
        Assert.NotNull(receivedCursor);
        string decodedCursor = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(receivedCursor));
        Assert.Contains(crashedLastItemId.ToString(), decodedCursor);
        // Verify checkpoint is now marked completed
        using (var checkScope = scopeFactory.CreateScope())
        {
            var reconRepo = checkScope.ServiceProvider.GetRequiredService<IReconciliationRepository>();
            var latest = await reconRepo.GetLatestAsync();
            Assert.NotNull(latest);
            Assert.Equal(crashedSweepId, latest.SweepId);
            Assert.True(latest.IsCompleted);
            Assert.Equal(newItemId, latest.LastItemId);

            var projRepo = checkScope.ServiceProvider.GetRequiredService<IReviewProjectionRepository>();
            var proj = await projRepo.GetByItemIdAsync(newItemId);
            Assert.NotNull(proj);
            Assert.True(proj.IsEligible);
        }
    }
}
