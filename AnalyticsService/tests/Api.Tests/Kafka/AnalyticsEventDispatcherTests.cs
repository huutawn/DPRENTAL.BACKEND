using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Application.Kafka.Contracts;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Kafka;

public sealed class AnalyticsEventDispatcherTests
{
    private static readonly Guid OrderId = Guid.Parse("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d");
    private static readonly Guid ItemId = Guid.Parse("c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f");
    private static readonly DateTimeOffset PaidAt = new(2026, 11, 10, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidMessage_IsProcessed_WithFactAndInboxSavedTogether()
    {
        DispatcherHarness h = new();
        Guid messageId = Guid.NewGuid();

        DispatchResult result = await h.DispatchAsync(h.Message(AnalyticsMessageTypes.RentalOrderPaid, Paid(), messageId));

        Assert.Equal(DispatchOutcome.Processed, result.Outcome);
        Assert.True(result.ShouldCommitOffset);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(PaidAt, (await db.RentalOrderFacts.SingleAsync()).PaidAt);
        AnalyticsInboxMessage inbox = await db.AnalyticsInboxMessages.SingleAsync();
        Assert.Equal(messageId, inbox.MessageId);
        Assert.Equal("rental.events.v1", inbox.Topic);
    }

    [Fact]
    public async Task SameMessageIdTwice_SecondIsDuplicate()
    {
        DispatcherHarness h = new();
        Guid messageId = Guid.NewGuid();

        await h.DispatchAsync(h.Message(AnalyticsMessageTypes.RentalOrderPaid, Paid(), messageId));
        DispatchResult second = await h.DispatchAsync(h.Message(AnalyticsMessageTypes.RentalOrderPaid, Paid(), messageId));

        Assert.Equal(DispatchOutcome.Duplicate, second.Outcome);
        Assert.True(second.ShouldCommitOffset);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(1, await db.AnalyticsInboxMessages.CountAsync());
    }

    [Theory]
    [InlineData("InvoicePaid")]     // event Payment Analytics không dùng
    [InlineData("RentalItemPaid")]  // đã có RentalOrderPaid
    [InlineData("CartItemAdded")]
    public async Task UnhandledType_IsSkipped_WithoutWritingAnything(string messageType)
    {
        DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Message(messageType, new { anything = 1 }));

        Assert.Equal(DispatchOutcome.Skipped, result.Outcome);
        Assert.True(result.ShouldCommitOffset);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.AnalyticsInboxMessages.CountAsync()); // không ghi inbox cho event không dùng
        Assert.Equal(0, await db.AnalyticsDeadLetters.CountAsync());
    }

    [Fact]
    public async Task MalformedJson_IsDeadLettered_WithPositionOnly()
    {
        DispatcherHarness h = new();
        KafkaRecord record = h.Raw("{ not valid json");

        DispatchResult result = await h.DispatchAsync(record);

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.True(result.ShouldCommitOffset); // không kẹt hàng đợi
        await using AnalyticsServiceDbContext db = h.Read();
        AnalyticsDeadLetter deadLetter = await db.AnalyticsDeadLetters.SingleAsync();
        Assert.Equal(record.Offset, deadLetter.Offset);
        Assert.Null(deadLetter.MessageId);
        Assert.Equal(KafkaMessageReader.SchemaInvalidErrorCode, deadLetter.ErrorCode);
    }

    [Fact]
    public async Task PayloadMissingRequiredField_IsDeadLettered()
    {
        DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(
            h.Message(AnalyticsMessageTypes.RentalOrderPaid, new { paidAt = PaidAt, orderVersion = 2 }));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(AnalyticsMessageTypes.RentalOrderPaid, result.MessageType);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.RentalOrderFacts.CountAsync());
    }

    [Fact]
    public async Task ProjectorError_IsDeadLettered_AndSavesNoFactsNorInbox()
    {
        DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(
            h.Message(AnalyticsMessageTypes.RentalOrderCreated, Created(rentalAmount: 900_000.5m)));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(VndAmount.InvalidErrorCode, result.ErrorCode);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.RentalOrderFacts.CountAsync());
        Assert.Equal(0, await db.RentalItemFacts.CountAsync());
        Assert.Equal(0, await db.AnalyticsInboxMessages.CountAsync());
        Assert.Equal(1, await db.AnalyticsDeadLetters.CountAsync());
    }

    [Fact]
    public async Task SamePositionDeadLetteredTwice_StoresOneRow()
    {
        DispatcherHarness h = new();
        KafkaRecord record = h.Raw("broken");

        await h.DispatchAsync(record);
        await h.DispatchAsync(record); // worker sập rồi đọc lại cùng vị trí

        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(1, await db.AnalyticsDeadLetters.CountAsync());
    }

    [Fact]
    public async Task DatabaseFailure_IsRetryLater_AndNotDeadLettered()
    {
        DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(
            h.Message(AnalyticsMessageTypes.RentalOrderPaid, Paid()), failOnSave: true);

        Assert.Equal(DispatchOutcome.RetryLater, result.Outcome);
        Assert.False(result.ShouldCommitOffset); // không commit offset → Kafka giao lại
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.AnalyticsDeadLetters.CountAsync());
        Assert.Equal(0, await db.RentalOrderFacts.CountAsync());
    }

    [Fact]
    public async Task RetryAfterDatabaseFailure_SucceedsWithSameMessage()
    {
        DispatcherHarness h = new();
        KafkaRecord record = h.Message(AnalyticsMessageTypes.RentalOrderPaid, Paid());

        await h.DispatchAsync(record, failOnSave: true);
        DispatchResult retry = await h.DispatchAsync(record);

        Assert.Equal(DispatchOutcome.Processed, retry.Outcome);
    }

    [Fact]
    public async Task RentalItemCompleted_UsesEnvelopeOccurredAt()
    {
        DispatcherHarness h = new();
        DateTimeOffset occurredAt = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

        await h.DispatchAsync(h.Message(
            AnalyticsMessageTypes.RentalItemCompleted,
            new { rentalOrderId = OrderId, rentalItemId = ItemId, orderVersion = 6 },
            occurredAt: occurredAt));

        await using AnalyticsServiceDbContext db = h.Read();
        RentalItemFact item = await db.RentalItemFacts.SingleAsync();
        Assert.Equal(occurredAt, item.CompletedAt);
        Assert.Equal(ItemFactStatus.Completed, item.Status);
    }

    [Fact]
    public async Task ForeignCurrencySettlement_IsDeadLetteredWithCurrencyCode()
    {
        DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Message(AnalyticsMessageTypes.RentalSettled, new
        {
            rentalOrderId = OrderId, rentalItemId = ItemId, settlementRef = "ST-1",
            finalDeductionAmount = 0, refundedDepositAmount = 0, currency = "USD",
            completedAt = PaidAt
        }, topic: "payment.events.v1"));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(ProjectionErrors.CurrencyInvalidCode, result.ErrorCode);
    }

    private static object Paid() => new { rentalOrderId = OrderId, paidAt = PaidAt, orderVersion = 2 };

    private static object Created(decimal rentalAmount) => new
    {
        rentalOrderId = OrderId,
        orderCode = "RNT-1",
        renterId = Guid.NewGuid(),
        currency = "VND",
        totalRentalAmount = rentalAmount,
        totalDepositAmount = 0m,
        createdAt = PaidAt,
        items = new[]
        {
            new
            {
                rentalItemId = ItemId, equipmentListingId = Guid.NewGuid(), supplierId = Guid.NewGuid(),
                quantity = 1, expectedStartDate = "2026-11-12", expectedEndDate = "2026-11-15",
                rentalAmount, depositAmount = 0m
            }
        },
        orderVersion = 1
    };
}
