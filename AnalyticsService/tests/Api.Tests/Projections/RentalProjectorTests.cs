using Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;
using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Projections;

public sealed class RentalProjectorTests
{
    private static readonly Guid OrderId = Guid.Parse("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d");
    private static readonly Guid ItemId = Guid.Parse("c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f");
    private static readonly Guid SupplierId = Guid.Parse("e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f90a1");

    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PaidAt = new(2026, 11, 10, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EventsInOrder_FillEveryTimestampAndFinalStatus()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Created(version: 1)));
        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Paid(version: 2)));
        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Completed(version: 5)));

        RentalOrderFact order = await h.Context.RentalOrderFacts.SingleAsync();
        Assert.Equal(CreatedAt, order.CreatedAt);
        Assert.Equal(PaidAt, order.PaidAt);
        Assert.Equal(CompletedAt, order.CompletedAt);
        Assert.Equal(OrderFactStatus.Completed, order.Status);
        Assert.Equal(5, order.LastVersion);
        Assert.Equal(900_000, order.RentalAmount);

        RentalItemFact item = await h.Context.RentalItemFacts.SingleAsync();
        Assert.Equal(PaidAt, item.PaidAt);
        Assert.Equal("Máy đầm cóc", item.EquipmentTypeName);
    }

    [Fact]
    public async Task OldEventArrivingLate_DoesNotRollBackStatus_ButStillFillsItsTimestamp()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Completed(version: 5)));
        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Paid(version: 2))); // đến muộn

        RentalOrderFact order = await h.Context.RentalOrderFacts.SingleAsync();
        Assert.Equal(OrderFactStatus.Completed, order.Status); // không lùi về Paid
        Assert.Equal(5, order.LastVersion);
        Assert.Equal(PaidAt, order.PaidAt);                    // nhưng PaidAt vẫn được điền
    }

    [Fact]
    public async Task PaidBeforeCreated_StubRowIsCompletedAndItemsGetOrderPaidAt()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Paid(version: 2)));  // tạo dòng tạm
        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Created(version: 1))); // đến muộn

        RentalOrderFact order = await h.Context.RentalOrderFacts.SingleAsync();
        Assert.Equal("RNT-20261110-0001", order.OrderCode); // dòng tạm được điền nốt
        Assert.Equal(900_000, order.RentalAmount);
        Assert.Equal(OrderFactStatus.Paid, order.Status);   // Created(v1) không kéo lùi Paid(v2)

        RentalItemFact item = await h.Context.RentalItemFacts.SingleAsync();
        Assert.Equal(PaidAt, item.PaidAt);                   // item nhận PaidAt của đơn
        Assert.Equal(ItemFactStatus.PendingSupplierConfirmation, item.Status);
    }

    [Fact]
    public async Task SameEventTwice_ChangesNothing()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Created(version: 1)));
        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Paid(version: 2)));
        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Paid(version: 2))); // gửi lại

        Assert.Equal(1, await h.Context.RentalOrderFacts.CountAsync());
        Assert.Equal(1, await h.Context.RentalItemFacts.CountAsync());
        RentalOrderFact order = await h.Context.RentalOrderFacts.SingleAsync();
        Assert.Equal(PaidAt, order.PaidAt);
        Assert.Equal(OrderFactStatus.Paid, order.Status);
    }

    [Fact]
    public async Task EventWithoutVersion_FillsTimestamp_ButCannotOverrideVersionedStatus()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Paid(version: 2)));
        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(Completed(version: null))); // event lịch sử

        RentalOrderFact order = await h.Context.RentalOrderFacts.SingleAsync();
        Assert.Equal(CompletedAt, order.CompletedAt);
        Assert.Equal(OrderFactStatus.Paid, order.Status);
        Assert.Equal(2, order.LastVersion);
    }

    [Fact]
    public async Task SameVersionEvents_FurtherStatusWins()
    {
        using ProjectionHarness h = new();

        // Cùng một transaction bên Rental: cùng orderVersion = 3
        await h.ApplyOkAsync(() => h.Items.ApplyAsync(new RentalItemConfirmedPayload
        {
            RentalOrderId = OrderId, RentalItemId = ItemId, SupplierId = SupplierId,
            DecisionAt = PaidAt, OrderVersion = 3
        }));
        await h.ApplyOkAsync(() => h.Items.ApplyAsync(new RentalItemCancelledPayload
        {
            RentalOrderId = OrderId, RentalItemId = ItemId,
            CancelledAt = PaidAt, Reason = "HANDOVER_NOT_DELIVERED", OrderVersion = 3
        }));

        RentalItemFact item = await h.Context.RentalItemFacts.SingleAsync();
        Assert.Equal(ItemFactStatus.Cancelled, item.Status); // kết thúc thắng khi cùng version
        Assert.NotNull(item.ConfirmedAt);
        Assert.NotNull(item.CancelledAt);
    }

    [Fact]
    public async Task VietnamOffset_IsStoredAsUtc()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Orders.ApplyAsync(new RentalOrderPaidPayload
        {
            RentalOrderId = OrderId,
            PaidAt = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.FromHours(7)),
            OrderVersion = 2
        }));

        RentalOrderFact order = await h.Context.RentalOrderFacts.SingleAsync();
        Assert.Equal(TimeSpan.Zero, order.PaidAt!.Value.Offset);
        Assert.Equal(PaidAt, order.PaidAt);
    }

    [Fact]
    public async Task CreatedWithFractionalItemAmount_FailsAndWritesNothing()
    {
        using ProjectionHarness h = new();
        RentalOrderCreatedPayload created = Created(version: 1) with
        {
            Items = [Item(ItemId, rentalAmount: 900_000m), Item(Guid.NewGuid(), rentalAmount: 500_000.5m)]
        };

        Result result = await h.ApplyAsync(() => h.Orders.ApplyAsync(created));

        Assert.True(result.IsFailure);
        Assert.Equal(VndAmount.InvalidErrorCode, result.Error.Code);
        Assert.Equal(0, await h.Context.RentalOrderFacts.CountAsync()); // không ghi một nửa
        Assert.Equal(0, await h.Context.RentalItemFacts.CountAsync());
    }

    [Fact]
    public async Task CreatedWithForeignCurrency_Fails()
    {
        using ProjectionHarness h = new();

        Result result = await h.ApplyAsync(() => h.Orders.ApplyAsync(Created(version: 1) with { Currency = "USD" }));

        Assert.True(result.IsFailure);
        Assert.Equal(ProjectionErrors.CurrencyInvalidCode, result.Error.Code);
    }

    [Fact]
    public async Task ItemCompleted_UsesEnvelopeOccurredAt()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Items.ApplyAsync(
            new RentalItemCompletedPayload { RentalOrderId = OrderId, RentalItemId = ItemId, OrderVersion = 6 },
            occurredAt: CompletedAt));

        RentalItemFact item = await h.Context.RentalItemFacts.SingleAsync();
        Assert.Equal(CompletedAt, item.CompletedAt);
        Assert.Equal(ItemFactStatus.Completed, item.Status);
    }

    [Fact]
    public async Task HandoverCompleted_FillsHandoverAtAndQuantity()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Items.ApplyAsync(new RentalItemHandoverCompletedPayload
        {
            RentalOrderId = OrderId, RentalItemId = ItemId, SupplierId = SupplierId,
            Quantity = 3, ActualHandoverAt = CompletedAt, OrderVersion = 4
        }));

        RentalItemFact item = await h.Context.RentalItemFacts.SingleAsync();
        Assert.Equal(CompletedAt, item.HandoverAt);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(ItemFactStatus.InRental, item.Status);
    }

    private static RentalOrderCreatedPayload Created(long? version) => new()
    {
        RentalOrderId = OrderId,
        OrderCode = "RNT-20261110-0001",
        RenterId = Guid.Parse("b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e"),
        Currency = "VND",
        TotalRentalAmount = 900_000m,
        TotalDepositAmount = 3_000_000m,
        CreatedAt = CreatedAt,
        Items = [Item(ItemId, rentalAmount: 900_000m)],
        OrderVersion = version
    };

    private static RentalOrderCreatedItemPayload Item(Guid itemId, decimal rentalAmount) => new()
    {
        RentalItemId = itemId,
        EquipmentListingId = Guid.Parse("d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90"),
        SupplierId = SupplierId,
        Quantity = 3,
        ExpectedStartDate = new DateOnly(2026, 11, 12),
        ExpectedEndDate = new DateOnly(2026, 11, 15),
        RentalAmount = rentalAmount,
        DepositAmount = 3_000_000m,
        EquipmentTypeName = "Máy đầm cóc"
    };

    private static RentalOrderPaidPayload Paid(long? version) =>
        new() { RentalOrderId = OrderId, PaidAt = PaidAt, OrderVersion = version };

    private static RentalOrderCompletedPayload Completed(long? version) =>
        new() { RentalOrderId = OrderId, CompletedAt = CompletedAt, OrderVersion = version };
}
