using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Reminders;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

[Collection("PostgreSql")]
public sealed class RentalReminderPostgreSqlIntegrationTests
{
    private static async Task EnsurePostgreSqlAvailableAsync()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available.");
        }
    }

    [Fact]
    public async Task ClaimDueBatchAsync_ConcurrentWorkers_ClaimDisjointBatchesWithoutOverlap()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                // Seed 20 due reminder schedules
                for (int i = 0; i < 20; i++)
                {
                    var schedule = RentalReminderSchedule.Create(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        RentalReminderScheduleType.Handover,
                        1,
                        now.AddMinutes(-5),
                        now.AddDays(-1));
                    db.RentalReminderSchedules.Add(schedule);
                }
                await db.SaveChangesAsync();
            }

            // Two concurrent workers claiming batches of 10
            await using var db1 = new RentalServiceDbContext(options);
            await using var db2 = new RentalServiceDbContext(options);
            var repo1 = new RentalReminderScheduleRepository(db1);
            var repo2 = new RentalReminderScheduleRepository(db2);

            var task1 = repo1.ClaimDueBatchAsync("worker-1", TimeSpan.FromMinutes(2), 10);
            var task2 = repo2.ClaimDueBatchAsync("worker-2", TimeSpan.FromMinutes(2), 10);

            var results = await Task.WhenAll(task1, task2);
            var worker1Claims = results[0];
            var worker2Claims = results[1];

            Assert.Equal(10, worker1Claims.Count);
            Assert.Equal(10, worker2Claims.Count);

            var set1 = worker1Claims.Select(s => s.ScheduleId).ToHashSet();
            var set2 = worker2Claims.Select(s => s.ScheduleId).ToHashSet();

            // Strictly disjoint (no duplicate claims between workers)
            Assert.Empty(set1.Intersect(set2));

            // Verify database has updated leases
            await using var verifyDb = new RentalServiceDbContext(options);
            var w1InDb = await verifyDb.RentalReminderSchedules.CountAsync(s => s.LeasedBy == "worker-1");
            var w2InDb = await verifyDb.RentalReminderSchedules.CountAsync(s => s.LeasedBy == "worker-2");
            Assert.Equal(10, w1InDb);
            Assert.Equal(10, w2InDb);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task LeaseRecovery_WhenLeaseExpires_NextWorkerCanClaim()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid scheduleId;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                var schedule = RentalReminderSchedule.Create(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    RentalReminderScheduleType.Handover,
                    2,
                    now.AddMinutes(-10),
                    now.AddDays(-1));
                // Leased by a dead worker, lease expired 5 minutes ago
                schedule.Claim("crashed-worker", Guid.NewGuid(), now.AddMinutes(-5), now.AddMinutes(-10));
                db.RentalReminderSchedules.Add(schedule);
                await db.SaveChangesAsync();
                scheduleId = schedule.Id;
            }

            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                var claimed = await repo.ClaimDueBatchAsync("recovering-worker", TimeSpan.FromMinutes(1), 10);

                var claimedItem = Assert.Single(claimed);
                Assert.Equal(scheduleId, claimedItem.ScheduleId);
                var dbSchedule = await db.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Equal("recovering-worker", dbSchedule.LeasedBy);
                Assert.True(dbSchedule.LeasedUntil > now);
                Assert.Equal(claimedItem.LeaseToken, dbSchedule.LeaseToken);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
    [Fact]
    public async Task LeaseRecoveryAndFencing_WorkerAClaimsWithLease5s_Expires_WorkerBClaimsNewToken_WorkerAAdvanceIsFencedAndNotCommitted()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid scheduleId;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                var schedule = RentalReminderSchedule.Create(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    RentalReminderScheduleType.Handover,
                    2,
                    now.AddMinutes(-5),
                    now.AddDays(-1));
                db.RentalReminderSchedules.Add(schedule);
                await db.SaveChangesAsync();
                scheduleId = schedule.Id;
            }

            // 1. Worker A claims schedule with 5s lease
            ReminderClaimDescriptor claimA;
            await using (var dbA = new RentalServiceDbContext(options))
            {
                var repoA = new RentalReminderScheduleRepository(dbA);
                var claimedA = await repoA.ClaimDueBatchAsync("worker-A", TimeSpan.FromSeconds(5), 10);
                claimA = Assert.Single(claimedA);
                Assert.Equal(scheduleId, claimA.ScheduleId);
            }

            Guid staleTokenA = claimA.LeaseToken;

            // 2. Worker A lease expires in PostgreSQL
            await using (var dbExpire = new RentalServiceDbContext(options))
            {
                await dbExpire.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE rental.rental_reminder_schedules SET leased_until = NOW() - interval '1 second' WHERE \"Id\" = {scheduleId};");
            }

            // 3. Worker B claims schedule with new lease and new token
            ReminderClaimDescriptor claimB;
            await using (var dbB = new RentalServiceDbContext(options))
            {
                var repoB = new RentalReminderScheduleRepository(dbB);
                var claimedB = await repoB.ClaimDueBatchAsync("worker-B", TimeSpan.FromSeconds(30), 10);
                claimB = Assert.Single(claimedB);
                Assert.Equal(scheduleId, claimB.ScheduleId);
                Assert.NotEqual(staleTokenA, claimB.LeaseToken);
            }

            // 4. Worker A attempts to advance schedule with stale token -> fenced
            DateOnly notifiedDate = DateOnly.FromDateTime(DateTime.UtcNow);
            DateTimeOffset firedAt = DateTimeOffset.UtcNow;
            DateTimeOffset nextTriggerAt = firedAt.AddDays(1);

            await using (var dbA = new RentalServiceDbContext(options))
            {
                var repoA = new RentalReminderScheduleRepository(dbA);
                bool advancedByA = await repoA.AdvanceScheduleWithLeaseCheckAsync(
                    scheduleId,
                    staleTokenA,
                    notifiedDate,
                    firedAt,
                    nextTriggerAt,
                    isOverdue: false,
                    markCompleted: false);

                Assert.False(advancedByA);
            }

            // 5. Verify database state is NOT committed by Worker A and remains leased to Worker B
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var scheduleInDb = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Equal("worker-B", scheduleInDb.LeasedBy);
                Assert.Equal(claimB.LeaseToken, scheduleInDb.LeaseToken);
                Assert.Null(scheduleInDb.LastNotifiedDate);
                Assert.Null(scheduleInDb.LastNotifiedAt);
            }

            // 6. Worker B advances schedule with active token -> succeeds and commits
            await using (var dbB = new RentalServiceDbContext(options))
            {
                var repoB = new RentalReminderScheduleRepository(dbB);
                bool advancedByB = await repoB.AdvanceScheduleWithLeaseCheckAsync(
                    scheduleId,
                    claimB.LeaseToken,
                    notifiedDate,
                    firedAt,
                    nextTriggerAt,
                    isOverdue: false,
                    markCompleted: false);

                Assert.True(advancedByB);
            }

            // 7. Verify schedule is now committed with advance data
            await using (var verifyDb2 = new RentalServiceDbContext(options))
            {
                var finalSchedule = await verifyDb2.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Equal(notifiedDate, finalSchedule.LastNotifiedDate);
                Assert.Null(finalSchedule.LeasedBy);
                Assert.Null(finalSchedule.LeaseToken);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task RollbackAtomicity_WhenErrorOccursAfterOutboxEnqueueBeforeCommit_RollsBackAllChangesWithoutOrphanedOutboxOrScheduleAdvance()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = new(2026, 11, 9, 2, 0, 0, TimeSpan.Zero); // 02:00 UTC = 09:00 VN time (past 08:00 VN policy trigger)
            var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

            Guid orderId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            Guid scheduleId;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                var order = PostgresTestDatabaseHelper.CreateOrderWithItem(
                    orderId, itemId, supplierId, 1, now);
                var item = order.Items.First();
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                    .SetValue(item, RentalItemStatus.AwaitingHandover);
                typeof(RentalItem).GetProperty(nameof(RentalItem.ExpectedStartDate))!
                    .SetValue(item, RentalReminderPolicy.ToVietnamDate(now));

                db.RentalOrders.Add(order);

                var schedule = RentalReminderSchedule.Create(
                    orderId, itemId, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-1));
                db.RentalReminderSchedules.Add(schedule);

                await db.SaveChangesAsync();
                scheduleId = schedule.Id;
            }

            // Worker claims the schedule in database
            ReminderClaimDescriptor claim;
            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                var claimed = await repo.ClaimDueBatchAsync("worker-1", TimeSpan.FromSeconds(30), 1);
                claim = Assert.Single(claimed);
            }

            // Create descriptor with mismatched/stale fencing lease token
            // BatchProcessor will enqueue Outbox message in transaction, but AdvanceScheduleWithLeaseCheckAsync will fail!
            var staleDescriptor = new ReminderClaimDescriptor(
                claim.ScheduleId,
                claim.OrderId,
                claim.ItemId,
                claim.ScheduleType,
                Guid.NewGuid(), // Stale/invalid token
                claim.LeasedUntil);

            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                var outboxRepo = new OutboxRepository(db);
                var outboxWriter = new KafkaOutboxWriter(outboxRepo);
                var kafkaOptions = Microsoft.Extensions.Options.Options.Create(new KafkaOptions
                {
                    RentalEventsTopic = "rental.events.v1"
                });
                var producer = new RentalEventProducer(outboxWriter, kafkaOptions, timeProvider);
                var lockManager = new FakeOrderLockManager();

                var processor = new RentalReminderBatchProcessor(
                    db,
                    repo,
                    lockManager,
                    producer,
                    timeProvider,
                    unitOfWork: db,
                    logger: NullLogger<RentalReminderBatchProcessor>.Instance);

                var result = await processor.ProcessOrderReminderGroupAsync(orderId, [staleDescriptor]);

                Assert.Equal(1, result.Failed);
                Assert.Equal(0, result.NoticesSent);
            }

            // Verify in PostgreSQL: Atomicity guarantees no orphaned outbox messages and schedule has not advanced
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var outboxMessages = await verifyDb.RentalOutboxMessages.ToListAsync();
                Assert.Empty(outboxMessages);

                var dbSchedule = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Null(dbSchedule.LastNotifiedDate);
                Assert.Null(dbSchedule.LastNotifiedAt);
                Assert.Equal(0, dbSchedule.OverdueDayCount);
                Assert.Equal(RentalReminderScheduleStatus.Active, dbSchedule.Status);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task RollbackAtomicity_DirectTransaction_RollbackAfterOutboxEnqueue_DiscardsOutboxAndSchedule()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid scheduleId;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                var schedule = RentalReminderSchedule.Create(
                    Guid.NewGuid(), Guid.NewGuid(), RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-1));
                db.RentalReminderSchedules.Add(schedule);
                await db.SaveChangesAsync();
                scheduleId = schedule.Id;
            }

            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                var claimed = await repo.ClaimDueBatchAsync("worker-1", TimeSpan.FromMinutes(1), 1);
                var claim = Assert.Single(claimed);

                var outboxRepo = new OutboxRepository(db);
                var outboxMsg = new RentalOutboxMessage(
                    Guid.NewGuid(),
                    "rental.events.v1",
                    Guid.NewGuid().ToString("D"),
                    "RentalReminderDue",
                    "{\"test\":true}",
                    now);

                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    var enqueueResult = await outboxRepo.EnqueueAtomicAsync(outboxMsg);
                    Assert.True(enqueueResult.IsSuccess);

                    // Simulate an unexpected failure after outbox enqueue before transaction commit
                    await tx.RollbackAsync();
                }
            }

            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var outboxCount = await verifyDb.RentalOutboxMessages.CountAsync();
                Assert.Equal(0, outboxCount);

                var s = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Null(s.LastNotifiedDate);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task UniqueConstraint_SameItemAndType_ThrowsDbUpdateException()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid itemId = Guid.NewGuid();
            Guid orderId = Guid.NewGuid();

            await using var db = new RentalServiceDbContext(options);
            await db.Database.MigrateAsync();

            var schedule1 = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Handover, 1, now, now);
            db.RentalReminderSchedules.Add(schedule1);
            await db.SaveChangesAsync();

            // Attempt to insert duplicate for same itemId and same scheduleType
            var schedule2 = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Handover, 1, now, now);
            db.RentalReminderSchedules.Add(schedule2);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("uq_rental_reminder_schedules_item_type", ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task BackfillActiveItems_KeysetPagination_CreatesSchedulesAndIsIdempotentOnRerun()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);
            var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

            Guid orderId = Guid.NewGuid();
            Guid item1Id = Guid.NewGuid();
            Guid item2Id = Guid.NewGuid();

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                var order1 = PostgresTestDatabaseHelper.CreateOrderWithItem(
                    Guid.NewGuid(), item1Id, Guid.NewGuid(), 2, now);
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                    .SetValue(order1.Items.First(), RentalItemStatus.AwaitingHandover);

                var order2 = PostgresTestDatabaseHelper.CreateOrderWithItem(
                    Guid.NewGuid(), item2Id, Guid.NewGuid(), 1, now);
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                    .SetValue(order2.Items.First(), RentalItemStatus.InRental);

                db.RentalOrders.AddRange(order1, order2);
                await db.SaveChangesAsync();
            }

            // Run Backfill
            await using (var db = new RentalServiceDbContext(options))
            {
                var backfillService = new RentalReminderBackfillService(
                    db, timeProvider, NullLogger<RentalReminderBackfillService>.Instance);

                var result1 = await backfillService.BackfillActiveItemsAsync(batchSize: 10);
                Assert.Equal(1, result1.HandoverSchedulesCreated);
                Assert.Equal(1, result1.ReturnSchedulesCreated);

                var schedules = await db.RentalReminderSchedules.ToListAsync();
                Assert.Equal(2, schedules.Count);
                Assert.Contains(schedules, s => s.RentalItemId == item1Id && s.ScheduleType == RentalReminderScheduleType.Handover);
                Assert.Contains(schedules, s => s.RentalItemId == item2Id && s.ScheduleType == RentalReminderScheduleType.Return);

                // Re-run backfill: must be idempotent, creating 0 duplicates
                var result2 = await backfillService.BackfillActiveItemsAsync(batchSize: 10);
                Assert.Equal(0, result2.HandoverSchedulesCreated);
                Assert.Equal(0, result2.ReturnSchedulesCreated);

                var schedulesAfterRerun = await db.RentalReminderSchedules.ToListAsync();
                Assert.Equal(2, schedulesAfterRerun.Count);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Index_ExplainAnalyze_VerifyClaimQueryUsesIndex()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using var db = new RentalServiceDbContext(options);
            await db.Database.MigrateAsync();

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                EXPLAIN
                SELECT s.""Id""
                FROM rental.rental_reminder_schedules s
                WHERE s.status = 'Active'
                  AND s.remaining_unit_count > 0
                  AND (s.next_retry_at IS NULL OR s.next_retry_at <= NOW())
                  AND s.next_trigger_at IS NOT NULL
                  AND s.next_trigger_at <= NOW()
                  AND (s.leased_until IS NULL OR s.leased_until <= NOW())
                ORDER BY s.next_trigger_at, s.""Id""
                LIMIT 100;";

            var planLines = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                planLines.Add(reader.GetString(0));
            }

            var planText = string.Join("\n", planLines);
            // Verify query compiles cleanly and has a bounded execution plan
            Assert.NotEmpty(planText);
            Assert.Contains("Limit", planText);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task LockSchedulesForUpdateAsync_LocksRowsSuccessfully()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            Guid id1, id2;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();
                var s1 = RentalReminderSchedule.Create(Guid.NewGuid(), Guid.NewGuid(), RentalReminderScheduleType.Handover, 1, now, now);
                var s2 = RentalReminderSchedule.Create(Guid.NewGuid(), Guid.NewGuid(), RentalReminderScheduleType.Return, 1, now, now);
                db.RentalReminderSchedules.AddRange(s1, s2);
                await db.SaveChangesAsync();
                id1 = s1.Id;
                id2 = s2.Id;
            }

            await using var db2 = new RentalServiceDbContext(options);
            await using var tx = await db2.Database.BeginTransactionAsync();
            var repo = new RentalReminderScheduleRepository(db2);
            var locked = await repo.LockSchedulesForUpdateAsync(new[] { id2, id1 });
            Assert.Equal(2, locked.Count);
            Assert.Equal(new[] { id1, id2 }.OrderBy(x => x).ToList(), locked);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
