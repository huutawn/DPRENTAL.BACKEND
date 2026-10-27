using Dprental.AnalyticsService.Api.Tests.TestHelpers;
using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Application.Projections;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Ingestion;

/// <summary>rental.events.v1 qua dispatcher thật (InMemory): version, thứ tự, trùng, dead letter, retry.</summary>
public sealed class RentalIngestionTests
{
    private static readonly Guid OrderId = Guid.Parse("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d");
    private static readonly Guid ItemId = Guid.Parse("c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f");
    private static readonly Guid SupplierId = Guid.Parse("e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f90a1");
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PaidAt = new(2026, 11, 10, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EventsInOrder_FillTimestampsSnapshotAndFinalStatus()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, Created(1)));
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2)));
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderCompleted, Completed(5)));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalOrderFact order = await db.RentalOrders.SingleAsync();
        Assert.Equal((CreatedAt, PaidAt, CompletedAt), (order.CreatedAt!.Value, order.PaidAt!.Value, order.CompletedAt!.Value));
        Assert.Equal(OrderStatuses.Completed, order.Status);
        Assert.Equal(5, order.StatusVersion);

        RentalItemFact item = await db.RentalItems.SingleAsync();
        Assert.Equal(SupplierId, item.SupplierId);
        Assert.Equal(900_000m, item.RentalAmount);
        Assert.Equal("Máy đầm cóc", item.EquipmentTypeName);
        Assert.Equal(ItemStatuses.PendingSupplierConfirmation, item.Status);
    }

    [Fact]
    public async Task OldEventArrivingLate_DoesNotRollBackStatus_ButStillFillsItsTimestamp()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderCompleted, Completed(5)));
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2)));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalOrderFact order = await db.RentalOrders.SingleAsync();
        Assert.Equal(OrderStatuses.Completed, order.Status);
        Assert.Equal(5, order.StatusVersion);
        Assert.Equal(PaidAt, order.PaidAt);
    }

    [Fact]
    public async Task OlderOrderCreatedEvent_PopulatesSnapshotWithoutRevertingPaidStatus()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2)));
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, Created(1)));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalOrderFact order = await db.RentalOrders.SingleAsync();
        Assert.Equal(OrderStatuses.Paid, order.Status);
        Assert.Equal(2, order.StatusVersion);
        Assert.Equal(CreatedAt, order.CreatedAt);
        RentalItemFact item = await db.RentalItems.SingleAsync();
        Assert.Equal(ItemStatuses.PendingSupplierConfirmation, item.Status); // item vào thẳng trạng thái đã trả tiền
    }

    [Fact]
    public async Task SameMessageTwice_SecondIsDuplicate()
    {
        using DispatcherHarness h = new();
        Guid messageId = Guid.NewGuid();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2), messageId));
        DispatchResult second = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2), messageId));

        Assert.Equal(DispatchOutcome.Duplicate, second.Outcome);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(1, await db.ProcessedRentalEvents.CountAsync());
    }

    [Fact]
    public async Task EventWithoutVersion_IsUnknownNotOne_AndCannotOverrideVersionedStatus()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2)));
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderCompleted,
            new { rentalOrderId = OrderId, completedAt = CompletedAt })); // event lịch sử, không có orderVersion

        await using AnalyticsServiceDbContext db = h.Read();
        RentalOrderFact order = await db.RentalOrders.SingleAsync();
        Assert.Equal(CompletedAt, order.CompletedAt); // thời gian vẫn được điền
        Assert.Equal(OrderStatuses.Paid, order.Status); // nhưng không ghi đè trạng thái có version
    }

    [Fact]
    public async Task OnlyUnversionedEvents_StillMoveStatusForward()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, new { rentalOrderId = OrderId, paidAt = PaidAt }));
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderCompleted, new { rentalOrderId = OrderId, completedAt = CompletedAt }));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalOrderFact order = await db.RentalOrders.SingleAsync();
        Assert.Equal(OrderStatuses.Completed, order.Status);
        Assert.Equal(0, order.StatusVersion);
    }

    [Fact]
    public async Task SameVersionEvents_AreAllApplied_AndFurtherStatusWins()
    {
        using DispatcherHarness h = new();

        // Cùng một transaction bên Rental: cùng orderVersion = 3
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalItemConfirmed, new
        {
            rentalOrderId = OrderId, rentalItemId = ItemId, supplierId = SupplierId, decisionAt = PaidAt, orderVersion = 3
        }));
        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalItemCancelled, new
        {
            rentalOrderId = OrderId, rentalItemId = ItemId, cancelledAt = PaidAt, reason = "HANDOVER_NOT_DELIVERED", orderVersion = 3
        }));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalItemFact item = await db.RentalItems.SingleAsync();
        Assert.Equal(ItemStatuses.Cancelled, item.Status);
        Assert.NotNull(item.ConfirmedAt);
        Assert.NotNull(item.CancelledAt); // RentalItemCancelled trước đây bị bỏ qua
    }

    [Fact]
    public async Task VietnamOffset_IsStoredAsUtc()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid,
            new { rentalOrderId = OrderId, paidAt = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.FromHours(7)), orderVersion = 2 }));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalOrderFact order = await db.RentalOrders.SingleAsync();
        Assert.Equal(TimeSpan.Zero, order.PaidAt!.Value.Offset);
        Assert.Equal(PaidAt, order.PaidAt);
    }

    [Fact]
    public async Task RentalItemCompleted_UsesEnvelopeOccurredAt()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalItemCompleted,
            new { rentalOrderId = OrderId, rentalItemId = ItemId, orderVersion = 6 }, occurredAt: CompletedAt));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalItemFact item = await db.RentalItems.SingleAsync();
        Assert.Equal(CompletedAt, item.CompletedAt);
        Assert.Equal(ItemStatuses.Completed, item.Status);
    }

    [Fact]
    public async Task FractionalAmount_IsDeadLettered_AndNothingIsSaved()
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, Created(1, rentalAmount: 900_000.5m)));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(VndAmount.InvalidErrorCode, result.ErrorCode);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.RentalOrders.CountAsync());
        Assert.Equal(0, await db.RentalItems.CountAsync());
        Assert.Equal(0, await db.ProcessedRentalEvents.CountAsync());
        Assert.Equal(1, await db.DeadLetters.CountAsync());
    }

    [Fact]
    public async Task ForeignCurrency_IsDeadLettered()
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, Created(1, currency: "USD")));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(ProjectionErrors.CurrencyInvalidCode, result.ErrorCode);
    }

    [Theory]
    [InlineData("{ not valid json")]
    [InlineData("""{ "messageId": "660e8400-e29b-41d4-a716-446655440001", "messageType": "RentalOrderPaid", "version": 2, "occurredAt": "2026-11-10T03:00:00Z", "payload": {} }""")]
    public async Task BrokenEnvelope_IsDeadLetteredWithPositionOnly_AndDoesNotBlock(string value)
    {
        using DispatcherHarness h = new();
        KafkaRecord broken = h.Raw(h.Kafka.RentalTopic, value);

        DispatchResult result = await h.DispatchAsync(broken);
        DispatchResult next = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2)));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.True(result.ShouldCommitOffset); // không kẹt partition (bản cũ Seek lại mãi)
        Assert.Equal(DispatchOutcome.Processed, next.Outcome);
        await using AnalyticsServiceDbContext db = h.Read();
        AnalyticsDeadLetter deadLetter = await db.DeadLetters.SingleAsync();
        Assert.Equal(broken.Offset, deadLetter.Offset);
        Assert.Equal(KafkaMessageReader.SchemaInvalidErrorCode, deadLetter.ErrorCode);
    }

    [Fact]
    public async Task SamePositionDeadLetteredTwice_StoresOneRow()
    {
        using DispatcherHarness h = new();
        KafkaRecord broken = h.Raw(h.Kafka.RentalTopic, "broken");

        await h.DispatchAsync(broken);
        await h.DispatchAsync(broken);

        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(1, await db.DeadLetters.CountAsync());
    }

    [Theory]
    [InlineData("CartItemAdded")]
    [InlineData("RentalItemPaid")]
    public async Task UnhandledType_IsSkippedWithoutWritingAnything(string messageType)
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Rental(messageType, new { anything = 1 }));

        Assert.Equal(DispatchOutcome.Skipped, result.Outcome);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.ProcessedRentalEvents.CountAsync());
    }

    [Fact]
    public async Task UnknownTopic_IsSkipped()
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Raw("inventory.allocation.events.v1", "{}"));

        Assert.Equal(DispatchOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task DatabaseFailure_IsRetryLater_ThenSucceedsOnRetry()
    {
        using DispatcherHarness h = new();
        KafkaRecord record = h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, Paid(2));

        DispatchResult failed = await h.DispatchAsync(record, failOnSave: true);
        DispatchResult retried = await h.DispatchAsync(record);

        Assert.Equal(DispatchOutcome.RetryLater, failed.Outcome);
        Assert.False(failed.ShouldCommitOffset);
        Assert.Equal(DispatchOutcome.Processed, retried.Outcome);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.DeadLetters.CountAsync());
    }

    [Fact]
    public async Task ProcessedRentalEvent_ReportsTouchedSupplier()
    {
        using DispatcherHarness h = new();

        await ProcessAsync(h, h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, Created(1)));

        Assert.Equal([SupplierId], h.LastTouchedSuppliers);
    }

    private static async Task ProcessAsync(DispatcherHarness h, KafkaRecord record)
    {
        DispatchResult result = await h.DispatchAsync(record);
        Assert.True(result.Outcome == DispatchOutcome.Processed, $"{result.Outcome} {result.ErrorCode}");
    }

    private static object Created(long version, decimal rentalAmount = 900_000m, string currency = "VND") => new
    {
        rentalOrderId = OrderId,
        currency,
        createdAt = CreatedAt,
        orderVersion = version,
        items = new[]
        {
            new
            {
                rentalItemId = ItemId, equipmentListingId = Guid.NewGuid(), supplierId = SupplierId,
                quantity = 3, rentalAmount, depositAmount = 3_000_000m, equipmentTypeName = "Máy đầm cóc"
            }
        }
    };

    private static object Paid(long version) => new { rentalOrderId = OrderId, paidAt = PaidAt, orderVersion = version };

    private static object Completed(long version) => new { rentalOrderId = OrderId, completedAt = CompletedAt, orderVersion = version };
}
