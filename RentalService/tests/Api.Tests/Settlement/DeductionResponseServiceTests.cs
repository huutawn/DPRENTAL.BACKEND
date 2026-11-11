using Dprental.RentalService.Api.Tests.TestHelpers;
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Settlement;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        // DPR-199: Ä‘Æ¡n cá»§a renter khÃ¡c tráº£ nhÆ° khÃ´ng tá»“n táº¡i Ä‘á»ƒ khÃ´ng lá»™ dá»¯ liá»‡u.
        Assert.Equal(Dprental.RentalService.Common.ErrorType.NotFound, foreign.Error.Type);
    }

    [Fact]
    public async Task SupplierOfferAndRenterAgreement_PersistOnceWithoutAdmin()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        var events = new FakeRentalEventProducer();
        var service = NewService(db, eventProducer: events);
        Assert.True((await service.RespondAsync(item.Id, Caller(order.RenterId),
            new RespondDeductionRequest(RenterDeductionDecision.Dispute, "Disagree", null),
            "dispute-key")).IsSuccess);
        var opened = Assert.Single(events.DisputeOpenedEvents);
        Assert.Equal(item.DeductionProposal!.Dispute!.Id, opened.DisputeId);
        Assert.Equal(order.RenterId, opened.RenterId);
        Assert.True((await service.RespondAsync(item.Id, Caller(order.RenterId),
            new RespondDeductionRequest(RenterDeductionDecision.Dispute, "Disagree", null),
            "dispute-key")).IsSuccess);
        Assert.Single(events.DisputeOpenedEvents);
        var request = new OfferDisputeAmountRequest(50_000m);
        Assert.True((await service.OfferDisputeAmountAsync(item.Id, Caller(Guid.NewGuid()),
            request, "foreign-offer")).IsFailure);
        var offered = await service.OfferDisputeAmountAsync(item.Id, Caller(item.SupplierId),
            request, "offer-key");
        Assert.True(offered.IsSuccess, offered.Error.Message);
        Assert.True((await service.OfferDisputeAmountAsync(item.Id, Caller(item.SupplierId),
            request, "offer-key")).IsSuccess);
        Assert.True((await service.OfferDisputeAmountAsync(item.Id, Caller(item.SupplierId),
            new OfferDisputeAmountRequest(40_000m), "offer-key")).IsFailure);
        Assert.True((await service.RespondAsync(item.Id, Caller(order.RenterId),
            new RespondDeductionRequest(RenterDeductionDecision.Accept, null, null),
            "accept-key")).IsSuccess);
        Assert.Equal(50_000m, item.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.Single(order.OperationReceipts, r => r.Action == "SupplierDisputeOffer");
    }

    [Fact]
    public async Task SupplierOffer_SignalRFailure_DoesNotUndoSavedOffer()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        Assert.True((await NewService(db).RespondAsync(item.Id, Caller(order.RenterId),
            new RespondDeductionRequest(RenterDeductionDecision.Dispute, "Disagree", null),
            "dispute-key")).IsSuccess);
        var notifier = new Mock<IOrderHubNotifier>();
        notifier.Setup(x => x.NotifyRenterOrderChangedAsync(It.IsAny<Guid>(),
                It.IsAny<RenterOrderChangedNotification>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SignalR unavailable"));

        var result = await NewService(db, notifier: notifier.Object).OfferDisputeAmountAsync(
            item.Id, Caller(item.SupplierId), new OfferDisputeAmountRequest(50_000m), "offer-key");

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(50_000m, item.DeductionProposal!.Dispute!.SupplierOfferAmount);
    }

    [Fact]
    public async Task AdminDisputeDetail_ShowsRenterReturnEvidence_WithoutGrantingRenterAdminAccess()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        Guid mediaId = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();
        item.AddReturnRequest(new ReturnRequest(requestId, item.Id, Now, null,
            order.RenterId, Now, [new ReturnRequestUnit(Guid.NewGuid(), requestId, unit.Id, [mediaId])]));
        await db.SaveChangesAsync();
        var service = NewService(db);
        Assert.True((await service.RespondAsync(item.Id, Caller(order.RenterId),
            new RespondDeductionRequest(RenterDeductionDecision.Dispute, "Disagree", null),
            "dispute-key")).IsSuccess);

        Assert.True((await service.GetDisputeDetailAsync(item.Id, Caller(order.RenterId))).IsFailure);
        var detail = await service.GetDisputeDetailAsync(item.Id, Caller(Guid.NewGuid(), admin: true));
        Assert.True(detail.IsSuccess, detail.Error.Message);
        Assert.Contains(mediaId, Assert.Single(detail.Value.Units).RenterReturnMediaIds);
        Assert.Equal(item.DeductionProposal!.TotalProposedAmount,
            detail.Value.CurrentSupplierOfferAmount);
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
    public async Task DisputeHoldWorker_FinalizesProposedAmountOnceAfterSevenDays()
    {
        await using var db = NewDb();
        var order = SeedProposed(db);
        var item = order.Items.Single();
        Assert.True((await NewService(db).RespondAsync(item.Id, Caller(order.RenterId),
            new RespondDeductionRequest(RenterDeductionDecision.Dispute, "Disagree", null),
            "dispute-key")).IsSuccess);

        var before = NewService(db, item.ReturnsCompletedAt!.Value.AddDays(7).AddTicks(-1));
        Assert.Equal(0, await before.ProcessDisputeHoldTimeoutsAsync());
        Assert.Null(item.FinalDeductionAmount);

        var after = NewService(db, item.ReturnsCompletedAt.Value.AddDays(7));
        Assert.Equal(1, await after.ProcessDisputeHoldTimeoutsAsync());
        Assert.Equal(0, await after.ProcessDisputeHoldTimeoutsAsync());
        Assert.Equal(item.DeductionProposal!.TotalProposedAmount, item.FinalDeductionAmount);
        Assert.Equal(item.DepositAmount - item.FinalDeductionAmount, item.DepositRefundAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
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
            Mock.Of<IRentalMediaClient>(), new FakeTimeProvider(Now.AddHours(2)), new FakeRentalEventProducer());

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
        ReturnTestDataSeeder.SeedReturn(item, item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId);
        Assert.True(item.SubmitDeduction(item.SupplierId, 100_000m, "Repair", [Guid.NewGuid()],
            [unit.AssetId], Now).IsSuccess);
        db.Add(order);
        db.SaveChanges();
        return order;
    }

    private static DeductionResponseService NewService(
        RentalServiceDbContext db, DateTimeOffset? now = null, IOrderHubNotifier? notifier = null,
        FakeRentalEventProducer? eventProducer = null)
    {
        return new DeductionResponseService(new RentalOrderRepository(db), NewLocks(), db,
            Mock.Of<IRentalMediaClient>(), new FakeTimeProvider(now ?? Now.AddHours(1)),
            new ConfigurationBuilder().Build(), NullLogger<DeductionResponseService>.Instance,
            eventProducer ?? new FakeRentalEventProducer(), notifier);
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
