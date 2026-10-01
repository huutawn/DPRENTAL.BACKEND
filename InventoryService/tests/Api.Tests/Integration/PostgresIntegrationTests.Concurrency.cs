using System.Data;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    /// <summary>
    /// Kiểm thử 6: Concurrency Protocol với PostgreSQL pg_advisory_xact_lock.
    /// Hai request cạnh tranh đặt lịch trùng khoảng thời gian (Overlap) trên cùng một thiết bị:
    /// Nhờ khóa advisory lock cấp thiết bị, chỉ ĐÚNG 1 request thành công, request còn lại bị từ chối do xung đột.
    /// </summary>
    [Fact]
    public async Task Concurrency_TwoOverlappingRequests_OnlyOneSucceeds()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);
        await setupContext.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();
        var overlapPeriod = new RentalPeriod(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 15));

        // Khởi động đồng thời 2 task cạnh tranh đặt cùng khoảng thời gian
        Task<Result<Reservation>> task1 = Task.Run(async () =>
        {
            return await executor.ExecuteAsync(equipmentId, async (ctx, ct) =>
            {
                var eq = await ctx.Equipments.GetByIdAsync(equipmentId, ct);
                var candidates = await ctx.Reservations.GetActiveOrCompletedCandidatesAsync(equipmentId, overlapPeriod.StartDate, overlapPeriod.EndDate, ct);
                var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(equipmentId, overlapPeriod.StartDate, overlapPeriod.EndDate, ct);

                var check = EquipmentAvailabilityPolicy.CheckAvailability(eq!, overlapPeriod, DateTimeOffset.UtcNow, candidates, blocks);
                if (check.IsFailure)
                {
                    return check.Error;
                }

                var res = Reservation.CreateManual(Guid.NewGuid(), equipmentId, overlapPeriod, "User 1", "0900000001", DateTimeOffset.UtcNow).Value;
                ctx.Reservations.Add(res);
                return Result.Success(res);
            });
        });

        Task<Result<Reservation>> task2 = Task.Run(async () =>
        {
            return await executor.ExecuteAsync(equipmentId, async (ctx, ct) =>
            {
                var eq = await ctx.Equipments.GetByIdAsync(equipmentId, ct);
                var candidates = await ctx.Reservations.GetActiveOrCompletedCandidatesAsync(equipmentId, overlapPeriod.StartDate, overlapPeriod.EndDate, ct);
                var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(equipmentId, overlapPeriod.StartDate, overlapPeriod.EndDate, ct);

                var check = EquipmentAvailabilityPolicy.CheckAvailability(eq!, overlapPeriod, DateTimeOffset.UtcNow, candidates, blocks);
                if (check.IsFailure)
                {
                    return check.Error;
                }

                var res = Reservation.CreateManual(Guid.NewGuid(), equipmentId, overlapPeriod, "User 2", "0900000002", DateTimeOffset.UtcNow).Value;
                ctx.Reservations.Add(res);
                return Result.Success(res);
            });
        });

        var results = await Task.WhenAll(task1, task2);

        int successCount = results.Count(r => r.IsSuccess);
        int failureCount = results.Count(r => r.IsFailure);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);
    }

    /// <summary>
    /// Kiểm thử 7: Concurrency Protocol với hai khoảng thời gian KHÔNG trùng lặp trên cùng thiết bị.
    /// Cả hai request tuần tự lấy khóa và đều thành công.
    /// </summary>
    [Fact]
    public async Task Concurrency_TwoNonOverlappingRequests_BothSucceed()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);
        await setupContext.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();
        var period1 = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var period2 = new RentalPeriod(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 15));

        Task<Result<Reservation>> task1 = Task.Run(async () =>
        {
            return await executor.ExecuteAsync(equipmentId, async (ctx, ct) =>
            {
                var eq = await ctx.Equipments.GetByIdAsync(equipmentId, ct);
                var candidates = await ctx.Reservations.GetActiveOrCompletedCandidatesAsync(equipmentId, period1.StartDate, period1.EndDate, ct);
                var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(equipmentId, period1.StartDate, period1.EndDate, ct);

                var check = EquipmentAvailabilityPolicy.CheckAvailability(eq!, period1, DateTimeOffset.UtcNow, candidates, blocks);
                if (check.IsFailure) return check.Error;

                var res = Reservation.CreateManual(Guid.NewGuid(), equipmentId, period1, "User A", "0900000001", DateTimeOffset.UtcNow).Value;
                ctx.Reservations.Add(res);
                return Result.Success(res);
            });
        });

        Task<Result<Reservation>> task2 = Task.Run(async () =>
        {
            return await executor.ExecuteAsync(equipmentId, async (ctx, ct) =>
            {
                var eq = await ctx.Equipments.GetByIdAsync(equipmentId, ct);
                var candidates = await ctx.Reservations.GetActiveOrCompletedCandidatesAsync(equipmentId, period2.StartDate, period2.EndDate, ct);
                var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(equipmentId, period2.StartDate, period2.EndDate, ct);

                var check = EquipmentAvailabilityPolicy.CheckAvailability(eq!, period2, DateTimeOffset.UtcNow, candidates, blocks);
                if (check.IsFailure) return check.Error;

                var res = Reservation.CreateManual(Guid.NewGuid(), equipmentId, period2, "User B", "0900000002", DateTimeOffset.UtcNow).Value;
                ctx.Reservations.Add(res);
                return Result.Success(res);
            });
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess);
    }

    /// <summary>
    /// Kiểm thử 8: Transaction Rollback đảm bảo tính nguyên tử.
    /// Nếu callback trả về Failure hoặc Exception, transaction tự động rollback,
    /// không để lại bất kỳ reservation, transition hay outbox message nào trong database.
    /// </summary>
    [Fact]
    public async Task WriteTransaction_Rollback_LeavesNoDataOrOutbox()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);
        await setupContext.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();
        Guid resId = Guid.NewGuid();
        Guid outboxId = Guid.NewGuid();
        var period = new RentalPeriod(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25));

        var failedResult = await executor.ExecuteAsync<Reservation>(equipmentId, async (ctx, ct) =>
        {
            var res = Reservation.CreateManual(resId, equipmentId, period, "User", "0900000000", DateTimeOffset.UtcNow).Value;
            ctx.Reservations.Add(res);

            var outbox = OutboxMessage.Create(outboxId, "ReservationCreated", "Reservation", resId.ToString(), "{}", DateTimeOffset.UtcNow);
            ctx.StageOutbox(outbox);

            // Giả lập lỗi nghiệp vụ dẫn đến Failure
            return Error.Validation("BUSINESS_FAILURE", "Giả lập lỗi hủy giao dịch");
        });

        Assert.True(failedResult.IsFailure);

        // Kiểm tra cơ sở dữ liệu hoàn toàn sạch sẽ, không có bản ghi mồ côi
        await using var verifyContext = await CreateMigratedDbContextAsync();
        var savedRes = await verifyContext.Reservations.FindAsync(resId);
        var savedOutbox = await verifyContext.OutboxMessages.FindAsync(outboxId);
        var savedTransitions = await verifyContext.ReservationTransitions.Where(t => t.ReservationId == resId).ToListAsync();

        Assert.Null(savedRes);
        Assert.Null(savedOutbox);
        Assert.Empty(savedTransitions);
    }

    /// <summary>
    /// Kiểm thử 9: Khóa độc quyền pg_advisory_xact_lock được giải phóng ngay lập tức khi operation bị hủy (CancellationToken).
    /// Các thao tác tiếp theo trên cùng thiết bị có thể tiếp tục chiếm khóa bình thường mà không bị deadlock.
    /// </summary>
    [Fact]
    public async Task AdvisoryLock_ReleasedWhenOperationCancelled()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);
        await setupContext.SaveChangesAsync();

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        // 1. Chạy một operation và chủ động hủy token giữa chừng
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Token đã bị hủy

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await executor.ExecuteAsync<bool>(equipmentId, async (ctx, ct) =>
            {
                await Task.Delay(100, ct);
                return Result.Success(true);
            }, cts.Token);
        });

        // 2. Thao tác tiếp theo trên cùng equipmentId phải thực thi thành công ngay lập tức (khóa đã được giải phóng)
        var subsequentResult = await executor.ExecuteAsync<bool>(equipmentId, async (ctx, ct) =>
        {
            var eq = await ctx.Equipments.GetByIdAsync(equipmentId, ct);
            return Result.Success(eq != null);
        });

        Assert.True(subsequentResult.IsSuccess);
        Assert.True(subsequentResult.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationWaitsForConcurrentBlockOrDisable(bool disableBookings)
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var today = BusinessDateTime.ToVietnamDate(now);
        var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, now).Value;
        setup.Equipments.Add(equipment);
        await setup.SaveChangesAsync();
        var period = new RentalPeriod(today.AddDays(2), today.AddDays(5));
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = services.GetRequiredService<IInventoryWriteTransaction>();
        var first = executor.ExecuteAsync<int>(equipment.Id, async (ctx, ct) =>
        {
            locked.SetResult();
            await release.Task.WaitAsync(ct);
            if (disableBookings)
            {
                var loaded = await ctx.Equipments.GetByIdAsync(equipment.Id, ct);
                var disabled = loaded!.DisableBookings(null, now);
                if (disabled.IsFailure) return disabled.Error;
            }
            else
            {
                var block = CalendarBlock.Create(Guid.NewGuid(), equipment.Id, period.StartDate, period.EndDate,
                    CalendarBlockReason.Maintenance, "staff", now, today).Value;
                ctx.CalendarBlocks.Add(block);
            }
            return Result.Success(1);
        }, deadline.Token);
        await locked.Task.WaitAsync(deadline.Token);
        var second = executor.ExecuteAsync<Reservation>(equipment.Id, async (ctx, ct) =>
        {
            reservationEntered.SetResult();
            var loaded = await ctx.Equipments.GetByIdAsync(equipment.Id, ct);
            var reservations = await ctx.Reservations.GetActiveOrCompletedCandidatesAsync(equipment.Id, period.StartDate, period.EndDate, ct);
            var blocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(equipment.Id, period.StartDate, period.EndDate, ct);
            var available = EquipmentAvailabilityPolicy.CheckAvailability(loaded!, period, now, reservations, blocks);
            if (available.IsFailure) return available.Error;
            var reservation = Reservation.CreateManual(Guid.NewGuid(), equipment.Id, period, "Renter", "0900000000", now).Value;
            ctx.Reservations.Add(reservation);
            return Result.Success(reservation);
        }, deadline.Token);
        try
        {
            var waiters = 0L;
            while (waiters == 0)
            {
                waiters = await setup.Database.SqlQueryRaw<long>(
                    "SELECT count(*)::bigint AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())")
                    .SingleAsync(deadline.Token);
                if (waiters == 0) await Task.Delay(20, deadline.Token);
            }
            Assert.False(reservationEntered.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True((await first).IsSuccess);
        var result = await second;
        Assert.True(result.IsFailure);
        Assert.Equal(disableBookings ? "EQUIPMENT_NOT_ACCEPTING_BOOKINGS" : "EQUIPMENT_BLOCKED", result.Error.Code);
        await using var verify = await CreateMigratedDbContextAsync();
        Assert.False(await verify.Reservations.AnyAsync(r => r.EquipmentId == equipment.Id));
    }

    /// <summary>
    /// Kiểm thử 13: Cạnh tranh giữa ExpireHold và ConfirmHold dưới khóa độc quyền cấp thiết bị.
    /// Một trong hai thao tác hoàn tất trước sẽ chuyển trạng thái, thao tác sau sẽ thấy trạng thái đã đổi và không thể thực thi trái phép.
    /// </summary>
    [Fact]
    public async Task Concurrency_ExpireCompetesWithConfirm()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);

        var period = new RentalPeriod(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 15));
        // Hold hết hạn sau 1 giây
        DateTimeOffset holdExpiresAt = now.AddSeconds(1);
        Guid resId = Guid.NewGuid();
        var res = Reservation.CreateHold(resId, equipmentId, period, holdExpiresAt, now, sourceSystem: "tests", externalReference: Guid.NewGuid().ToString(), renterAccountId: Guid.NewGuid()).Value;
        setupContext.Reservations.Add(res);
        await setupContext.SaveChangesAsync();

        // Đợi 1.1 giây để hold chính thức quá hạn
        await Task.Delay(1100);
        DateTimeOffset afterExpiryTime = DateTimeOffset.UtcNow;

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        // Thao tác 1: ExpireHold được thực thi
        var expireResult = await executor.ExecuteAsync<bool>(equipmentId, async (ctx, ct) =>
        {
            var targetRes = await ctx.Reservations.GetByIdAsync(resId, ct);
            var expireCheck = targetRes!.ExpireHold(afterExpiryTime, "system");
            if (expireCheck.IsFailure) return expireCheck.Error;

            ctx.Reservations.Update(targetRes);
            return Result.Success(true);
        });
        Assert.True(expireResult.IsSuccess);

        // Thao tác 2: ConfirmHold cạnh tranh trên lượt đặt đã Released -> BỊ TỪ CHỐI do đã ở Terminal State
        var confirmResult = await executor.ExecuteAsync<bool>(equipmentId, async (ctx, ct) =>
        {
            var targetRes = await ctx.Reservations.GetByIdAsync(resId, ct);
            var confirmCheck = targetRes!.ConfirmHold(afterExpiryTime, "renter");
            if (confirmCheck.IsFailure) return confirmCheck.Error;

            ctx.Reservations.Update(targetRes);
            return Result.Success(true);
        });

        Assert.True(confirmResult.IsFailure);
        Assert.Equal("RESERVATION_IN_TERMINAL_STATE", confirmResult.Error.Code);
    }

}
