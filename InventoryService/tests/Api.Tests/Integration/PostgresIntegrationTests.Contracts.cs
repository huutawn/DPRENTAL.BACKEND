using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    [Fact]
    public async Task ExpiredHoldRehydratesHistoryAndPersistsReleaseAndOutboxAtomically()
    {
        using var services = BuildServiceProvider();
        await using var setup = await CreateMigratedDbContextAsync();
        var now = BusinessDateTime.NormalizeUtc(DateTimeOffset.UtcNow);
        var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, now).Value;
        var start = BusinessDateTime.ToVietnamDate(now).AddDays(1);
        var hold = Reservation.CreateHold(Guid.NewGuid(), equipment.Id, new(start, start), now.AddSeconds(-20), now.AddMinutes(-2),
            "tests", Guid.NewGuid().ToString(), Guid.NewGuid()).Value;
        Assert.True(hold.ExtendHold(now.AddSeconds(-10), now.AddMinutes(-1), operationKey: "extend").IsSuccess);
        setup.Equipments.Add(equipment);
        setup.Reservations.Add(hold);
        await setup.SaveChangesAsync();
        var result = await services.GetRequiredService<IInventoryWriteTransaction>().ExecuteAsync<int>(equipment.Id, async (ctx, ct) =>
        {
            var expired = Assert.Single(await ctx.Reservations.GetExpiredHoldsAsync(equipment.Id, now, ct));
            Assert.Equal(2, expired.Transitions.Count);
            var release = expired.ExpireHold(now, operationKey: "expire");
            if (release.IsFailure) return release.Error;
            ctx.StageOutbox(OutboxMessage.Create(Guid.NewGuid(), "inventory.reservation.events.v1", $"{equipment.OrganizationId}:{expired.Id}", "{}", now));
            return Result.Success(expired.Transitions.Count);
        });
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(3, result.Value);
        await using var verify = await CreateMigratedDbContextAsync();
        var persisted = await verify.Reservations.Include(r => r.Transitions).SingleAsync(r => r.Id == hold.Id);
        Assert.Equal(ReservationStatus.Released, persisted.Status);
        Assert.Equal(Enumerable.Range(1, 3), persisted.Transitions.OrderBy(t => t.Sequence).Select(t => t.Sequence));
        Assert.Single(await verify.OutboxMessages.Where(m => m.KafkaKey == $"{equipment.OrganizationId}:{hold.Id}").ToListAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public async Task CompletedCandidatesAndAvailabilityUseActualReturn(int returnOffset)
    {
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var today = BusinessDateTime.ToVietnamDate(now);
        var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, now).Value;
        var period = new Dprental.InventoryService.Domain.ValueObjects.RentalPeriod(today.AddDays(1), today.AddDays(5));
        var reservation = Reservation.CreateManual(Guid.NewGuid(), equipment.Id, period, "Renter", "0900000000", now).Value;
        var delivered = BusinessDateTime.StartOfDayUtc(period.StartDate).AddHours(3);
        var returnDay = today.AddDays(returnOffset);
        var returned = BusinessDateTime.StartOfDayUtc(returnDay).AddHours(23);
        Assert.True(reservation.Deliver("staff", delivered, null, [Guid.NewGuid()], delivered).IsSuccess);
        Assert.True(reservation.Return("staff", returned, EquipmentReturnCondition.Damaged, new string('x', 1000),
            [Guid.NewGuid()], returned).IsSuccess);
        setup.Equipments.Add(equipment);
        setup.Reservations.Add(reservation);
        await setup.SaveChangesAsync();
        using var services = BuildServiceProvider();
        var repository = services.GetRequiredService<IReservationRepository>();
        var candidates = await repository.GetActiveOrCompletedCandidatesAsync(equipment.Id, returnDay, returnDay);
        var loaded = Assert.Single(candidates);
        Assert.Single(loaded.DeliveryPhotoIds);
        Assert.Single(loaded.ReturnPhotoIds);
        Assert.Equal(EquipmentReturnCondition.Damaged, loaded.ReturnCondition);
        Assert.True(EquipmentAvailabilityPolicy.CheckAvailability(equipment, new(returnDay, returnDay), returned, candidates, []).IsFailure);
        var nextDay = returnDay.AddDays(1);
        var freeCandidates = await repository.GetActiveOrCompletedCandidatesAsync(equipment.Id, nextDay, nextDay);
        Assert.Empty(freeCandidates);
        Assert.True(EquipmentAvailabilityPolicy.CheckAvailability(equipment, new(nextDay, nextDay), returned, freeCandidates, []).IsSuccess);
    }

    [Fact]
    public async Task DamageReportLimitResetsForEachReservationOnSameEquipment()
    {
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var today = BusinessDateTime.ToVietnamDate(now);
        var renter = Guid.NewGuid();
        var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, now).Value;
        Reservation CreateRental(int offset)
        {
            var start = today.AddDays(offset);
            var reservation = Reservation.CreateManual(Guid.NewGuid(), equipment.Id, new(start, start), "Renter", "0900000000", now, renter).Value;
            var delivered = BusinessDateTime.StartOfDayUtc(start).AddHours(1);
            Assert.True(reservation.Deliver("staff", delivered, null, null, delivered).IsSuccess);
            return reservation;
        }
        var first = CreateRental(1);
        var second = CreateRental(3);
        setup.Equipments.Add(equipment);
        setup.Reservations.AddRange(first, second);
        for (var i = 0; i < 5; i++)
            setup.DamageReports.Add(DamageReport.Create(Guid.NewGuid(), first, renter, "Broken equipment", false, null, now, i).Value);
        await setup.SaveChangesAsync();
        using var services = BuildServiceProvider();
        var executor = services.GetRequiredService<IInventoryWriteTransaction>();
        async Task<Result<DamageReport>> SubmitAsync(Guid reservationId) => await executor.ExecuteAsync<DamageReport>(equipment.Id, async (ctx, ct) =>
        {
            var reservation = await ctx.Reservations.GetByIdAsync(reservationId, ct);
            var count = await ctx.Reservations.GetDamageReportsCountForReservationAsync(reservationId, ct);
            var created = DamageReport.Create(Guid.NewGuid(), reservation!, renter, "Broken equipment", false, [Guid.NewGuid()], now, count);
            if (created.IsSuccess) ctx.DamageReports.Add(created.Value);
            return created;
        });
        var rejected = await SubmitAsync(first.Id);
        var accepted = await SubmitAsync(second.Id);
        Assert.Equal("DAMAGE_REPORT_LIMIT_EXCEEDED", rejected.Error.Code);
        Assert.True(accepted.IsSuccess, accepted.Error.Message);
        await using var verify = await CreateMigratedDbContextAsync();
        Assert.Equal(5, await verify.DamageReports.CountAsync(r => r.ReservationId == first.Id));
        Assert.Equal(1, await verify.DamageReports.CountAsync(r => r.ReservationId == second.Id));
    }

    [Fact]
    public async Task WritesToAnotherEquipmentRollbackEvenWhenUsingTransactionProtocol()
    {
        await using var setup = await CreateMigratedDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var lockedId = Guid.NewGuid();
        var other = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, now).Value;
        setup.Equipments.Add(other);
        await setup.SaveChangesAsync();
        var today = BusinessDateTime.ToVietnamDate(now);
        using var services = BuildServiceProvider();
        var result = await services.GetRequiredService<IInventoryWriteTransaction>().ExecuteAsync<Guid>(lockedId, (ctx, ct) =>
        {
            Assert.Equal(lockedId, ctx.EquipmentId);
            var reservation = Reservation.CreateManual(Guid.NewGuid(), other.Id, new(today, today), "Renter", "0900000000", now).Value;
            ctx.Reservations.Add(reservation);
            ctx.StageOutbox(OutboxMessage.Create(Guid.NewGuid(), "inventory.reservation.events.v1", $"{other.OrganizationId}:{reservation.Id}", "{}", now));
            return Task.FromResult(Result.Success(reservation.Id));
        });
        Assert.Equal("EQUIPMENT_LOCK_MISMATCH", result.Error.Code);
        await using var verify = await CreateMigratedDbContextAsync();
        Assert.False(await verify.Reservations.AnyAsync(r => r.EquipmentId == other.Id));
    }

    [Fact]
    public async Task NestedTransactionFailsWithoutWaitingForItsOwnLock()
    {
        using var services = BuildServiceProvider();
        var executor = services.GetRequiredService<IInventoryWriteTransaction>();
        var equipmentId = Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await executor.ExecuteAsync<int>(equipmentId, async (ctx, ct) =>
            await executor.ExecuteAsync<int>(equipmentId, (inner, token) => Task.FromResult(Result.Success(1)), ct), deadline.Token);
        Assert.Equal("NESTED_INVENTORY_TRANSACTION", result.Error.Code);
    }

    [Fact]
    public async Task CommandFingerprintSurvivesRoundtripAndRejectsChangedPayload()
    {
        var now = DateTimeOffset.UtcNow;
        var start = BusinessDateTime.ToVietnamDate(now).AddDays(2);
        var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, now).Value;
        var reservation = Reservation.CreateHold(Guid.NewGuid(), equipment.Id, new(start, start), now.AddHours(1), now,
            "tests", Guid.NewGuid().ToString(), Guid.NewGuid()).Value;
        Assert.True(reservation.ExtendHold(now.AddHours(2), now, "service", "extend").IsSuccess);
        await using var setup = await CreateMigratedDbContextAsync();
        setup.Equipments.Add(equipment);
        setup.Reservations.Add(reservation);
        await setup.SaveChangesAsync();
        using var services = BuildServiceProvider();
        var executor = services.GetRequiredService<IInventoryWriteTransaction>();
        async Task<Result<int>> ExtendAsync(DateTimeOffset requested) => await executor.ExecuteAsync<int>(equipment.Id, async (ctx, ct) =>
        {
            var loaded = await ctx.Reservations.GetByIdAsync(reservation.Id, ct);
            var operation = loaded!.ExtendHold(requested, now, "service", "extend");
            return operation.IsSuccess ? Result.Success(loaded.Transitions.Count) : operation.Error;
        });
        var retry = await ExtendAsync(now.AddHours(2).ToOffset(TimeSpan.FromHours(7)));
        Assert.True(retry.IsSuccess, retry.Error.Message);
        Assert.Equal(2, retry.Value);
        var changed = await ExtendAsync(now.AddHours(3));
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", changed.Error.Code);
    }
}
