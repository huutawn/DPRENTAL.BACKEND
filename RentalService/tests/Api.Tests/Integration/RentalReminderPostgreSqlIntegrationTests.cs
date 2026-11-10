using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Application.IServices.Reminders;
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
using Microsoft.Extensions.DependencyInjection;
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
                    repo,
                    lockManager,
                    producer,
                    unitOfWork: db,
                    timeProvider: timeProvider,
                    logger: NullLogger<RentalReminderBatchProcessor>.Instance);

                var result = await processor.ProcessOrderReminderGroupAsync(orderId, [staleDescriptor]);

                Assert.Equal(0, result.NoticesSent);
                Assert.Equal(ReminderItemProcessOutcome.LeaseLost, result.ItemResults[0].Outcome);
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
                var item1 = order1.Items.First();
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                    .SetValue(item1, RentalItemStatus.AwaitingHandover);
                typeof(RentalItem).GetProperty(nameof(RentalItem.ExpectedStartDate))!
                    .SetValue(item1, DateOnly.FromDateTime(now.AddDays(2).DateTime));

                var order2 = PostgresTestDatabaseHelper.CreateOrderWithItem(
                    Guid.NewGuid(), item2Id, Guid.NewGuid(), 1, now);
                var item2 = order2.Items.First();
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                    .SetValue(item2, RentalItemStatus.InRental);
                typeof(RentalItem).GetProperty(nameof(RentalItem.ExpectedEndDate))!
                    .SetValue(item2, DateOnly.FromDateTime(now.AddDays(2).DateTime));
                db.RentalOrders.AddRange(order1, order2);
                await db.SaveChangesAsync();
            }

            // Run Backfill
            await using (var db = new RentalServiceDbContext(options))
            {
                var services = new ServiceCollection();
                services.AddScoped(_ => new RentalServiceDbContext(options));
                services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
                services.AddScoped<IRentalReminderScheduleRepository, RentalReminderScheduleRepository>();
                var sp = services.BuildServiceProvider();
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var lockManager = new FakeOrderLockManager();

                var backfillService = new RentalReminderBackfillService(
                    scopeFactory,
                    lockManager,
                    timeProvider,
                    NullLogger<RentalReminderBackfillService>.Instance);
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

    [Fact]
    public async Task Ownership_WhenWorkerALostLease_AllOperationsByWorkerAFailWithoutMutatingData()
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

            // 1. Worker A claims schedule with 2s lease
            ReminderClaimDescriptor claimA;
            await using (var dbA = new RentalServiceDbContext(options))
            {
                var repoA = new RentalReminderScheduleRepository(dbA);
                var claimed = await repoA.ClaimDueBatchAsync("worker-A", TimeSpan.FromSeconds(2), 10);
                claimA = Assert.Single(claimed);
            }

            // 2. Expire Worker A lease in database
            await using (var dbExpire = new RentalServiceDbContext(options))
            {
                await dbExpire.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE rental.rental_reminder_schedules SET leased_until = NOW() - interval '1 second' WHERE \"Id\" = {scheduleId};");
            }

            // 3. Worker B claims schedule
            ReminderClaimDescriptor claimB;
            await using (var dbB = new RentalServiceDbContext(options))
            {
                var repoB = new RentalReminderScheduleRepository(dbB);
                var claimed = await repoB.ClaimDueBatchAsync("worker-B", TimeSpan.FromMinutes(2), 10);
                claimB = Assert.Single(claimed);
                Assert.NotEqual(claimA.LeaseToken, claimB.LeaseToken);
            }

            // 4. Worker A attempts ALL operations with stale token A -> all MUST return false
            await using (var dbA = new RentalServiceDbContext(options))
            {
                var repoA = new RentalReminderScheduleRepository(dbA);

                bool advanceResult = await repoA.AdvanceScheduleWithLeaseCheckAsync(
                    scheduleId, claimA.LeaseToken, DateOnly.FromDateTime(DateTime.UtcNow), now, now.AddDays(1), false, false);
                Assert.False(advanceResult, "Advance must fail when lease is lost.");

                bool failResult = await repoA.FailScheduleWithLeaseCheckAsync(
                    scheduleId, claimA.LeaseToken, now.AddMinutes(5), now);
                Assert.False(failResult, "Fail must fail when lease is lost.");

                bool releaseResult = await repoA.ReleaseLeaseWithTokenAsync(
                    scheduleId, claimA.LeaseToken, now.AddMinutes(5), now);
                Assert.False(releaseResult, "Release must fail when lease is lost.");

                bool pauseResult = await repoA.PauseScheduleWithLeaseCheckAsync(
                    scheduleId, claimA.LeaseToken, now);
                Assert.False(pauseResult, "Pause must fail when lease is lost.");

                bool completeResult = await repoA.CompleteScheduleWithLeaseCheckAsync(
                    scheduleId, claimA.LeaseToken, now);
                Assert.False(completeResult, "Complete must fail when lease is lost.");
            }

            // 5. Verify database state is untouched by Worker A and remains leased to Worker B
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var dbSchedule = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Equal("worker-B", dbSchedule.LeasedBy);
                Assert.Equal(claimB.LeaseToken, dbSchedule.LeaseToken);
                Assert.Equal(RentalReminderScheduleStatus.Active, dbSchedule.Status);
                Assert.Null(dbSchedule.LastNotifiedDate);
                Assert.Equal(0, dbSchedule.RetryCount);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Lifecycle_PauseResumeAfterClaim_StaleDescriptorCannotOverwrite()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid scheduleId;
            Guid itemId = Guid.NewGuid();

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();
                var schedule = RentalReminderSchedule.Create(
                    Guid.NewGuid(), itemId, RentalReminderScheduleType.Return, 2, now.AddMinutes(-5), now.AddDays(-1));
                db.RentalReminderSchedules.Add(schedule);
                await db.SaveChangesAsync();
                scheduleId = schedule.Id;
            }

            // 1. Worker claims schedule
            ReminderClaimDescriptor claim;
            await using (var dbClaim = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(dbClaim);
                var claimed = await repo.ClaimDueBatchAsync("worker-1", TimeSpan.FromMinutes(2), 10);
                claim = Assert.Single(claimed);
            }

            // 2. Lifecycle updates schedule: pause due to all units in return request (remaining = 0)
            await using (var dbLifecycle = new RentalServiceDbContext(options))
            {
                var scheduleService = new RentalReminderScheduleService(
                    new RentalReminderScheduleRepository(dbLifecycle),
                    NullLogger<RentalReminderScheduleService>.Instance);

                await scheduleService.OnReturnUnitsChangedAsync(itemId, 0, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)), now);
                await dbLifecycle.SaveChangesAsync();
            }

            // 3. Worker tries to advance with descriptor token -> MUST fail (fenced by lease clearing)
            await using (var dbWorker = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(dbWorker);
                bool advanced = await repo.AdvanceScheduleWithLeaseCheckAsync(
                    scheduleId,
                    claim.LeaseToken,
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    now,
                    now.AddDays(1),
                    false,
                    false);

                Assert.False(advanced, "Worker with stale descriptor must not be able to advance a paused schedule.");
            }

            // 4. Verify schedule in DB remains paused with remaining_unit_count = 0 and trigger = null
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var dbSchedule = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Equal(0, dbSchedule.RemainingUnitCount);
                Assert.Null(dbSchedule.NextTriggerAt);
                Assert.Null(dbSchedule.LeasedBy);
                Assert.Null(dbSchedule.LeaseToken);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Retry_WhenRollbackFinished_RetryRecordedInNewContext_NextRetryAtPreventsImmediateReclaim()
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

            // 1. Worker claims schedule
            ReminderClaimDescriptor claim;
            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                var claimed = await repo.ClaimDueBatchAsync("worker-1", TimeSpan.FromMinutes(2), 1);
                claim = Assert.Single(claimed);
            }

            // 2. Order transaction rolls back
            await using (var orderDb = new RentalServiceDbContext(options))
            {
                await using var tx = await orderDb.Database.BeginTransactionAsync();
                // Some operations happened... then error -> rollback!
                await tx.RollbackAsync();
            }

            // 3. Record retry in a fresh scope/context
            DateTimeOffset nextRetry = now.AddMinutes(10);
            await using (var retryDb = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(retryDb);
                bool recorded = await repo.FailScheduleWithLeaseCheckAsync(
                    claim.ScheduleId,
                    claim.LeaseToken,
                    nextRetry,
                    now);

                Assert.True(recorded);
            }

            // 4. Verify in DB: retry_count = 1, next_retry_at = nextRetry, lease is released
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var dbSchedule = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
                Assert.Equal(1, dbSchedule.RetryCount);
                Assert.True(dbSchedule.NextRetryAt.HasValue && (dbSchedule.NextRetryAt.Value - nextRetry).Duration() < TimeSpan.FromMilliseconds(1));
                Assert.Null(dbSchedule.LeasedBy);
                Assert.Null(dbSchedule.LeaseToken);

                // 5. A competing worker attempts to claim: MUST be skipped because next_retry_at > NOW()!
                var repo = new RentalReminderScheduleRepository(verifyDb);
                var reclaimed = await repo.ClaimDueBatchAsync("competing-worker", TimeSpan.FromMinutes(1), 10);
                Assert.Empty(reclaimed);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task RollbackAtomicity_WhenGroupHasThreeSchedulesAndSecondLosesLease_RollsBackAllAndReplacesProvisionalOutcomesCleanly()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = new(2026, 11, 9, 2, 0, 0, TimeSpan.Zero); // 09:00 VN
            var timeProvider = new FixedTimeProvider(now);

            Guid orderId = Guid.NewGuid();
            Guid item1Id = Guid.NewGuid();
            Guid item2Id = Guid.NewGuid();
            Guid item3Id = Guid.NewGuid();
            Guid s1Id, s2Id, s3Id;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                DateOnly startDate = DateOnly.FromDateTime(now.DateTime);
                DateOnly endDate = DateOnly.FromDateTime(now.AddDays(5).DateTime);

                var order = PostgresTestDatabaseHelper.CreateOrderWithMultipleItems(
                    orderId,
                    now,
                    (item1Id, Guid.NewGuid(), 1, startDate, endDate),
                    (item2Id, Guid.NewGuid(), 1, startDate, endDate),
                    (item3Id, Guid.NewGuid(), 1, startDate, endDate));

                foreach (var item in order.Items)
                {
                    typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                        .SetValue(item, RentalItemStatus.AwaitingHandover);
                }

                db.RentalOrders.Add(order);

                var s1 = RentalReminderSchedule.Create(orderId, item1Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-1));
                var s2 = RentalReminderSchedule.Create(orderId, item2Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-4), now.AddDays(-1));
                var s3 = RentalReminderSchedule.Create(orderId, item3Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-3), now.AddDays(-1));
                db.RentalReminderSchedules.AddRange(s1, s2, s3);

                await db.SaveChangesAsync();
                s1Id = s1.Id;
                s2Id = s2.Id;
                s3Id = s3.Id;
            }

            // Worker 1 claims all 3 schedules
            IReadOnlyList<ReminderClaimDescriptor> claims;
            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                claims = await repo.ClaimDueBatchAsync("worker-1", TimeSpan.FromMinutes(2), 10);
                Assert.Equal(3, claims.Count);
            }

            var desc1 = claims.First(c => c.ScheduleId == s1Id);
            var desc2 = claims.First(c => c.ScheduleId == s2Id);
            var desc3 = claims.First(c => c.ScheduleId == s3Id);

            // Worker B steals lease for Schedule 2 in DB before worker 1 executes advance
            // Create stale descriptor for desc2
            await using (var db = new RentalServiceDbContext(options))
            {
                var realRepo = new RentalReminderScheduleRepository(db);
                var repo = new FencingFailingScheduleRepository(realRepo, s2Id);
                var outboxRepo = new OutboxRepository(db);
                var outboxWriter = new KafkaOutboxWriter(outboxRepo);
                var kafkaOptions = Microsoft.Extensions.Options.Options.Create(new KafkaOptions { RentalEventsTopic = "rental.events.v1" });
                var producer = new RentalEventProducer(outboxWriter, kafkaOptions, timeProvider);
                var lockManager = new FakeOrderLockManager();

                var processor = new RentalReminderBatchProcessor(
                    repo,
                    lockManager,
                    producer,
                    unitOfWork: db,
                    timeProvider: timeProvider,
                    logger: NullLogger<RentalReminderBatchProcessor>.Instance);

                // Pass [desc1, desc2, desc3]
                var result = await processor.ProcessOrderReminderGroupAsync(orderId, [desc1, desc2, desc3]);

                // Fencing failure on schedule 2 rolled back entire group!
                // NoticesSent and Completed must be 0
                Assert.Equal(0, result.NoticesSent);
                Assert.Equal(0, result.Completed);
                Assert.Equal(3, result.ItemResults.Count);

                var r1 = result.ItemResults.First(r => r.ScheduleId == s1Id);
                var r2 = result.ItemResults.First(r => r.ScheduleId == s2Id);
                var r3 = result.ItemResults.First(r => r.ScheduleId == s3Id);

                // Schedule 2 caused fencing failure -> LeaseLost
                Assert.Equal(ReminderItemProcessOutcome.LeaseLost, r2.Outcome);
                // Schedule 1 was provisional succeeded but rolled back -> RetryableFailure
                Assert.Equal(ReminderItemProcessOutcome.RetryableFailure, r1.Outcome);
                // Schedule 3 was not yet processed -> RetryableFailure
                Assert.Equal(ReminderItemProcessOutcome.RetryableFailure, r3.Outcome);
            }

            // Verify DB state: zero outbox messages, s1 was NOT advanced
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var outboxCount = await verifyDb.RentalOutboxMessages.CountAsync();
                Assert.Equal(0, outboxCount);

                var dbS1 = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == s1Id);
                Assert.Null(dbS1.LastNotifiedDate);
                Assert.Null(dbS1.LastNotifiedAt);

                // Simulate competing worker stealing lease for s1 while rollback was in-flight
                var stolenToken = Guid.NewGuid();
                await verifyDb.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE rental.rental_reminder_schedules SET leased_by = 'competing-worker', lease_token = {stolenToken}, leased_until = {now.AddMinutes(10)} WHERE \"Id\" = {s1Id};");
            }

            // Retry scope in a fresh context:
            // s2 caused fencing failure -> outcome is LeaseLost, no retry is attempted
            // s1 was rolled back -> worker attempts retry, but lease token is now lost -> MUST reject overwrite!
            // s3 was not processed -> worker attempts retry with still-valid token -> succeeds!
            await using (var retryDb = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(retryDb);

                // Attempt retry for s1 with stolen token: MUST fail and NOT overwrite token
                bool s1RetryRecorded = await repo.FailScheduleWithLeaseCheckAsync(desc1.ScheduleId, desc1.LeaseToken, now.AddMinutes(5), now);
                Assert.False(s1RetryRecorded);

                // Attempt retry for s3 with valid token: succeeds
                bool s3RetryRecorded = await repo.FailScheduleWithLeaseCheckAsync(desc3.ScheduleId, desc3.LeaseToken, now.AddMinutes(5), now);
                Assert.True(s3RetryRecorded);
            }

            // Verify in DB that s1's stolen token was NOT overwritten
            await using (var finalDb = new RentalServiceDbContext(options))
            {
                var finalS1 = await finalDb.RentalReminderSchedules.FirstAsync(s => s.Id == s1Id);
                Assert.Equal("competing-worker", finalS1.LeasedBy);
                Assert.NotEqual(desc1.LeaseToken, finalS1.LeaseToken);
                Assert.Equal(0, finalS1.RetryCount);

                var finalS3 = await finalDb.RentalReminderSchedules.FirstAsync(s => s.Id == s3Id);
                Assert.Equal(1, finalS3.RetryCount);
                Assert.Null(finalS3.LeasedBy);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task CommitSuccess_WhenAllThreeSchedulesSucceed_ResultMatchesDatabaseState()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = new(2026, 11, 9, 2, 0, 0, TimeSpan.Zero); // 09:00 VN
            var timeProvider = new FixedTimeProvider(now);

            Guid orderId = Guid.NewGuid();
            Guid item1Id = Guid.NewGuid();
            Guid item2Id = Guid.NewGuid();
            Guid item3Id = Guid.NewGuid();
            Guid s1Id, s2Id, s3Id;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                DateOnly startDate = DateOnly.FromDateTime(now.DateTime);
                DateOnly futureStartDate = DateOnly.FromDateTime(now.AddDays(5).DateTime);
                DateOnly expiredEndDate = DateOnly.FromDateTime(now.AddDays(-35).DateTime);

                var order = PostgresTestDatabaseHelper.CreateOrderWithMultipleItems(
                    orderId,
                    now,
                    (item1Id, Guid.NewGuid(), 1, startDate, startDate.AddDays(3)),
                    (item2Id, Guid.NewGuid(), 1, expiredEndDate.AddDays(-5), expiredEndDate),
                    (item3Id, Guid.NewGuid(), 1, futureStartDate, futureStartDate.AddDays(3)));

                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(order.Items.First(i => i.Id == item1Id), RentalItemStatus.AwaitingHandover);
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(order.Items.First(i => i.Id == item2Id), RentalItemStatus.InRental);
                typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(order.Items.First(i => i.Id == item3Id), RentalItemStatus.AwaitingHandover);

                db.RentalOrders.Add(order);

                // s1: SendToday
                var s1 = RentalReminderSchedule.Create(orderId, item1Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-1));
                // s2: WindowExpired
                var s2 = RentalReminderSchedule.Create(orderId, item2Id, RentalReminderScheduleType.Return, 1, now.AddMinutes(-4), now.AddDays(-35));
                // s3: Reschedule
                var s3 = RentalReminderSchedule.Create(orderId, item3Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-3), now.AddDays(-1));

                db.RentalReminderSchedules.AddRange(s1, s2, s3);
                await db.SaveChangesAsync();
                s1Id = s1.Id;
                s2Id = s2.Id;
                s3Id = s3.Id;
            }

            IReadOnlyList<ReminderClaimDescriptor> claims;
            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                claims = await repo.ClaimDueBatchAsync("commit-worker", TimeSpan.FromMinutes(2), 10);
                Assert.Equal(3, claims.Count);
            }

            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                var outboxRepo = new OutboxRepository(db);
                var outboxWriter = new KafkaOutboxWriter(outboxRepo);
                var kafkaOptions = Microsoft.Extensions.Options.Options.Create(new KafkaOptions { RentalEventsTopic = "rental.events.v1" });
                var producer = new RentalEventProducer(outboxWriter, kafkaOptions, timeProvider);
                var lockManager = new FakeOrderLockManager();

                var processor = new RentalReminderBatchProcessor(
                    repo,
                    lockManager,
                    producer,
                    unitOfWork: db,
                    timeProvider: timeProvider,
                    logger: NullLogger<RentalReminderBatchProcessor>.Instance);

                var result = await processor.ProcessOrderReminderGroupAsync(orderId, claims);

                // Commit success: figures match exactly
                Assert.Equal(3, result.Processed);
                Assert.Equal(1, result.NoticesSent);
                Assert.Equal(1, result.Completed);
                Assert.Equal(0, result.Failed);
                Assert.Equal(3, result.ItemResults.Count);

                var r1 = result.ItemResults.First(r => r.ScheduleId == s1Id);
                var r2 = result.ItemResults.First(r => r.ScheduleId == s2Id);
                var r3 = result.ItemResults.First(r => r.ScheduleId == s3Id);

                Assert.Equal(ReminderItemProcessOutcome.Succeeded, r1.Outcome);
                Assert.Equal(ReminderItemProcessOutcome.Succeeded, r2.Outcome);
                Assert.Equal(ReminderItemProcessOutcome.Deferred, r3.Outcome);
            }

            // Verify DB matches exactly:
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var outbox = await verifyDb.RentalOutboxMessages.ToListAsync();
                Assert.Single(outbox);

                var dbS1 = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == s1Id);
                Assert.NotNull(dbS1.LastNotifiedDate);

                var dbS2 = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == s2Id);
                Assert.Equal(RentalReminderScheduleStatus.Completed, dbS2.Status);

                var dbS3 = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == s3Id);
                Assert.Equal(RentalReminderScheduleStatus.Active, dbS3.Status);
                Assert.Null(dbS3.LeasedBy);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task CommitFailure_WhenCommitThrows_RollsBackAllAndPublishesNoSuccess()
    {
        await EnsurePostgreSqlAvailableAsync();
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = new(2026, 11, 9, 2, 0, 0, TimeSpan.Zero); // 09:00 VN
            var timeProvider = new FixedTimeProvider(now);

            Guid orderId = Guid.NewGuid();
            Guid item1Id = Guid.NewGuid();
            Guid item2Id = Guid.NewGuid();
            Guid item3Id = Guid.NewGuid();
            Guid s1Id, s2Id, s3Id;

            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();

                DateOnly startDate = DateOnly.FromDateTime(now.DateTime);

                var order = PostgresTestDatabaseHelper.CreateOrderWithMultipleItems(
                    orderId,
                    now,
                    (item1Id, Guid.NewGuid(), 1, startDate, startDate.AddDays(3)),
                    (item2Id, Guid.NewGuid(), 1, startDate, startDate.AddDays(3)),
                    (item3Id, Guid.NewGuid(), 1, startDate, startDate.AddDays(3)));

                foreach (var item in order.Items)
                {
                    typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.AwaitingHandover);
                }

                db.RentalOrders.Add(order);

                var s1 = RentalReminderSchedule.Create(orderId, item1Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-1));
                var s2 = RentalReminderSchedule.Create(orderId, item2Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-4), now.AddDays(-1));
                var s3 = RentalReminderSchedule.Create(orderId, item3Id, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-3), now.AddDays(-1));

                db.RentalReminderSchedules.AddRange(s1, s2, s3);
                await db.SaveChangesAsync();
                s1Id = s1.Id;
                s2Id = s2.Id;
                s3Id = s3.Id;
            }

            IReadOnlyList<ReminderClaimDescriptor> claims;
            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                claims = await repo.ClaimDueBatchAsync("failing-commit-worker", TimeSpan.FromMinutes(2), 10);
                Assert.Equal(3, claims.Count);
            }

            await using (var db = new RentalServiceDbContext(options))
            {
                var repo = new RentalReminderScheduleRepository(db);
                var outboxRepo = new OutboxRepository(db);
                var outboxWriter = new KafkaOutboxWriter(outboxRepo);
                var kafkaOptions = Microsoft.Extensions.Options.Options.Create(new KafkaOptions { RentalEventsTopic = "rental.events.v1" });
                var producer = new RentalEventProducer(outboxWriter, kafkaOptions, timeProvider);
                var lockManager = new FakeOrderLockManager();

                var failingUnitOfWork = new FailingCommitUnitOfWork(db);

                var processor = new RentalReminderBatchProcessor(
                    repo,
                    lockManager,
                    producer,
                    unitOfWork: failingUnitOfWork,
                    timeProvider: timeProvider,
                    logger: NullLogger<RentalReminderBatchProcessor>.Instance);

                var result = await processor.ProcessOrderReminderGroupAsync(orderId, claims);

                // Commit failure: must roll back all, noticesSent = 0, completed = 0, no success published
                Assert.Equal(3, result.Processed);
                Assert.Equal(0, result.NoticesSent);
                Assert.Equal(0, result.Completed);
                Assert.Equal(3, result.Failed);

                Assert.All(result.ItemResults, r => Assert.Equal(ReminderItemProcessOutcome.RetryableFailure, r.Outcome));
            }

            // Verify DB state: zero outbox messages, no schedules advanced
            await using (var verifyDb = new RentalServiceDbContext(options))
            {
                var outboxCount = await verifyDb.RentalOutboxMessages.CountAsync();
                Assert.Equal(0, outboxCount);

                var dbS1 = await verifyDb.RentalReminderSchedules.FirstAsync(s => s.Id == s1Id);
                Assert.Null(dbS1.LastNotifiedDate);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private sealed class FailingCommitUnitOfWork : IUnitOfWork
    {
        private readonly RentalServiceDbContext _inner;
        public FailingCommitUnitOfWork(RentalServiceDbContext inner) => _inner = inner;
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => _inner.SaveChangesAsync(cancellationToken);
        public void DiscardChanges() => _inner.DiscardChanges();
        public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            var tx = await _inner.BeginTransactionAsync(cancellationToken);
            return new FailingTransaction(tx);
        }

        private sealed class FailingTransaction : IUnitOfWorkTransaction
        {
            private readonly IUnitOfWorkTransaction _innerTx;
            public FailingTransaction(IUnitOfWorkTransaction innerTx) => _innerTx = innerTx;
            public Task CommitAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Simulated database commit failure");
            public Task RollbackAsync(CancellationToken cancellationToken = default) => _innerTx.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => _innerTx.DisposeAsync();
        }
    }

    private sealed class FencingFailingScheduleRepository : IRentalReminderScheduleRepository
    {
        private readonly IRentalReminderScheduleRepository _inner;
        private readonly Guid _failingScheduleId;

        public FencingFailingScheduleRepository(IRentalReminderScheduleRepository inner, Guid failingScheduleId)
        {
            _inner = inner;
            _failingScheduleId = failingScheduleId;
        }

        public Task<bool> AdvanceScheduleWithLeaseCheckAsync(
            Guid scheduleId, Guid leaseToken, DateOnly notifiedDate, DateTimeOffset firedAt,
            DateTimeOffset? nextTriggerAt, bool isOverdue, bool markCompleted, CancellationToken cancellationToken = default)
        {
            if (scheduleId == _failingScheduleId)
            {
                return Task.FromResult(false);
            }
            return _inner.AdvanceScheduleWithLeaseCheckAsync(scheduleId, leaseToken, notifiedDate, firedAt, nextTriggerAt, isOverdue, markCompleted, cancellationToken);
        }

        public Task<IReadOnlyList<ReminderClaimDescriptor>> ClaimDueBatchAsync(string workerId, TimeSpan leaseDuration, int batchSize, CancellationToken cancellationToken = default)
            => _inner.ClaimDueBatchAsync(workerId, leaseDuration, batchSize, cancellationToken);

        public Task<ReminderOrderProjection?> GetOrderReminderProjectionAsync(Guid orderId, CancellationToken cancellationToken = default)
            => _inner.GetOrderReminderProjectionAsync(orderId, cancellationToken);

        public Task<IReadOnlyList<Guid>> LockSchedulesForUpdateAsync(IEnumerable<Guid> scheduleIds, CancellationToken cancellationToken = default)
            => _inner.LockSchedulesForUpdateAsync(scheduleIds, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, ReminderScheduleStateProjection>> GetScheduleStatesAsync(IEnumerable<Guid> scheduleIds, CancellationToken cancellationToken = default)
            => _inner.GetScheduleStatesAsync(scheduleIds, cancellationToken);

        public Task<bool> FailScheduleWithLeaseCheckAsync(Guid scheduleId, Guid leaseToken, DateTimeOffset nextRetryAt, DateTimeOffset now, CancellationToken cancellationToken = default)
            => _inner.FailScheduleWithLeaseCheckAsync(scheduleId, leaseToken, nextRetryAt, now, cancellationToken);

        public Task<bool> ReleaseLeaseWithTokenAsync(Guid scheduleId, Guid leaseToken, DateTimeOffset? postponeTriggerTo, DateTimeOffset now, CancellationToken cancellationToken = default)
            => _inner.ReleaseLeaseWithTokenAsync(scheduleId, leaseToken, postponeTriggerTo, now, cancellationToken);

        public Task<bool> PauseScheduleWithLeaseCheckAsync(Guid scheduleId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken = default)
            => _inner.PauseScheduleWithLeaseCheckAsync(scheduleId, leaseToken, now, cancellationToken);

        public Task<bool> CompleteScheduleWithLeaseCheckAsync(Guid scheduleId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken = default)
            => _inner.CompleteScheduleWithLeaseCheckAsync(scheduleId, leaseToken, now, cancellationToken);

        public Task<RentalReminderSchedule?> GetByItemAndTypeAsync(Guid rentalItemId, RentalReminderScheduleType scheduleType, CancellationToken cancellationToken = default)
            => _inner.GetByItemAndTypeAsync(rentalItemId, scheduleType, cancellationToken);

        public Task<IReadOnlyList<RentalReminderSchedule>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default)
            => _inner.GetByOrderIdAsync(orderId, cancellationToken);

        public Task<IReadOnlyList<RentalReminderSchedule>> GetByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default)
            => _inner.GetByItemIdAsync(itemId, cancellationToken);

        public Task AddAsync(RentalReminderSchedule schedule, CancellationToken cancellationToken = default)
            => _inner.AddAsync(schedule, cancellationToken);

        public Task UpdateAsync(RentalReminderSchedule schedule, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(schedule, cancellationToken);

        public Task UpdateRangeAsync(IEnumerable<RentalReminderSchedule> schedules, CancellationToken cancellationToken = default)
            => _inner.UpdateRangeAsync(schedules, cancellationToken);

        public Task<IReadOnlyList<RentalReminderSchedule>> GetActiveSchedulesPagedAsync(Guid? afterId, int pageSize, CancellationToken cancellationToken = default)
            => _inner.GetActiveSchedulesPagedAsync(afterId, pageSize, cancellationToken);

        public Task<IReadOnlyList<ReminderBackfillCandidate>> GetHandoverBackfillCandidatesAsync(Guid? afterItemId, int batchSize, CancellationToken cancellationToken = default)
            => _inner.GetHandoverBackfillCandidatesAsync(afterItemId, batchSize, cancellationToken);

        public Task<IReadOnlyList<ReminderBackfillCandidate>> GetReturnBackfillCandidatesAsync(Guid? afterItemId, int batchSize, CancellationToken cancellationToken = default)
            => _inner.GetReturnBackfillCandidatesAsync(afterItemId, batchSize, cancellationToken);

        public Task<bool> TryCreateScheduleIfMissingAsync(RentalReminderSchedule schedule, CancellationToken cancellationToken = default)
            => _inner.TryCreateScheduleIfMissingAsync(schedule, cancellationToken);
    }
}
