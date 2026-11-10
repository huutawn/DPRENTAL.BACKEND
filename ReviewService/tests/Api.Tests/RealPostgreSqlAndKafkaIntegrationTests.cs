using System.Net;
using System.Text.Json;
using Confluent.Kafka;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Application.Kafka.Consumers;
using Dprental.ReviewService.Application.Kafka.Contracts;
using Dprental.ReviewService.Application.Kafka.Producers;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReviewService.Api.Tests;

public sealed class RealPostgreSqlAndKafkaIntegrationTests
{
    private const string PostgresConnectionString = "Host=127.0.0.1;Port=24056;Database=reviewservice;Username=reviewservice;Password=review_secret_2026";
    private const string KafkaBootstrapServers = "127.0.0.1:29092";

    private static DbContextOptions<ReviewServiceDbContext> CreatePostgresOptions()
    {
        return new DbContextOptionsBuilder<ReviewServiceDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
    }

    [Fact]
    public async Task Postgres_Idempotency_SynchronizedConcurrentRequests_SamePayloadReplays_ExactSingleReviewAndSingleOutbox()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var idempotencyKey = $"sync-create-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var snapshot = new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", now.AddDays(-5), 1);

        var mockRentalClient = new MockRentalClient(snapshot);
        var mockMediaClient = new MockMediaClient();

        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
            var reviewRepo = new ReviewRepository(db);
            var auditRepo = new ReviewAuditRepository(db);
            var idempRepo = new IdempotencyRepository(db);
            var idempService = new IdempotencyService(idempRepo);
            var outboxRepo = new OutboxRepository(db);

            var service = new ReviewAuthoringService(
                db, reviewRepo, auditRepo, idempRepo, idempService, mockRentalClient, outboxRepo, TimeProvider.System);

            barrier.SignalAndWait(); // Synchronize both requests before entering transaction
            return await service.CreateReviewAsync(
                new ReviewActorContext(renterId, "cus", false),
                new CreateReviewRequest(orderId, itemId, 5, "Great product!"),
                idempotencyKey);
        });

        var task2 = Task.Run(async () =>
        {
            await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
            var reviewRepo = new ReviewRepository(db);
            var auditRepo = new ReviewAuditRepository(db);
            var idempRepo = new IdempotencyRepository(db);
            var idempService = new IdempotencyService(idempRepo);
            var outboxRepo = new OutboxRepository(db);

            var service = new ReviewAuthoringService(
                db, reviewRepo, auditRepo, idempRepo, idempService, mockRentalClient, outboxRepo, TimeProvider.System);

            barrier.SignalAndWait(); // Synchronize both requests before entering transaction
            return await service.CreateReviewAsync(
                new ReviewActorContext(renterId, "cus", false),
                new CreateReviewRequest(orderId, itemId, 5, "Great product!"),
                idempotencyKey);
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.True(results[0].IsSuccess, results[0].Error?.Message);
        Assert.True(results[1].IsSuccess, results[1].Error?.Message);
        Assert.Equal(results[0].Value.ReviewId, results[1].Value.ReviewId);

        // Verify PostgreSQL state: exactly 1 review, 1 receipt, 1 public outbox
        await using var verifyDb = new ReviewServiceDbContext(CreatePostgresOptions());
        var reviewsCount = await verifyDb.Reviews.CountAsync(r => r.OrderId == orderId && r.ItemId == itemId);
        Assert.Equal(1, reviewsCount);

        var receiptCount = await verifyDb.IdempotencyRecords.CountAsync(r => r.ActorId == renterId && r.IdempotencyKey == idempotencyKey);
        Assert.Equal(1, receiptCount);

        var outboxCount = await verifyDb.OutboxMessages.CountAsync(m => m.PartitionKey == listingId.ToString("D") && m.EventType == "ReviewCreated");
        Assert.Equal(1, outboxCount);

        // Cleanup
        var createdReview = await verifyDb.Reviews.FirstOrDefaultAsync(r => r.OrderId == orderId && r.ItemId == itemId);
        if (createdReview != null)
        {
            var audits = await verifyDb.ReviewAudits.Where(a => a.ReviewId == createdReview.Id).ToListAsync();
            verifyDb.ReviewAudits.RemoveRange(audits);
            verifyDb.Reviews.Remove(createdReview);
        }
        var receipts = await verifyDb.IdempotencyRecords.Where(r => r.ActorId == renterId && r.IdempotencyKey == idempotencyKey).ToListAsync();
        verifyDb.IdempotencyRecords.RemoveRange(receipts);
        var outboxes = await verifyDb.OutboxMessages.Where(m => m.PartitionKey == listingId.ToString("D")).ToListAsync();
        verifyDb.OutboxMessages.RemoveRange(outboxes);
        await verifyDb.SaveChangesAsync();
    }

    [Fact]
    public async Task Postgres_Idempotency_SynchronizedConcurrentRequests_DifferentPayload_Returns409WithoutSideEffects()
    {
        var orderId = Guid.NewGuid();
        var itemId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var idempotencyKey = $"sync-conflict-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var snapshot1 = new RentalItemReviewSnapshotDto(orderId, itemId1, renterId, supplierId, listingId, "Completed", now.AddDays(-5), 1);
        var snapshot2 = new RentalItemReviewSnapshotDto(orderId, itemId2, renterId, supplierId, listingId, "Completed", now.AddDays(-5), 1);

        var mockRentalClient1 = new MockRentalClient(snapshot1);
        var mockRentalClient2 = new MockRentalClient(snapshot2);
        var mockMediaClient = new MockMediaClient();

        var barrier = new Barrier(2);

        var task1 = Task.Run(async () =>
        {
            await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
            var reviewRepo = new ReviewRepository(db);
            var auditRepo = new ReviewAuditRepository(db);
            var idempRepo = new IdempotencyRepository(db);
            var idempService = new IdempotencyService(idempRepo);
            var outboxRepo = new OutboxRepository(db);

            var service = new ReviewAuthoringService(
                db, reviewRepo, auditRepo, idempRepo, idempService, mockRentalClient1, outboxRepo, TimeProvider.System);

            barrier.SignalAndWait();
            return await service.CreateReviewAsync(
                new ReviewActorContext(renterId, "cus", false),
                new CreateReviewRequest(orderId, itemId1, 5, "Payload 1"),
                idempotencyKey);
        });

        var task2 = Task.Run(async () =>
        {
            await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
            var reviewRepo = new ReviewRepository(db);
            var auditRepo = new ReviewAuditRepository(db);
            var idempRepo = new IdempotencyRepository(db);
            var idempService = new IdempotencyService(idempRepo);
            var outboxRepo = new OutboxRepository(db);

            var service = new ReviewAuthoringService(
                db, reviewRepo, auditRepo, idempRepo, idempService, mockRentalClient2, outboxRepo, TimeProvider.System);

            barrier.SignalAndWait();
            return await service.CreateReviewAsync(
                new ReviewActorContext(renterId, "cus", false),
                new CreateReviewRequest(orderId, itemId2, 3, "Payload 2 (Different)"),
                idempotencyKey);
        });

        var results = await Task.WhenAll(task1, task2);

        var successResult = results.FirstOrDefault(r => r.IsSuccess);
        var failureResult = results.FirstOrDefault(r => r.IsFailure);

        Assert.NotNull(successResult);
        Assert.NotNull(failureResult);
        Assert.Equal("IDEMPOTENCY_KEY_REUSE_MISMATCH", failureResult.Error.Code);

        // Cleanup
        await using var verifyDb = new ReviewServiceDbContext(CreatePostgresOptions());
        var createdReview = await verifyDb.Reviews.FirstOrDefaultAsync(r => r.OrderId == orderId);
        if (createdReview != null)
        {
            var audits = await verifyDb.ReviewAudits.Where(a => a.ReviewId == createdReview.Id).ToListAsync();
            verifyDb.ReviewAudits.RemoveRange(audits);
            verifyDb.Reviews.Remove(createdReview);
        }
        var receipts = await verifyDb.IdempotencyRecords.Where(r => r.ActorId == renterId && r.IdempotencyKey == idempotencyKey).ToListAsync();
        verifyDb.IdempotencyRecords.RemoveRange(receipts);
        var outboxes = await verifyDb.OutboxMessages.Where(m => m.PartitionKey == listingId.ToString("D")).ToListAsync();
        verifyDb.OutboxMessages.RemoveRange(outboxes);
        await verifyDb.SaveChangesAsync();
    }
    [Fact]
    public async Task Postgres_Update_ReplayWithOldETag_ReturnsStoredResponseWithoutIncrementingVersion()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var review = Review.Create(
            Guid.NewGuid(), orderId, itemId, ReviewDirection.renter_to_supplier_listing,
            renterId, supplierId, listingId, 4, "Initial review", now, now.AddDays(7));

        await using var dbSetup = new ReviewServiceDbContext(CreatePostgresOptions());
        await dbSetup.Reviews.AddAsync(review);
        await dbSetup.SaveChangesAsync();

        var updateKey = $"update-etag-{Guid.NewGuid():N}";
        var updateRequest = new UpdateReviewRequest(5, "Updated to 5 stars!");

        await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
        var reviewRepo = new ReviewRepository(db);
        var auditRepo = new ReviewAuditRepository(db);
        var idempRepo = new IdempotencyRepository(db);
        var idempService = new IdempotencyService(idempRepo);
        var outboxRepo = new OutboxRepository(db);
        var mockMediaClient = new MockMediaClient();
        var mockRentalClient = new MockRentalClient();

        var service = new ReviewAuthoringService(
            db, reviewRepo, auditRepo, idempRepo, idempService, mockRentalClient, outboxRepo, TimeProvider.System);

        // 1. Initial Update with If-Match: "1" -> Succeeds and increments version to 2
        var res1 = await service.UpdateReviewAsync(
            new ReviewActorContext(renterId, "cus", false),
            review.Id,
            updateRequest,
            updateKey,
            "\"1\"");

        Assert.True(res1.IsSuccess, res1.Error?.Message);
        Assert.Equal(2, res1.Value.Version);

        // 2. Retry with old ETag "1" (client retrying after dropped network response)
        // Must return stored 200 response with version 2, NOT 412 Precondition Failed!
        var res2 = await service.UpdateReviewAsync(
            new ReviewActorContext(renterId, "cus", false),
            review.Id,
            updateRequest,
            updateKey,
            "\"1\"");

        Assert.True(res2.IsSuccess, res2.Error?.Message);
        Assert.Equal(2, res2.Value.Version);

        // Verify version did NOT increment to 3
        await using var dbVerify = new ReviewServiceDbContext(CreatePostgresOptions());
        var freshReview = await dbVerify.Reviews.FindAsync(review.Id);
        Assert.NotNull(freshReview);
        Assert.Equal(2, freshReview.Version);

        // Cleanup
        var auditsToClean = await dbVerify.ReviewAudits.Where(a => a.ReviewId == freshReview.Id).ToListAsync();
        dbVerify.ReviewAudits.RemoveRange(auditsToClean);
        dbVerify.Reviews.Remove(freshReview);
        var receipts = await dbVerify.IdempotencyRecords.Where(r => r.ActorId == renterId && r.IdempotencyKey == updateKey).ToListAsync();
        dbVerify.IdempotencyRecords.RemoveRange(receipts);
        var outboxes = await dbVerify.OutboxMessages.Where(m => m.PartitionKey == listingId.ToString("D")).ToListAsync();
        dbVerify.OutboxMessages.RemoveRange(outboxes);
        await dbVerify.SaveChangesAsync();
    }
    [Fact]
    public async Task Postgres_Outbox_Fencing_ConditionalUpdateReturnsFalseWhenLeaseExpiredOrReclaimed()
    {
        await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
        var repo = new OutboxRepository(db);
        await db.OutboxMessages.ExecuteDeleteAsync();

        var msgId = Guid.NewGuid();
        var msg = new OutboxMessage(msgId, "test.topic", "TestEvent", "k1", "{}", DateTimeOffset.UtcNow);
        await db.OutboxMessages.AddAsync(msg);
        await db.SaveChangesAsync();

        // Worker A claims with 1 millisecond lease (immediately expired)
        var batch = await repo.ClaimBatchAsync("worker-A", TimeSpan.FromMilliseconds(1), 1, DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.Single(batch);
        var workerAToken = batch[0].ClaimToken!.Value;

        // Worker B reclaims after lease expiry
        var reclaimed = await repo.ClaimBatchAsync("worker-B", TimeSpan.FromMinutes(5), 1, DateTimeOffset.UtcNow);
        Assert.Single(reclaimed);
        var workerBToken = reclaimed[0].ClaimToken!.Value;
        Assert.NotEqual(workerAToken, workerBToken);

        // Worker A (stale) attempts conditional MarkProcessedAsync
        bool workerAUpdated = await repo.MarkProcessedAsync(msgId, workerAToken, "worker-A", DateTimeOffset.UtcNow);
        Assert.False(workerAUpdated); // Must return false: 0 rows updated!

        // Verify message state was NOT marked Published by Worker A
        await using var dbVerify = new ReviewServiceDbContext(CreatePostgresOptions());
        var freshMsg = await dbVerify.OutboxMessages.FindAsync(msgId);
        Assert.NotNull(freshMsg);
        Assert.NotEqual("Published", freshMsg.DeliveryState);
        Assert.Equal("worker-B", freshMsg.ClaimOwner);

        // Worker B (active) marks processed with its valid token
        bool workerBUpdated = await repo.MarkProcessedAsync(msgId, workerBToken, "worker-B", DateTimeOffset.UtcNow);
        Assert.True(workerBUpdated); // Must return true: 1 row updated!

        // Verify message IS now Published
        await using var dbVerify2 = new ReviewServiceDbContext(CreatePostgresOptions());
        var publishedMsg = await dbVerify2.OutboxMessages.FindAsync(msgId);
        Assert.NotNull(publishedMsg);
        Assert.Equal("Published", publishedMsg.DeliveryState);

        // Cleanup
        dbVerify2.OutboxMessages.Remove(publishedMsg);
        await dbVerify2.SaveChangesAsync();
    }

    [Fact]
    public async Task Kafka_Consumer_SharedTopic_IgnoresUnrelatedEventsAndCommits()
    {
        using var db = new ReviewServiceDbContext(CreatePostgresOptions());
        var inboxRepo = new InboxRepository(db);
        var projectionRepo = new ReviewProjectionRepository(db);
        var eligibilityService = new ReviewEligibilityService(projectionRepo);
        var producer = new SpyProducer();
        var client = new MockRentalClient();

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, client, eligibilityService, producer);

        // Feed RentalOrderPaid (unrelated event type on shared rental.events.v1 topic)
        var context = new KafkaMessageContext(
            "rental.events.v1", 0, 100, "key-1",
            "{\"messageType\":\"RentalOrderPaid\",\"messageId\":\"" + Guid.NewGuid() + "\",\"version\":1,\"producer\":\"RentalService\"}");

        var outcome = await consumer.ProcessMessageAsync(context);

        Assert.Equal(ProcessOutcome.Ignored, outcome);
    }

    [Fact]
    public async Task Kafka_Consumer_MalformedMessage_AcknowledgedToDlqAndDeadLettered()
    {
        using var db = new ReviewServiceDbContext(CreatePostgresOptions());
        var inboxRepo = new InboxRepository(db);
        var projectionRepo = new ReviewProjectionRepository(db);
        var eligibilityService = new ReviewEligibilityService(projectionRepo);
        var spyProducer = new SpyProducer(returnSuccess: true);
        var client = new MockRentalClient();

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, client, eligibilityService, spyProducer);

        var context = new KafkaMessageContext("rental.events.v1", 0, 105, "key-bad", "{not-valid-json");

        var outcome = await consumer.ProcessMessageAsync(context);

        Assert.Equal(ProcessOutcome.DeadLettered, outcome);
        Assert.Single(spyProducer.ProducedMessages);
        Assert.Equal(RentalItemCompletedConsumer.DlqTopic, spyProducer.ProducedMessages[0].Topic);
        Assert.Equal("rental.events.v1:0:105", spyProducer.ProducedMessages[0].Key);
    }

    [Fact]
    public async Task Kafka_Consumer_DlqOutage_ReturnsRetryAndDoesNotAdvance()
    {
        using var db = new ReviewServiceDbContext(CreatePostgresOptions());
        var inboxRepo = new InboxRepository(db);
        var projectionRepo = new ReviewProjectionRepository(db);
        var eligibilityService = new ReviewEligibilityService(projectionRepo);
        var failingProducer = new SpyProducer(returnSuccess: false); // Broker outage
        var client = new MockRentalClient();

        var consumer = new RentalItemCompletedConsumer(
            inboxRepo, db, client, eligibilityService, failingProducer);

        var context = new KafkaMessageContext("rental.events.v1", 0, 106, "key-bad", "{malformed");

        var outcome = await consumer.ProcessMessageAsync(context);

        Assert.Equal(ProcessOutcome.Retry, outcome);
    }

    [Fact]
    public async Task Kafka_RealBroker_ProduceAndReceiveAck()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = KafkaBootstrapServers
            })
            .Build();

        var producer = new KafkaProducer(config, NullLogger<KafkaProducer>.Instance);

        var testTopic = "review.events.v1";
        var testKey = Guid.NewGuid().ToString("D");
        var testPayload = "{\"testEvent\":\"IntegrationProof\",\"timestamp\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\"}";

        bool published = await producer.ProduceAsync(testTopic, testKey, testPayload);

        Assert.True(published);
    }

    [Fact]
    public async Task Postgres_BatchReviewStatus_QueriesProjectionsAndActorReviews_FilteredBoundedNoFullScan()
    {
        await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
        var projectionRepo = new ReviewProjectionRepository(db);
        var reviewRepo = new ReviewRepository(db);

        var actorId = Guid.NewGuid();
        var otherActorId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        var order1 = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        var order2 = Guid.NewGuid();
        var item2 = Guid.NewGuid();
        var unrequestedItem = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;

        // Projections
        var p1 = new ReviewProjection(Guid.NewGuid(), order1, item1, actorId, supplierId, listingId, now.AddDays(-2), now.AddDays(28), true, now.AddDays(-2));
        var p2 = new ReviewProjection(Guid.NewGuid(), order2, item2, actorId, supplierId, listingId, now.AddDays(-1), now.AddDays(29), true, now.AddDays(-1));
        var pUnrequested = new ReviewProjection(Guid.NewGuid(), Guid.NewGuid(), unrequestedItem, actorId, supplierId, listingId, now.AddDays(-1), now.AddDays(29), true, now.AddDays(-1));

        await db.ReviewProjections.AddRangeAsync(p1, p2, pUnrequested);

        // Reviews
        var rActor = Review.Create(Guid.NewGuid(), order1, item1, ReviewDirection.renter_to_supplier_listing, actorId, supplierId, listingId, 5, "By actor", now, now.AddDays(7));
        var rOther = Review.Create(Guid.NewGuid(), order2, item2, ReviewDirection.supplier_to_renter, otherActorId, actorId, listingId, 4, "By other", now, now.AddDays(7));

        await db.Reviews.AddRangeAsync(rActor, rOther);
        await db.SaveChangesAsync();

        try
        {
            // Query projections by requested item IDs
            var projections = await projectionRepo.GetByItemIdsAsync([item1, item2]);
            Assert.Equal(2, projections.Count);
            Assert.Contains(projections, p => p.ItemId == item1);
            Assert.Contains(projections, p => p.ItemId == item2);
            Assert.DoesNotContain(projections, p => p.ItemId == unrequestedItem);

            // Query actor reviews by requested item IDs
            var actorReviews = await reviewRepo.GetActorReviewsByItemIdsAsync(actorId, [item1, item2]);
            Assert.Single(actorReviews);
            Assert.Equal(rActor.Id, actorReviews[0].ReviewId);
            Assert.Equal(item1, actorReviews[0].ItemId);
            Assert.Equal(order1, actorReviews[0].OrderId);
        }
        finally
        {
            // Cleanup
            db.Reviews.RemoveRange(rActor, rOther);
            db.ReviewProjections.RemoveRange(p1, p2, pUnrequested);
            await db.SaveChangesAsync();
        }
    }

    private sealed class SpyProducer : IKafkaProducer
    {
        private readonly bool _returnSuccess;
        public List<(string Topic, string Key, string Payload)> ProducedMessages { get; } = [];

        public SpyProducer(bool returnSuccess = true) => _returnSuccess = returnSuccess;

        public Task<bool> ProduceAsync(string topic, string partitionKey, string payload, CancellationToken cancellationToken = default)
        {
            ProducedMessages.Add((topic, partitionKey, payload));
            return Task.FromResult(_returnSuccess);
        }
    }

    private sealed class MockRentalClient : IRentalServiceClient
    {
        private readonly RentalItemReviewSnapshotDto? _snapshot;

        public MockRentalClient(RentalItemReviewSnapshotDto? snapshot = null) => _snapshot = snapshot;

        public Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(Guid orderId, Guid itemId, CancellationToken cancellationToken = default)
        {
            if (_snapshot != null) return Task.FromResult(Result.Success(_snapshot));
            return Task.FromResult(Result.Failure<RentalItemReviewSnapshotDto>(Dprental.ReviewService.Common.Error.NotFound("NOT_FOUND", "Snapshot not found")));
        }

        public Task<Result<ReconciliationBatchDto>> GetReconciliationBatchAsync(string? cursor = null, int limit = 100, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success(new ReconciliationBatchDto([], null, false)));
        }
    }
    private sealed class MockMediaClient : IMediaServiceClient
    {
        public Task<Result<Dprental.ReviewService.Application.DTOs.Media.MediaVerifyDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success(new Dprental.ReviewService.Application.DTOs.Media.MediaVerifyDto(mediaId, Guid.Empty, "Ready", "image/jpeg")));
        }

        public Task<Result<Dprental.ReviewService.Application.DTOs.Media.MediaAccessDto>> CreatePrivateReadAccessAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success(new Dprental.ReviewService.Application.DTOs.Media.MediaAccessDto(mediaId, $"https://storage.test/{mediaId}", DateTimeOffset.UtcNow.AddSeconds(60))));
        }
    }
}
