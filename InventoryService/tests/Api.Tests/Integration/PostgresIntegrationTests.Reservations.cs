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
    /// Kiểm thử 14: Xử lý Idempotency theo ExternalReference:
    /// - Cùng source/reference và payload tương đương: trả về reservation đã tồn tại (kể cả khi đã terminal).
    /// - Cùng source/reference nhưng payload khác (khác thiết bị hoặc ngày thuê): trả về Conflict.
    /// </summary>
    [Fact]
    public async Task ExternalReference_PayloadEquivalenceAndConflict()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);
        await setupContext.SaveChangesAsync();

        const string sourceSystem = "external_web";
        string externalRef = $"REF_{Guid.NewGuid():N}";
        var period = new RentalPeriod(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 25));

        var executor = sp.GetRequiredService<IInventoryWriteTransaction>();

        // 1. Tạo reservation ban đầu
        Guid originalResId = Guid.NewGuid();
        var initialResult = await executor.ExecuteAsync<Reservation>(equipmentId, async (ctx, ct) =>
        {
            var newRes = Reservation.CreateHold(
                originalResId, equipmentId, period, now.AddHours(5), now, sourceSystem, externalRef, renterAccountId: Guid.NewGuid()).Value;
            ctx.Reservations.Add(newRes);
            return Result.Success(newRes);
        });
        Assert.True(initialResult.IsSuccess);

        async Task<Result<Reservation>> RetryAsync(RentalPeriod requestedPeriod) =>
            await executor.ExecuteAsync<Reservation>(equipmentId, async (ctx, ct) =>
            {
                var existing = await ctx.Reservations.GetByExternalReferenceAsync(sourceSystem, externalRef, ct);
                Assert.NotNull(existing);
                var retryCheck = existing.ValidateExternalRetry(equipmentId, requestedPeriod,
                    initialResult.Value.RenterAccountId!.Value, now.AddHours(5));
                return retryCheck.IsSuccess ? Result.Success(existing) : retryCheck.Error;
            });
        var equivalent = await RetryAsync(period);
        Assert.True(equivalent.IsSuccess);
        Assert.Equal(originalResId, equivalent.Value.Id);
        var different = await RetryAsync(new RentalPeriod(period.StartDate.AddDays(1), period.EndDate));
        Assert.True(different.IsFailure);
        Assert.Equal("EXTERNAL_REFERENCE_REUSED", different.Error.Code);
        await using var verify = await CreateMigratedDbContextAsync();
        Assert.Equal(1, await verify.Reservations.CountAsync(r => r.EquipmentId == equipmentId));

    }

    /// <summary>
    /// Kiểm thử 15: Retry thao tác sau khi reservation đã đạt trạng thái kết thúc (Terminal State).
    /// Mọi chuyển đổi trạng thái tiếp theo đều bị từ chối nghiêm ngặt.
    /// </summary>
    [Fact]
    public async Task TerminalState_RejectsFurtherTransitions()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);

        var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        Guid resId = Guid.NewGuid();
        var res = Reservation.CreateManual(resId, equipmentId, period, "User", "0900000000", now).Value;
        res.Cancel("Khách hủy đơn trước giao", now, "operator");
        setupContext.Reservations.Add(res);
        await setupContext.SaveChangesAsync();

        Assert.True(res.IsTerminalState());

        // Thử giao hàng trên đơn đã Cancelled -> BỊ TỪ CHỐI
        var deliverCheck = res.Deliver("staff", now, "note", null, now);
        Assert.True(deliverCheck.IsFailure);
        Assert.Equal("RESERVATION_IN_TERMINAL_STATE", deliverCheck.Error.Code);

        // Thử xác nhận trên đơn đã Cancelled -> BỊ TỪ CHỐI
        var confirmCheck = res.ConfirmHold(now, "renter");
        Assert.True(confirmCheck.IsFailure);
        Assert.Equal("RESERVATION_IN_TERMINAL_STATE", confirmCheck.Error.Code);
    }

    /// <summary>
    /// Kiểm thử 16: Kiểm tra tính khả dụng với reservation Completed trên PostgreSQL:
    /// Thu hồi ngày D cho đặt lại từ D+1. Ngày D vẫn là ngày bận dù đã Completed.
    /// </summary>
    [Fact]
    public async Task CompletedReservation_DayDRemainsBusy_InPostgresAvailability()
    {
        using var sp = BuildServiceProvider();
        await using var setupContext = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        setupContext.Equipments.Add(
            InventoryEquipment.Create(equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value);

        DateOnly dayD = new(2026, 11, 10);
        var completedPeriod = new RentalPeriod(new DateOnly(2026, 11, 5), dayD);
        var completedRes = Reservation.CreateManual(Guid.NewGuid(), equipmentId, completedPeriod, "User", "0900000000", now).Value;
        var deliveredAt = new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero);
        var returnedAt = new DateTimeOffset(2026, 11, 10, 3, 0, 0, TimeSpan.Zero);
        Assert.True(completedRes.Deliver("staff", deliveredAt, "ok", null, deliveredAt).IsSuccess);
        Assert.True(completedRes.Return("staff", returnedAt, EquipmentReturnCondition.Intact, "ok", null, returnedAt).IsSuccess);
        setupContext.Reservations.Add(completedRes);
        await setupContext.SaveChangesAsync();

        var resRepo = sp.GetRequiredService<IReservationRepository>();
        var candidates = await resRepo.GetActiveOrCompletedCandidatesAsync(
            equipmentId, dayD, dayD.AddDays(5));

        // Lượt Completed phải xuất hiện trong danh sách ứng viên
        Assert.Contains(candidates, r => r.Id == completedRes.Id);

        var eq = (await setupContext.Equipments.FindAsync(equipmentId))!;

        // Đặt bao gồm ngày D -> Thất bại
        var candidateOverlapD = new RentalPeriod(dayD, dayD.AddDays(3));
        var checkOverlapD = EquipmentAvailabilityPolicy.CheckAvailability(eq, candidateOverlapD, returnedAt, candidates, []);
        Assert.True(checkOverlapD.IsFailure);
        Assert.Equal("EQUIPMENT_UNAVAILABLE", checkOverlapD.Error.Code);

        // Đặt từ ngày D+1 -> Thành công
        var candidateDPlusOne = new RentalPeriod(dayD.AddDays(1), dayD.AddDays(4));
        var checkDPlusOne = EquipmentAvailabilityPolicy.CheckAvailability(eq, candidateDPlusOne, returnedAt, candidates, []);
        Assert.True(checkDPlusOne.IsSuccess);
    }
}
