using System.IO;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Services.Reminders;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Maintenance;

public sealed class RentalReminderMaintenanceTests
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);

    private static DbContextOptions<RentalServiceDbContext> CreateOptions(string dbName)
    {
        return new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
    }

    private static List<Guid> GenerateSortedGuids(int count)
    {
        var guids = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            guids.Add(Guid.NewGuid());
        }
        guids.Sort();
        return guids;
    }

    private static RentalOrder CreateTestOrder(Guid orderId, Guid itemId, RentalItemStatus status, DateOnly? startDate = null, DateOnly? endDate = null, int quantity = 1)
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var start = startDate ?? new DateOnly(2026, 11, 10);
        var end = endDate ?? (start.AddDays(5) > start ? start.AddDays(5) : start.AddDays(1));

        var line = new InitialRentalItemDto(
            itemId,
            Guid.NewGuid(),
            supplierId,
            quantity,
            start,
            end,
            "Test Item",
            null,
            "Kho Thủ Đức, TP.HCM",
            100_000m,
            500_000m,
            $"grp-{Guid.NewGuid():N}",
            Enumerable.Range(0, quantity)
                .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
                .ToList(),
            TestListingSnapshots.Default);

        var orderResult = RentalOrder.Create(
            orderId,
            $"DH-TEST-{orderId.ToString("N")[..6]}",
            renterId,
            "VND",
            "Delivery",
            "Nguyễn Văn An",
            "0901234567",
            "12 Võ Văn Ngân, Thủ Đức, TP.HCM",
            "Giao trước 7 giờ sáng",
            OrderTestData.RefundAccount,
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            $"op-{Guid.NewGuid():N}",
            FixedNow.AddDays(-2),
            [line]);

        var order = orderResult.Value;
        var item = order.Items.First();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, status);

        return order;
    }

    [Fact]
    public async Task BackfillActiveItemsAsync_KeysetPagination_ProcessesAllBatchesCorrectly()
    {
        string dbName = $"Keyset_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);
        var sortedIds = GenerateSortedGuids(5);

        await using (var db = new RentalServiceDbContext(options))
        {
            for (int i = 0; i < 5; i++)
            {
                var order = CreateTestOrder(Guid.NewGuid(), sortedIds[i], RentalItemStatus.AwaitingHandover,
                    startDate: DateOnly.FromDateTime(FixedNow.AddDays(2).DateTime));
                db.RentalOrders.Add(order);
            }
            await db.SaveChangesAsync();
        }

        await using (var db = new RentalServiceDbContext(options))
        {
            var timeProvider = new FixedTimeProvider(FixedNow);
            var service = new RentalReminderBackfillService(db, timeProvider, NullLogger<RentalReminderBackfillService>.Instance);

            var batchProgressList = new List<BackfillProgress>();
            var result = await service.BackfillActiveItemsAsync(
                batchSize: 2,
                onBatchCompleted: p => batchProgressList.Add(p));

            Assert.Equal(5, result.HandoverSchedulesCreated);
            Assert.Equal(0, result.ReturnSchedulesCreated);
            Assert.Equal(3, result.TotalBatches);
            Assert.Equal(3, batchProgressList.Count);

            // Batch 1: 2 items, checkpoint = sortedIds[1]
            Assert.Equal(2, batchProgressList[0].HandoverCreated);
            Assert.Equal(sortedIds[1], batchProgressList[0].LastProcessedItemId);

            // Batch 2: 2 items, checkpoint = sortedIds[3]
            Assert.Equal(2, batchProgressList[1].HandoverCreated);
            Assert.Equal(sortedIds[3], batchProgressList[1].LastProcessedItemId);

            // Batch 3: 1 item, checkpoint = sortedIds[4]
            Assert.Equal(1, batchProgressList[2].HandoverCreated);
            Assert.Equal(sortedIds[4], batchProgressList[2].LastProcessedItemId);

            var schedules = await db.RentalReminderSchedules.ToListAsync();
            Assert.Equal(5, schedules.Count);
            Assert.All(sortedIds, id => Assert.Contains(schedules, s => s.RentalItemId == id && s.ScheduleType == RentalReminderScheduleType.Handover));
        }
    }

    [Fact]
    public async Task BackfillActiveItemsAsync_WithCheckpoint_ResumesAfterCheckpoint()
    {
        string dbName = $"Checkpoint_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);
        var sortedIds = GenerateSortedGuids(4);

        await using (var db = new RentalServiceDbContext(options))
        {
            for (int i = 0; i < 4; i++)
            {
                var order = CreateTestOrder(Guid.NewGuid(), sortedIds[i], RentalItemStatus.AwaitingHandover,
                    startDate: DateOnly.FromDateTime(FixedNow.AddDays(2).DateTime));
                db.RentalOrders.Add(order);
            }
            await db.SaveChangesAsync();
        }

        await using (var db = new RentalServiceDbContext(options))
        {
            var timeProvider = new FixedTimeProvider(FixedNow);
            var service = new RentalReminderBackfillService(db, timeProvider, NullLogger<RentalReminderBackfillService>.Instance);

            // Resume after sortedIds[1] (item 2)
            var result = await service.BackfillActiveItemsAsync(
                batchSize: 2,
                startAfterItemId: sortedIds[1]);

            Assert.Equal(2, result.HandoverSchedulesCreated);
            Assert.Equal(sortedIds[3], result.LastProcessedItemId);

            var schedules = await db.RentalReminderSchedules.ToListAsync();
            Assert.Equal(2, schedules.Count);
            Assert.DoesNotContain(schedules, s => s.RentalItemId == sortedIds[0]);
            Assert.DoesNotContain(schedules, s => s.RentalItemId == sortedIds[1]);
            Assert.Contains(schedules, s => s.RentalItemId == sortedIds[2]);
            Assert.Contains(schedules, s => s.RentalItemId == sortedIds[3]);
        }
    }

    [Fact]
    public async Task BackfillActiveItemsAsync_IdempotentRerun_CreatesZeroDuplicates()
    {
        string dbName = $"Idempotent_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);
        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();

        await using (var db = new RentalServiceDbContext(options))
        {
            var order1 = CreateTestOrder(Guid.NewGuid(), item1Id, RentalItemStatus.AwaitingHandover,
                startDate: DateOnly.FromDateTime(FixedNow.AddDays(1).DateTime));
            var order2 = CreateTestOrder(Guid.NewGuid(), item2Id, RentalItemStatus.InRental,
                startDate: DateOnly.FromDateTime(FixedNow.AddDays(-2).DateTime),
                endDate: DateOnly.FromDateTime(FixedNow.AddDays(3).DateTime));

            db.RentalOrders.AddRange(order1, order2);
            await db.SaveChangesAsync();
        }

        await using (var db = new RentalServiceDbContext(options))
        {
            var timeProvider = new FixedTimeProvider(FixedNow);
            var service = new RentalReminderBackfillService(db, timeProvider, NullLogger<RentalReminderBackfillService>.Instance);

            // Run 1: creates 1 Handover and 1 Return schedule
            var result1 = await service.BackfillActiveItemsAsync(batchSize: 10);
            Assert.Equal(1, result1.HandoverSchedulesCreated);
            Assert.Equal(1, result1.ReturnSchedulesCreated);

            var schedulesRun1 = await db.RentalReminderSchedules.ToListAsync();
            Assert.Equal(2, schedulesRun1.Count);

            // Run 2: re-run must be idempotent, creating 0 schedules
            var result2 = await service.BackfillActiveItemsAsync(batchSize: 10);
            Assert.Equal(0, result2.HandoverSchedulesCreated);
            Assert.Equal(0, result2.ReturnSchedulesCreated);

            var schedulesRun2 = await db.RentalReminderSchedules.ToListAsync();
            Assert.Equal(2, schedulesRun2.Count);
        }
    }

    [Fact]
    public async Task BackfillActiveItemsAsync_WindowExpiredItem_IsSkipped()
    {
        string dbName = $"Expired_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);
        var itemId = Guid.NewGuid();

        await using (var db = new RentalServiceDbContext(options))
        {
            // ExpectedStartDate is 35 days in past (> D+30 -> WindowExpired)
            var order = CreateTestOrder(Guid.NewGuid(), itemId, RentalItemStatus.AwaitingHandover,
                startDate: DateOnly.FromDateTime(FixedNow.AddDays(-35).DateTime),
                endDate: DateOnly.FromDateTime(FixedNow.AddDays(-30).DateTime));

            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        await using (var db = new RentalServiceDbContext(options))
        {
            var timeProvider = new FixedTimeProvider(FixedNow);
            var service = new RentalReminderBackfillService(db, timeProvider, NullLogger<RentalReminderBackfillService>.Instance);

            var result = await service.BackfillActiveItemsAsync(batchSize: 10);
            Assert.Equal(0, result.HandoverSchedulesCreated);

            var schedules = await db.RentalReminderSchedules.ToListAsync();
            Assert.Empty(schedules);
        }
    }

    [Fact]
    public async Task TryCreateScheduleIfMissingAsync_DuplicateSchedule_ReturnsFalseWithoutThrowing()
    {
        string dbName = $"TryCreate_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);

        await using var db = new RentalServiceDbContext(options);
        var repo = new RentalReminderScheduleRepository(db);

        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var schedule1 = RentalReminderSchedule.Create(
            orderId, itemId, RentalReminderScheduleType.Handover, 1, FixedNow, FixedNow);

        bool first = await repo.TryCreateScheduleIfMissingAsync(schedule1);
        Assert.True(first);

        // Attempt duplicate insert for same (RentalItemId, ScheduleType)
        var schedule2 = RentalReminderSchedule.Create(
            orderId, itemId, RentalReminderScheduleType.Handover, 1, FixedNow.AddHours(1), FixedNow);

        bool second = await repo.TryCreateScheduleIfMissingAsync(schedule2);
        Assert.False(second);

        var schedules = await db.RentalReminderSchedules.ToListAsync();
        Assert.Single(schedules);
    }

    [Fact]
    public async Task ReturnBackfill_ActiveUnitsCalculatedByIdentityAssignment()
    {
        string dbName = $"ActiveUnits_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);
        var itemId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using (var db = new RentalServiceDbContext(options))
        {
            var order = CreateTestOrder(orderId, itemId, RentalItemStatus.InRental,
                startDate: DateOnly.FromDateTime(FixedNow.AddDays(-3).DateTime),
                endDate: DateOnly.FromDateTime(FixedNow.AddDays(2).DateTime), quantity: 3);

            var item = order.Items.First();
            // Mark 1 of 3 assignments as returned
            var returnedUnit = item.UnitAssignments.First();
            typeof(RentalUnitAssignment).GetProperty(nameof(RentalUnitAssignment.ReturnRecordId))!
                .SetValue(returnedUnit, Guid.NewGuid());

            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        await using (var db = new RentalServiceDbContext(options))
        {
            var timeProvider = new FixedTimeProvider(FixedNow);
            var service = new RentalReminderBackfillService(db, timeProvider, NullLogger<RentalReminderBackfillService>.Instance);

            var result = await service.BackfillActiveItemsAsync(batchSize: 10);
            Assert.Equal(1, result.ReturnSchedulesCreated);

            var returnSchedule = await db.RentalReminderSchedules
                .FirstOrDefaultAsync(s => s.RentalItemId == itemId && s.ScheduleType == RentalReminderScheduleType.Return);

            Assert.NotNull(returnSchedule);
            // 3 total units - 1 returned = 2 remaining active units
            Assert.Equal(2, returnSchedule.RemainingUnitCount);
        }
    }

    [Fact]
    public async Task MaintenanceCommand_DryRunMode_DoesNotPersistSchedules()
    {
        string dbName = $"CliDryRun_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);

        await using (var db = new RentalServiceDbContext(options))
        {
            var order = CreateTestOrder(Guid.NewGuid(), Guid.NewGuid(), RentalItemStatus.AwaitingHandover,
                startDate: DateOnly.FromDateTime(FixedNow.AddDays(1).DateTime));
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        var output = new StringWriter();
        int exitCode = await Dprental.RentalService.Maintenance.Program.RunReminderBackfillAsync(options, apply: false, batchSize: 10, output: output);

        Assert.Equal(0, exitCode);
        string text = output.ToString();
        Assert.Contains("Mode: DRY-RUN", text);
        Assert.Contains("Backfill completed", text);

        await using (var db = new RentalServiceDbContext(options))
        {
            var count = await db.RentalReminderSchedules.CountAsync();
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task MaintenanceCommand_ApplyMode_PersistsSchedulesAndPrintsProgress()
    {
        string dbName = $"CliApply_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);
        var sortedIds = GenerateSortedGuids(3);

        await using (var db = new RentalServiceDbContext(options))
        {
            for (int i = 0; i < 3; i++)
            {
                var order = CreateTestOrder(Guid.NewGuid(), sortedIds[i], RentalItemStatus.AwaitingHandover,
                    startDate: DateOnly.FromDateTime(FixedNow.AddDays(1).DateTime));
                db.RentalOrders.Add(order);
            }
            await db.SaveChangesAsync();
        }

        var output = new StringWriter();
        int exitCode = await Dprental.RentalService.Maintenance.Program.RunReminderBackfillAsync(options, apply: true, batchSize: 2, output: output);

        Assert.Equal(0, exitCode);
        string text = output.ToString();
        Assert.Contains("Mode: APPLY", text);
        Assert.Contains("Batch 1: Handover created: 2", text);
        Assert.Contains("Batch 2: Handover created: 1", text);
        Assert.Contains("Backfill completed: 3 Handover schedule(s)", text);

        await using (var db = new RentalServiceDbContext(options))
        {
            var count = await db.RentalReminderSchedules.CountAsync();
            Assert.Equal(3, count);
        }
    }

    [Fact]
    public async Task MaintenanceCommand_WithCheckpoint_ResumesFromCheckpoint()
    {
        string dbName = $"CliCheckpoint_{Guid.NewGuid():N}";
        var options = CreateOptions(dbName);
        var sortedIds = GenerateSortedGuids(3);

        await using (var db = new RentalServiceDbContext(options))
        {
            for (int i = 0; i < 3; i++)
            {
                var order = CreateTestOrder(Guid.NewGuid(), sortedIds[i], RentalItemStatus.AwaitingHandover,
                    startDate: DateOnly.FromDateTime(FixedNow.AddDays(1).DateTime));
                db.RentalOrders.Add(order);
            }
            await db.SaveChangesAsync();
        }

        var output = new StringWriter();
        int exitCode = await Dprental.RentalService.Maintenance.Program.RunReminderBackfillAsync(options, apply: true, batchSize: 10, checkpoint: sortedIds[0], output: output);

        Assert.Equal(0, exitCode);
        string text = output.ToString();
        Assert.Contains("Checkpoint: " + sortedIds[0], text);
        Assert.Contains("Backfill completed: 2 Handover schedule(s)", text);

        await using (var db = new RentalServiceDbContext(options))
        {
            var schedules = await db.RentalReminderSchedules.ToListAsync();
            Assert.Equal(2, schedules.Count);
            Assert.DoesNotContain(schedules, s => s.RentalItemId == sortedIds[0]);
            Assert.Contains(schedules, s => s.RentalItemId == sortedIds[1]);
            Assert.Contains(schedules, s => s.RentalItemId == sortedIds[2]);
        }
    }
}
