using Dprental.AnalyticsService.Api.Tests.TestHelpers;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Ingestion;

/// <summary>
/// Milestone 4 tests: Repeat customer metrics, real deadline timeout metrics,
/// shipping fee invariants on invoice and settlement, and expand-and-contract safety.
/// </summary>
public sealed class Milestone4AnalyticsTests
{
    private static readonly Guid SupplierA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid Renter1 = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
    private static readonly Guid Renter2 = Guid.Parse("bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb");
    private static readonly Guid Renter3 = Guid.Parse("cccccccc-3333-3333-3333-cccccccccccc");

    private static readonly DateTimeOffset WindowStart = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 11, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RepeatCustomerCount_CalculatesRentersWithCompletedOrderInWindow_AndAtLeastTwoCompletedOrdersTotal()
    {
        using DispatcherHarness h = new();
        await using (var db = h.Read())
        {
            // Renter 1: Order 1 completed in Oct (outside window), Order 2 completed in Nov (inside window) -> REPEAT (>= 2 total, 1 in window)
            db.RentalOrders.AddRange(
                new RentalOrderFact
                {
                    RentalOrderId = Guid.NewGuid(),
                    RenterId = Renter1,
                    CreatedAt = WindowStart.AddDays(-20),
                    CompletedAt = WindowStart.AddDays(-10),
                    Status = "COMPLETED"
                },
                new RentalOrderFact
                {
                    RentalOrderId = Guid.NewGuid(),
                    RenterId = Renter1,
                    CreatedAt = WindowStart.AddDays(1),
                    CompletedAt = WindowStart.AddDays(5),
                    Status = "COMPLETED"
                });

            // Renter 2: Order 1 completed in Nov (inside window) -> ONLY 1 order total -> NOT REPEAT
            db.RentalOrders.Add(
                new RentalOrderFact
                {
                    RentalOrderId = Guid.NewGuid(),
                    RenterId = Renter2,
                    CreatedAt = WindowStart.AddDays(2),
                    CompletedAt = WindowStart.AddDays(6),
                    Status = "COMPLETED"
                });

            // Renter 3: 2 completed orders in Oct (outside window) -> NOT REPEAT for this window
            db.RentalOrders.AddRange(
                new RentalOrderFact
                {
                    RentalOrderId = Guid.NewGuid(),
                    RenterId = Renter3,
                    CreatedAt = WindowStart.AddDays(-30),
                    CompletedAt = WindowStart.AddDays(-25),
                    Status = "COMPLETED"
                },
                new RentalOrderFact
                {
                    RentalOrderId = Guid.NewGuid(),
                    RenterId = Renter3,
                    CreatedAt = WindowStart.AddDays(-20),
                    CompletedAt = WindowStart.AddDays(-15),
                    Status = "COMPLETED"
                });

            await db.SaveChangesAsync();
        }

        await using (var db = h.Read())
        {
            var repo = new RentalFactRepository(db);
            int count = await repo.GetRepeatCustomerCountAsync(WindowStart, WindowEnd);
            Assert.Equal(1, count); // Only Renter 1 qualifies
        }
    }

    [Fact]
    public async Task RepeatCustomerCount_ExposedInDashboardOverview()
    {
        using DispatcherHarness h = new();
        await using (var db = h.Read())
        {
            // Renter 1 has 2 completed orders within November
            db.RentalOrders.AddRange(
                new RentalOrderFact
                {
                    RentalOrderId = Guid.NewGuid(),
                    RenterId = Renter1,
                    CreatedAt = WindowStart.AddDays(1),
                    CompletedAt = WindowStart.AddDays(3),
                    Status = "COMPLETED"
                },
                new RentalOrderFact
                {
                    RentalOrderId = Guid.NewGuid(),
                    RenterId = Renter1,
                    CreatedAt = WindowStart.AddDays(5),
                    CompletedAt = WindowStart.AddDays(7),
                    Status = "COMPLETED"
                });

            await db.SaveChangesAsync();
        }

        await using (var db = h.Read())
        {
            var rentalRepo = new RentalFactRepository(db);
            var paymentRepo = new PaymentFinancialEventRepository(db);
            var supplierRepo = new SupplierDashboardQueryRepository(db);
            var queryService = new DashboardQueryService(rentalRepo, paymentRepo, supplierRepo);

            var range = DashboardDateRange.Create(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
            OverviewResponse overview = await queryService.OverviewAsync(range);

            Assert.Equal(1, overview.Orders.RepeatCustomers);
            Assert.Equal(2, overview.Orders.CompletedOrders);
        }
    }

    [Fact]
    public async Task IngestRentalOrderCreated_PersistsRenterIdAndShippingFees()
    {
        using DispatcherHarness h = new();
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        var payload = new
        {
            rentalOrderId = orderId,
            renterId = Renter1,
            currency = "VND",
            createdAt = WindowStart,
            totalDeliveryFee = 50_000m,
            totalReturnFee = 50_000m,
            items = new[]
            {
                new
                {
                    rentalItemId = itemId,
                    equipmentListingId = Guid.NewGuid(),
                    supplierId = SupplierA,
                    quantity = 1,
                    rentalAmount = 500_000m,
                    depositAmount = 1_000_000m,
                    deliveryFee = 50_000m,
                    returnFee = 50_000m
                }
            }
        };

        DispatchResult result = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, payload));
        Assert.Equal(DispatchOutcome.Processed, result.Outcome);

        await using AnalyticsServiceDbContext db = h.Read();
        RentalOrderFact order = await db.RentalOrders.SingleAsync(x => x.RentalOrderId == orderId);
        Assert.Equal(Renter1, order.RenterId);

        RentalItemFact item = await db.RentalItems.SingleAsync(x => x.RentalItemId == itemId);
        Assert.Equal(50_000m, item.DeliveryFee);
        Assert.Equal(50_000m, item.ReturnFee);
    }

    [Fact]
    public async Task IngestRentalSupplierConfirmationRequested_PersistsRealDeadline()
    {
        using DispatcherHarness h = new();
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset deadline = WindowStart.AddHours(18);

        var payload = new
        {
            rentalOrderId = orderId,
            rentalItemId = itemId,
            supplierId = SupplierA,
            supplierConfirmationDeadline = deadline,
            orderVersion = 1L
        };

        DispatchResult result = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalSupplierConfirmationRequested, payload));
        Assert.Equal(DispatchOutcome.Processed, result.Outcome);

        await using AnalyticsServiceDbContext db = h.Read();
        RentalItemFact item = await db.RentalItems.SingleAsync(x => x.RentalItemId == itemId);
        Assert.Equal(deadline.ToUniversalTime(), item.SupplierConfirmationDeadline);

        RentalOrderFact order = await db.RentalOrders.SingleAsync(x => x.RentalOrderId == orderId);
        Assert.Equal(deadline.ToUniversalTime(), order.SupplierConfirmationDeadline);
    }

    [Fact]
    public async Task GetSupplierRentalStats_MeasuresTimeoutsAgainstRealDeadline()
    {
        using DispatcherHarness h = new();
        DateTimeOffset deadline = WindowStart.AddHours(12);
        DateTimeOffset expiredTime = deadline.AddMinutes(5); // Expired past deadline

        await using (var db = h.Read())
        {
            db.RentalItems.Add(new RentalItemFact
            {
                RentalItemId = Guid.NewGuid(),
                RentalOrderId = Guid.NewGuid(),
                SupplierId = SupplierA,
                EquipmentListingId = Guid.NewGuid(),
                Quantity = 1,
                RentalAmount = 100_000m,
                DepositAmount = 200_000m,
                SupplierConfirmationDeadline = deadline,
                ExpiredAt = expiredTime,
                ExpiryReason = "SUPPLIER_CONFIRMATION_EXPIRED",
                Status = "EXPIRED"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = h.Read())
        {
            var repo = new RentalFactRepository(db);
            var stats = await repo.GetSupplierRentalStatsAsync([SupplierA], WindowStart, WindowEnd);

            Assert.True(stats.TryGetValue(SupplierA, out var stat));
            Assert.Equal(1, stat.Timeout);
        }
    }

    [Fact]
    public async Task InvoicePaidFinancial_WithShippingFees_ValidatesAndPersists()
    {
        using DispatcherHarness h = new();
        Guid invoiceId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();

        // 100k rental + 200k deposit + 30k delivery + 30k return = 360k total
        var payload = new
        {
            invoiceId,
            invoiceRef = "INV-SHIPPING-1",
            rentalOrderId = orderId,
            renterId = Renter1,
            rentalAmount = 100_000m,
            depositAmount = 200_000m,
            totalDeliveryFee = 30_000m,
            totalReturnFee = 30_000m,
            totalAmount = 360_000m,
            currency = "VND",
            paidAt = WindowStart
        };

        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.InvoicePaidFinancial, payload));
        Assert.Equal(DispatchOutcome.Processed, result.Outcome);

        await using AnalyticsServiceDbContext db = h.Read();
        PaymentFinancialEvent evt = await db.PaymentFinancialEvents.SingleAsync(x => x.InvoiceId == invoiceId);
        Assert.Equal(30_000m, evt.TotalDeliveryFee);
        Assert.Equal(30_000m, evt.TotalReturnFee);
        Assert.Equal(360_000m, evt.TotalAmount);
    }

    [Fact]
    public async Task InvoicePaidFinancial_WithMismatchTotal_FailsInvariantAndDeadLetters()
    {
        using DispatcherHarness h = new();
        Guid invoiceId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();

        // Formula mismatch: 100k + 200k + 30k + 30k = 360k != 350k
        var payload = new
        {
            invoiceId,
            invoiceRef = "INV-SHIPPING-BAD",
            rentalOrderId = orderId,
            renterId = Renter1,
            rentalAmount = 100_000m,
            depositAmount = 200_000m,
            totalDeliveryFee = 30_000m,
            totalReturnFee = 30_000m,
            totalAmount = 350_000m,
            currency = "VND",
            paidAt = WindowStart
        };

        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.InvoicePaidFinancial, payload));
        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(ProjectionErrors.FinancialFactsInvalidCode, result.ErrorCode);
    }

    [Fact]
    public async Task RentalItemFinancialSettled_WithEarnedShippingFees_ValidatesAndPersists()
    {
        using DispatcherHarness h = new();
        Guid settlementId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        // Rental: 1,000,000; Deposit: 500,000; PlatformFee: 100,000 (10%); FinalDeduction: 50,000; DeliveryEarned: 40,000; ReturnEarned: 40,000
        // SupplierPayable = 1,000,000 - 100,000 + 50,000 + 40,000 + 40,000 = 1,030,000
        var payload = new
        {
            settlementId,
            settlementRef = "SETTLE-SHIPPING-1",
            rentalOrderId = orderId,
            rentalItemId = itemId,
            supplierId = SupplierA,
            rentalAmount = 1_000_000m,
            depositAmount = 500_000m,
            finalDeductionAmount = 50_000m,
            depositRefundDue = 450_000m,
            platformFeeRate = 0.10m,
            platformFeeAmount = 100_000m,
            deliveryFeeEarned = 40_000m,
            returnFeeEarned = 40_000m,
            supplierPayable = 1_030_000m,
            currency = "VND",
            completedAt = WindowStart.AddDays(5)
        };

        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, payload));
        Assert.Equal(DispatchOutcome.Processed, result.Outcome);

        await using AnalyticsServiceDbContext db = h.Read();
        PaymentFinancialEvent evt = await db.PaymentFinancialEvents.SingleAsync(x => x.SettlementId == settlementId);
        Assert.Equal(40_000m, evt.DeliveryFeeEarned);
        Assert.Equal(40_000m, evt.ReturnFeeEarned);
        Assert.Equal(1_030_000m, evt.SupplierPayable);
    }

    [Fact]
    public async Task ExpandAndContract_HistoricalEventsWithoutNewFields_ProcessCleanly()
    {
        using DispatcherHarness h = new();
        Guid invoiceId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();

        // Historical invoice: totalDeliveryFee / totalReturnFee absent/null
        // 100k rental + 200k deposit = 300k total
        var payload = new
        {
            invoiceId,
            invoiceRef = "INV-HISTORICAL-1",
            rentalOrderId = orderId,
            renterId = Renter1,
            rentalAmount = 100_000m,
            depositAmount = 200_000m,
            totalAmount = 300_000m,
            currency = "VND",
            paidAt = WindowStart
        };

        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.InvoicePaidFinancial, payload));
        Assert.Equal(DispatchOutcome.Processed, result.Outcome);

        await using AnalyticsServiceDbContext db = h.Read();
        PaymentFinancialEvent evt = await db.PaymentFinancialEvents.SingleAsync(x => x.InvoiceId == invoiceId);
        Assert.Null(evt.TotalDeliveryFee);
        Assert.Null(evt.TotalReturnFee);
        Assert.Equal(300_000m, evt.TotalAmount);
    }
}
