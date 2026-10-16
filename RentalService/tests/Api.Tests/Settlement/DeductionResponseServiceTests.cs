using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Settlement;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class DeductionResponseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RenterAcceptPersistsOnce_AndDifferentRenterIsForbidden()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        var service = NewService(db);
        var request = new RespondDeductionRequest(RenterDeductionDecision.Accept, null, null);
        var renter = Caller(order.RenterId);

        var first = await service.RespondAsync(item.Id, renter, request, "accept-key");
        Assert.True(first.IsSuccess, first.Error.Message);
        var replay = await service.RespondAsync(item.Id, renter, request, "accept-key");
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(item.DeductionProposal!.TotalProposedAmount, item.FinalDeductionAmount);
        Assert.Single(order.OperationReceipts, r => r.Action == "RenterRespondDeduction");
        var foreign = await service.RespondAsync(item.Id, Caller(Guid.NewGuid()), request, "foreign-key");
        Assert.True(foreign.IsFailure);
        // DPR-199: đơn của renter khác trả như không tồn tại để không lộ dữ liệu.
        Assert.Equal(Dprental.RentalService.Common.ErrorType.NotFound, foreign.Error.Type);
    }

    [Fact]
    public async Task AdminResolutionPersistsDecision_AndRenterCannotResolve()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        var service = NewService(db);
        var dispute = await service.RespondAsync(item.Id, Caller(order.RenterId),
            new RespondDeductionRequest(RenterDeductionDecision.Dispute, "Disagree", null), "dispute-key");
        Assert.True(dispute.IsSuccess, dispute.Error.Message);
        var request = new ResolveDisputeRequest(0m, "Evidence does not support deduction", null);
        Assert.True((await service.ResolveDisputeAsync(item.Id, Caller(order.RenterId), request, "resolve-key")).IsFailure);
        var admin = Caller(Guid.NewGuid(), admin: true);
        var result = await service.ResolveDisputeAsync(item.Id, admin, request, "resolve-key");
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(0m, item.FinalDeductionAmount);
        Assert.Equal(DisputeStatus.Resolved, item.DeductionProposal!.Dispute!.Status);
        Assert.True((await service.ResolveDisputeAsync(item.Id, admin, request, "resolve-key")).IsSuccess);
        Assert.Single(order.OperationReceipts, r => r.Action == "AdminResolveDispute");
    }

    [Fact]
    public async Task RenterDeadlineWorkerAutoAcceptsOnlyOnce()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        var service = NewService(db, item.DeductionProposal!.RenterResponseDeadline.AddTicks(1));
        Assert.Equal(1, await service.ProcessRenterResponseTimeoutsAsync());
        Assert.Equal(0, await service.ProcessRenterResponseTimeoutsAsync());
        Assert.Equal(item.DeductionProposal.TotalProposedAmount, item.FinalDeductionAmount);
        Assert.Equal(DeductionProposalStatus.AcceptedByTimeout, item.DeductionProposal.Status);
    }

    [Fact]
    public async Task SettlementSchedulerCreatesOnePendingPaymentOperationAfterInventoryClosure()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        Assert.True(item.AcceptDeductionByRenter(order.RenterId, Now.AddHours(1), "accept").IsSuccess);
        Assert.True(item.ConfirmDamagedUnitBlocked(unit.Id, "maintenance-1").IsSuccess);
        Assert.True(item.ConfirmReturnedUnitClosure(unit.Id, Now.AddHours(1), "maintenance-1",
            "inventory-1", Now.AddHours(1)).IsSuccess);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.InvoiceRef))!.SetValue(order, "invoice-1");
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.PaymentRef))!.SetValue(order, "payment-1");
        await db.SaveChangesAsync();
        var service = new DeductionService(new RentalOrderRepository(db), NewLocks(), db,
            Mock.Of<IRentalMediaClient>(), new FakeTimeProvider(Now.AddHours(2)));

        Assert.Equal(1, await service.ScheduleReadySettlementsAsync());
        Assert.Equal(0, await service.ScheduleReadySettlementsAsync());
        var receipt = Assert.Single(order.OperationReceipts, r => r.Action == RentalItem.SettleRentalAction);
        Assert.Equal(RentalOperationReceipt.StatusPending, receipt.Status);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
    }

    private static RentalServiceDbContext NewDb() => new(new DbContextOptionsBuilder<RentalServiceDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static RentalOrder SeedProposed(RentalServiceDbContext db)
    {
        var order = Returns.ReturnTests.NewOrder(1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        Assert.True(item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId).IsSuccess);
        Assert.True(item.SubmitDeduction(item.SupplierId, 100_000m, "Repair", [Guid.NewGuid()],
            [unit.AssetId], Now).IsSuccess);
        db.Add(order);
        db.SaveChanges();
        return order;
    }

    private static DeductionResponseService NewService(RentalServiceDbContext db, DateTimeOffset? now = null)
    {
        return new DeductionResponseService(new RentalOrderRepository(db), NewLocks(), db,
            Mock.Of<IRentalMediaClient>(), new FakeTimeProvider(now ?? Now.AddHours(1)),
            new ConfigurationBuilder().Build(), NullLogger<DeductionResponseService>.Instance);
    }

    private static IOrderLockManager NewLocks()
    {
        var locks = new Mock<IOrderLockManager>();
        locks.Setup(x => x.AcquireOrderLockAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NoopLock());
        return locks.Object;
    }

    private static RentalCallerContext Caller(Guid id, bool admin = false) =>
        RentalCallerContext.FromClaims(admin
            ? [new("sub", id.ToString()), new("r", "adm")]
            : [new("sub", id.ToString())]);

    private sealed class NoopLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
