using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Events;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class KafkaOutboxAtomicEnqueuePostgreSqlTests
{
    private static async Task<bool> EnsurePostgreSqlAvailableAsync()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }
        return true;
    }

    [Fact]
    public async Task EnqueueAtomic_InitialAndReplay_ReturnsSuccess_WithIsReplayFlag()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            var repository = new OutboxRepository(db);
            Guid orderId = Guid.NewGuid();
            string opKey = "checkout:order-1";
            Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId("rental-service", "CreateHold", opKey);
            string payload = """{"rentalOrderId":"123","quantity":2}""";

            var message1 = new RentalOutboxMessage(
                messageId,
                KafkaContractSpec.Topics.InventoryCommands,
                orderId.ToString("D"),
                KafkaContractSpec.CommandTypes.CreateHold,
                payload,
                DateTimeOffset.UtcNow);

            // 1. Initial Enqueue -> IsReplay = false
            var res1 = await repository.EnqueueAtomicAsync(message1);
            Assert.True(res1.IsSuccess);
            Assert.False(res1.Value.IsReplay);
            Assert.Equal(messageId, res1.Value.Message.Id);

            // 2. Exact same payload Replay -> IsReplay = true
            var message2 = new RentalOutboxMessage(
                messageId,
                KafkaContractSpec.Topics.InventoryCommands,
                orderId.ToString("D"),
                KafkaContractSpec.CommandTypes.CreateHold,
                payload,
                DateTimeOffset.UtcNow);

            var res2 = await repository.EnqueueAtomicAsync(message2);
            Assert.True(res2.IsSuccess);
            Assert.True(res2.Value.IsReplay);
            Assert.Equal(messageId, res2.Value.Message.Id);

            // Exactly 1 row in database
            int count = await db.RentalOutboxMessages.CountAsync(m => m.Id == messageId);
            Assert.Equal(1, count);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task EnqueueAtomic_SameId_ConflictingPayload_ReturnsConflictError()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            var repository = new OutboxRepository(db);
            var writer = new KafkaOutboxWriter(repository);
            Guid orderId = Guid.NewGuid();
            string opKey = "checkout:order-2";
            Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId("rental-service", "CreateHold", opKey);

            var originalEnvelope = new KafkaMessageEnvelope<JsonElement>(
                messageId,
                KafkaContractSpec.CommandTypes.CreateHold,
                KafkaContractSpec.CurrentEnvelopeVersion,
                "rental-service",
                orderId.ToString("D"),
                DateTimeOffset.UtcNow,
                null,
                null,
                JsonDocument.Parse("""{"rentalOrderId":"123","quantity":2}""").RootElement);

            var res1 = await writer.EnqueueAsync(KafkaContractSpec.Topics.InventoryCommands, orderId.ToString("D"), originalEnvelope);
            Assert.True(res1.IsSuccess);

            // Conflicting payload
            var conflictingEnvelope = new KafkaMessageEnvelope<JsonElement>(
                messageId,
                KafkaContractSpec.CommandTypes.CreateHold,
                KafkaContractSpec.CurrentEnvelopeVersion,
                "rental-service",
                orderId.ToString("D"),
                DateTimeOffset.UtcNow,
                null,
                null,
                JsonDocument.Parse("""{"rentalOrderId":"123","quantity":999}""").RootElement);

            var res2 = await writer.EnqueueAsync(KafkaContractSpec.Topics.InventoryCommands, orderId.ToString("D"), conflictingEnvelope);
            Assert.True(res2.IsFailure);
            Assert.Equal("OUTBOX_MESSAGE_CONFLICT", res2.Error.Code);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task EnqueueAtomic_DoesNotFlushUnrelatedTrackedChanges()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            var repository = new OutboxRepository(db);

            // Add an unrelated tracked entity to db ChangeTracker without saving
            Guid unrelatedCartId = Guid.NewGuid();
            var cart = RentalCart.Create(unrelatedCartId, Guid.NewGuid(), DateTimeOffset.UtcNow).Value;
            await db.RentalCarts.AddAsync(cart);
            Assert.Equal(EntityState.Added, db.Entry(cart).State);

            // Enqueue outbox message
            Guid messageId = Guid.NewGuid();
            var outboxMsg = new RentalOutboxMessage(
                messageId,
                "rental.events.v1",
                "cart-1",
                "CartCreated",
                "{}",
                DateTimeOffset.UtcNow);

            var enqueueResult = await repository.EnqueueAtomicAsync(outboxMsg);
            Assert.True(enqueueResult.IsSuccess);

            // Cart must STILL be uncommitted in the database
            await using var verifyDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            var persistedCart = await verifyDb.RentalCarts.FirstOrDefaultAsync(c => c.Id == unrelatedCartId);
            Assert.Null(persistedCart);

            // Outbox message MUST be persisted
            var persistedOutbox = await verifyDb.RentalOutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            Assert.NotNull(persistedOutbox);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Producer_AfterAdvancingTimeProvider_SamePayloadReturnsReplay_DifferentPayloadReturnsConflict()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            var repository = new OutboxRepository(db);
            var writer = new KafkaOutboxWriter(repository);
            var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);

            var options = Options.Create(new KafkaOptions
            {
                Enabled = true,
                RentalEventsTopic = "rental.events.v1"
            });

            var producer = new RentalEventProducer(writer, options, timeProvider);

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            var payload = new RentalOrderCreatedPayload(
                RentalOrderId: orderId,
                OrderCode: "DH-2026-000001",
                RenterId: renterId,
                Currency: "VND",
                TotalRentalAmount: 1000000,
                TotalDepositAmount: 500000,
                TotalPayableAmount: 1500000,
                PaymentDeadline: timeProvider.GetUtcNow().AddMinutes(30),
                CreatedAt: timeProvider.GetUtcNow(),
                Items: new[]
                {
                    new RentalOrderCreatedItemPayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), 1000000, 500000)
                });

            // 1. Initial enqueue at T1
            var res1 = await producer.EnqueueRentalOrderCreatedAsync(payload);
            Assert.True(res1.IsSuccess);
            Assert.False(res1.Value.IsReplay);

            var stored1 = await db.RentalOutboxMessages.FirstAsync(m => m.Id == res1.Value.MessageId);
            DateTimeOffset originalOccurredAt = DateTimeOffset.Parse(
                JsonDocument.Parse(stored1.Payload).RootElement.GetProperty("occurredAt").GetString()!,
                CultureInfo.InvariantCulture);

            // 2. Advance time by 2 hours
            timeProvider.Advance(TimeSpan.FromHours(2));

            // 3. Replay with exact same payload at T2
            var res2 = await producer.EnqueueRentalOrderCreatedAsync(payload);
            Assert.True(res2.IsSuccess);
            Assert.True(res2.Value.IsReplay);
            Assert.Equal(res1.Value.MessageId, res2.Value.MessageId);

            await using var verifyDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            var stored2 = await verifyDb.RentalOutboxMessages.FirstAsync(m => m.Id == res1.Value.MessageId);
            DateTimeOffset preservedOccurredAt = DateTimeOffset.Parse(
                JsonDocument.Parse(stored2.Payload).RootElement.GetProperty("occurredAt").GetString()!,
                CultureInfo.InvariantCulture);
            Assert.Equal(originalOccurredAt, preservedOccurredAt);

            // 4. Same messageId deterministic key, but different payload (different amount)
            var conflictingPayload = payload with { TotalPayableAmount = 9999999 };
            var res3 = await producer.EnqueueRentalOrderCreatedAsync(conflictingPayload);
            Assert.True(res3.IsFailure);
            Assert.Equal("OUTBOX_MESSAGE_CONFLICT", res3.Error.Code);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task EnqueueAtomic_CallerTransaction_Rollback_DoesNotPersistOutbox()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            var repository = new OutboxRepository(db);
            Guid orderId = Guid.NewGuid();
            string opKey = "checkout:order-tx-rollback";
            Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId("rental-service", "CreateHold", opKey);

            var msg = new RentalOutboxMessage(
                messageId,
                KafkaContractSpec.Topics.InventoryCommands,
                orderId.ToString("D"),
                KafkaContractSpec.CommandTypes.CreateHold,
                """{"quantity":1}""",
                DateTimeOffset.UtcNow);

            // Caller begins transaction
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var enqueueResult = await repository.EnqueueAtomicAsync(msg);
                Assert.True(enqueueResult.IsSuccess);

                // Caller explicitly rolls back
                await tx.RollbackAsync();
            }

            // Verify Outbox record was rolled back
            await using var verifyDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            var persisted = await verifyDb.RentalOutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            Assert.Null(persisted);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task EnqueueAtomic_CallerTransaction_Commit_PersistsOutboxWithoutPrematureCommit()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            var repository = new OutboxRepository(db);
            Guid orderId = Guid.NewGuid();
            string opKey = "checkout:order-tx-commit";
            Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId("rental-service", "CreateHold", opKey);

            var msg = new RentalOutboxMessage(
                messageId,
                KafkaContractSpec.Topics.InventoryCommands,
                orderId.ToString("D"),
                KafkaContractSpec.CommandTypes.CreateHold,
                """{"quantity":5}""",
                DateTimeOffset.UtcNow);

            // Caller begins transaction
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var enqueueResult = await repository.EnqueueAtomicAsync(msg);
                Assert.True(enqueueResult.IsSuccess);

                // Caller commits
                await tx.CommitAsync();
            }

            // Verify Outbox record is persisted
            await using var verifyDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            var persisted = await verifyDb.RentalOutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            Assert.NotNull(persisted);
            Assert.Equal("Pending", persisted.Status);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task EnqueueAtomic_ConcurrentRace_OnPostgreSql_AllSucceed_WithoutUniqueViolationCrash()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var initDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await initDb.Database.MigrateAsync();

            Guid orderId = Guid.NewGuid();
            string opKey = "checkout:concurrent-race";
            Guid messageId = OutboxMessageIdGenerator.ComputeDeterministicId("rental-service", "CreateHold", opKey);
            string payload = """{"rentalOrderId":"race-1","quantity":1}""";

            const int workerCount = 8;
            var barrier = new Barrier(workerCount);

            var tasks = Enumerable.Range(0, workerCount).Select(async i =>
            {
                await Task.Yield();
                barrier.SignalAndWait();

                await using var workerDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
                var repo = new OutboxRepository(workerDb);

                var msg = new RentalOutboxMessage(
                    messageId,
                    KafkaContractSpec.Topics.InventoryCommands,
                    orderId.ToString("D"),
                    KafkaContractSpec.CommandTypes.CreateHold,
                    payload,
                    DateTimeOffset.UtcNow);

                return await repo.EnqueueAtomicAsync(msg);
            });

            var results = await Task.WhenAll(tasks);

            // All tasks must succeed (one as initial insert, others as idempotent replays)
            Assert.All(results, r => Assert.True(r.IsSuccess));
            Assert.Contains(results, r => !r.Value.IsReplay);
            Assert.Equal(workerCount - 1, results.Count(r => r.Value.IsReplay));

            await using var verifyDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            int totalCount = await verifyDb.RentalOutboxMessages.CountAsync(m => m.Id == messageId);
            Assert.Equal(1, totalCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task ClaimNextPendingAsync_OnPostgreSql_ClaimsOneMessageAtATimeWithLease()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            var repository = new OutboxRepository(db);

            // Add 3 pending messages
            for (int i = 1; i <= 3; i++)
            {
                var msg = new RentalOutboxMessage(
                    Guid.NewGuid(),
                    "rental.events.v1",
                    $"order-{i}",
                    "TestEvent",
                    $"{{\"index\":{i}}}",
                    DateTimeOffset.UtcNow.AddSeconds(i));
                await repository.AddAsync(msg);
            }
            await db.SaveChangesAsync();

            string claimToken1 = "worker-1";
            var claimed1 = await repository.ClaimNextPendingAsync(claimToken1, TimeSpan.FromSeconds(30));
            Assert.NotNull(claimed1);
            Assert.Equal(claimToken1, claimed1.LockedBy);

            string claimToken2 = "worker-2";
            var claimed2 = await repository.ClaimNextPendingAsync(claimToken2, TimeSpan.FromSeconds(30));
            Assert.NotNull(claimed2);
            Assert.Equal(claimToken2, claimed2.LockedBy);
            Assert.NotEqual(claimed1.Id, claimed2.Id);

            // Mark first message as published
            bool published1 = await repository.MarkPublishedAsync(claimed1.Id, claimToken1, DateTimeOffset.UtcNow);
            Assert.True(published1);

            await using var verifyDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            var reloaded1 = await verifyDb.RentalOutboxMessages.FirstAsync(m => m.Id == claimed1.Id);
            Assert.Equal("Published", reloaded1.Status);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
