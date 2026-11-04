using System.Net;
using Confluent.Kafka;
using Dprental.ReviewService.Application.Kafka.Producers;
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
    public async Task Postgres_AdvisoryLock_IdempotencyConcurrentRequests_SamePayloadReplays_DiffPayloadConflicts()
    {
        // Arrange
        var actorId = Guid.NewGuid();
        var idempotencyKey = $"test-key-{Guid.NewGuid():N}";
        var fingerprintA = "sha256-payload-A";
        var fingerprintB = "sha256-payload-B";
        var now = DateTimeOffset.UtcNow;

        var recordA1 = new IdempotencyRecord(
            Guid.NewGuid(),
            actorId,
            idempotencyKey,
            fingerprintA,
            "POST",
            "/api/v1/reviews",
            201,
            "{}",
            "{\"reviewId\":\"success\"}",
            now,
            now.AddMinutes(10));

        var recordA2 = new IdempotencyRecord(
            Guid.NewGuid(),
            actorId,
            idempotencyKey,
            fingerprintA,
            "POST",
            "/api/v1/reviews",
            201,
            "{}",
            "{\"reviewId\":\"success\"}",
            now,
            now.AddMinutes(10));

        var recordB = new IdempotencyRecord(
            Guid.NewGuid(),
            actorId,
            idempotencyKey,
            fingerprintB,
            "POST",
            "/api/v1/reviews",
            201,
            "{}",
            "{\"reviewId\":\"different\"}",
            now,
            now.AddMinutes(10));

        // Act 1: Two concurrent requests with SAME payload
        var task1 = Task.Run(async () =>
        {
            await using var db1 = new ReviewServiceDbContext(CreatePostgresOptions());
            var repo1 = new IdempotencyRepository(db1);
            return await repo1.SaveAsync(recordA1);
        });

        var task2 = Task.Run(async () =>
        {
            await using var db2 = new ReviewServiceDbContext(CreatePostgresOptions());
            var repo2 = new IdempotencyRepository(db2);
            return await repo2.SaveAsync(recordA2);
        });

        var results = await Task.WhenAll(task1, task2);

        // Assert 1: Both must succeed (one created, one replayed same fingerprint)
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess);
        Assert.Equal(fingerprintA, results[0].Value.RequestFingerprint);
        Assert.Equal(fingerprintA, results[1].Value.RequestFingerprint);

        // Act 2: Third request with DIFFERENT payload and same key
        await using var db3 = new ReviewServiceDbContext(CreatePostgresOptions());
        var repo3 = new IdempotencyRepository(db3);
        var resultB = await repo3.SaveAsync(recordB);

        // Assert 2: Must fail with 409 Conflict (IDEMPOTENCY_KEY_REUSE_MISMATCH)
        Assert.False(resultB.IsSuccess);
        Assert.Equal("IDEMPOTENCY_KEY_REUSE_MISMATCH", resultB.Error.Code);
    }

    [Fact]
    public async Task Postgres_Outbox_ConcurrentWorkers_ForUpdateSkipLocked_NoDuplicateClaims()
    {
        await using var dbSetup = new ReviewServiceDbContext(CreatePostgresOptions());
        var msgId1 = Guid.NewGuid();
        var msgId2 = Guid.NewGuid();

        var msg1 = new OutboxMessage(msgId1, "test.topic", "TestEvent", "k1", "{}", DateTimeOffset.UtcNow);
        var msg2 = new OutboxMessage(msgId2, "test.topic", "TestEvent", "k2", "{}", DateTimeOffset.UtcNow);

        await dbSetup.OutboxMessages.AddRangeAsync(msg1, msg2);
        await dbSetup.SaveChangesAsync();

        // 2 workers claim concurrently with batchSize = 1
        var worker1Task = Task.Run(async () =>
        {
            await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
            var repo = new OutboxRepository(db);
            return await repo.ClaimBatchAsync("worker-1", TimeSpan.FromMinutes(1), 1, DateTimeOffset.UtcNow);
        });

        var worker2Task = Task.Run(async () =>
        {
            await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
            var repo = new OutboxRepository(db);
            return await repo.ClaimBatchAsync("worker-2", TimeSpan.FromMinutes(1), 1, DateTimeOffset.UtcNow);
        });

        var claimedBatches = await Task.WhenAll(worker1Task, worker2Task);

        var batch1 = claimedBatches[0];
        var batch2 = claimedBatches[1];

        // Each worker claims at most 1, and no overlapping message IDs
        var allClaimedIds = batch1.Select(m => m.Id).Concat(batch2.Select(m => m.Id)).ToList();
        Assert.Equal(allClaimedIds.Distinct().Count(), allClaimedIds.Count);

        // Cleanup
        await using var dbCleanup = new ReviewServiceDbContext(CreatePostgresOptions());
        var msgsToDelete = await dbCleanup.OutboxMessages.Where(m => m.Id == msgId1 || m.Id == msgId2).ToListAsync();
        dbCleanup.OutboxMessages.RemoveRange(msgsToDelete);
        await dbCleanup.SaveChangesAsync();
    }

    [Fact]
    public async Task Postgres_Outbox_ExpiredLeaseWorker_CannotMarkProcessedAfterReclaim()
    {
        await using var db = new ReviewServiceDbContext(CreatePostgresOptions());
        var repo = new OutboxRepository(db);

        var msgId = Guid.NewGuid();
        var msg = new OutboxMessage(msgId, "test.topic", "TestEvent", "k1", "{}", DateTimeOffset.UtcNow);
        await db.OutboxMessages.AddAsync(msg);
        await db.SaveChangesAsync();

        // Worker 1 claims with very short lease (already expired)
        var batch = await repo.ClaimBatchAsync("worker-1", TimeSpan.FromMilliseconds(1), 1, DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.Single(batch);
        var worker1Token = batch[0].ClaimToken!.Value;

        // Worker 2 reclaims after lease expiry
        var reclaimed = await repo.ClaimBatchAsync("worker-2", TimeSpan.FromMinutes(5), 1, DateTimeOffset.UtcNow);
        Assert.Single(reclaimed);
        var worker2Token = reclaimed[0].ClaimToken!.Value;
        Assert.NotEqual(worker1Token, worker2Token);

        // Worker 1 (stale) attempts to mark processed with its old token
        await repo.MarkProcessedAsync(msgId, worker1Token, DateTimeOffset.UtcNow);

        // Verify message state was NOT marked Published by Worker 1
        await using var dbVerify = new ReviewServiceDbContext(CreatePostgresOptions());
        var freshMsg = await dbVerify.OutboxMessages.FindAsync(msgId);
        Assert.NotNull(freshMsg);
        Assert.NotEqual("Published", freshMsg.DeliveryState);
        Assert.Equal("worker-2", freshMsg.ClaimOwner);

        // Worker 2 (active) marks processed with its valid token
        await repo.MarkProcessedAsync(msgId, worker2Token, DateTimeOffset.UtcNow);

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
}
