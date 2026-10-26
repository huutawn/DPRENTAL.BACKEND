using System.Text.Json;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Events.Payment;
using Dprental.AnalyticsService.Application.Events.Rental;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests;

public sealed class DashboardProjectionTests
{
    [Fact]
    public async Task FinancialEvent_ReplayedWithNewMessageId_IsCountedOnce()
    {
        await using var db = CreateDb();
        var ingestor = new PaymentAnalyticsEventIngestor(db);
        Guid refundId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        object Payload() => new
        {
            refundId,
            refundRef = "REF-1",
            rentalOrderId = orderId,
            rentalItemId = (Guid?)null,
            renterId = Guid.NewGuid(),
            refundKind = "CancellationRefund",
            amount = 1000m,
            currency = "VND",
            completedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(PaymentAnalyticsIngestResult.Recorded,
            await ingestor.IngestAsync(Envelope(Guid.NewGuid(), "RefundCompletedFinancial", orderId, Payload())));
        Assert.Equal(PaymentAnalyticsIngestResult.Duplicate,
            await ingestor.IngestAsync(Envelope(Guid.NewGuid(), "RefundCompletedFinancial", orderId, Payload())));
        Assert.Single(await db.PaymentFinancialEvents.ToListAsync());
    }

    [Fact]
    public async Task RefundRequestedFinancial_ReplayedWithNewMessageId_IsCountedOnce()
    {
        await using var db = CreateDb();
        var ingestor = new PaymentAnalyticsEventIngestor(db);
        Guid orderId = Guid.NewGuid();
        object Payload() => new
        {
            refundRef = "REF-REQUEST-1",
            rentalOrderId = orderId,
            rentalItemId = (Guid?)null,
            refundKind = "CancellationRefund",
            amount = 1000m,
            currency = "VND",
            requestedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(PaymentAnalyticsIngestResult.Recorded,
            await ingestor.IngestAsync(Envelope(Guid.NewGuid(), "RefundRequestedFinancial", orderId, Payload())));
        Assert.Equal(PaymentAnalyticsIngestResult.Duplicate,
            await ingestor.IngestAsync(Envelope(Guid.NewGuid(), "RefundRequestedFinancial", orderId, Payload())));
        Assert.Single(await db.PaymentFinancialEvents.ToListAsync());
    }

    [Fact]
    public async Task OlderOrderCreatedEvent_PopulatesSnapshotWithoutRevertingPaidStatus()
    {
        await using var db = CreateDb();
        var ingestor = new RentalEventIngestor(db);
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var paidAt = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.FromHours(7));
        await ingestor.IngestAsync(Envelope(Guid.NewGuid(), "RentalOrderPaid", orderId,
            new { rentalOrderId = orderId, paidAt, orderVersion = 2L }));
        await ingestor.IngestAsync(Envelope(Guid.NewGuid(), "RentalOrderCreated", orderId,
            new
            {
                rentalOrderId = orderId,
                createdAt = paidAt.AddMinutes(-5),
                currency = "VND",
                orderVersion = 1L,
                items = new[]
                {
                    new
                    {
                        rentalItemId = itemId,
                        equipmentListingId = Guid.NewGuid(),
                        supplierId = Guid.NewGuid(),
                        quantity = 1,
                        rentalAmount = 900m,
                        depositAmount = 100m
                    }
                }
            }));

        var order = await db.RentalOrders.SingleAsync();
        Assert.Equal("PAID", order.Status);
        Assert.Equal(2, order.StatusVersion);
        Assert.Equal(paidAt.ToUniversalTime(), order.PaidAt);
        Assert.Single(await db.RentalItems.ToListAsync());
    }

    [Fact]
    public void VietnamDateRange_IncludesLateUtcTimeFromSameVietnamDay()
    {
        var range = DashboardDateRange.Create(new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 11));
        var payment = new DateTimeOffset(2026, 11, 10, 17, 30, 0, TimeSpan.Zero);
        Assert.True(payment >= range.StartUtc && payment < range.EndUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 17, 0, 0, TimeSpan.Zero), range.StartUtc);
    }

    private static AnalyticsServiceDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        return new AnalyticsServiceDbContext(options);
    }

    private static JsonElement Envelope(Guid messageId, string type, Guid orderId, object payload) =>
        JsonSerializer.SerializeToElement(new
        {
            messageId,
            messageType = type,
            version = 1,
            producer = type.EndsWith("Financial", StringComparison.Ordinal) ? "payment-service" : "rental-service",
            aggregateId = orderId.ToString("D"),
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = messageId.ToString("D"),
            causationId = (string?)null,
            payload
        });
}
