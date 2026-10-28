using System.Security.Claims;
using Dprental.AnalyticsService.Api.Controllers;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Dashboard.Revenue;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests;

public sealed class DashboardSupplierTests
{
    private static readonly Guid SupplierA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SupplierB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly DateTimeOffset Day1 = new(2026, 10, 10, 10, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Day2 = new(2026, 10, 11, 10, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Day3 = new(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public async Task MultiSupplierOrder_IsolatesRentalAmountAndItemsPerSupplier()
    {
        await using var db = CreateDb();
        SeedMultiSupplierData(db);

        var rentalRepo = new RentalFactRepository(db);
        var paymentRepo = new PaymentFinancialEventRepository(db);
        var service = new DashboardQueryService(rentalRepo, paymentRepo);

        var range = DashboardDateRange.Create(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));

        // 1. Admin view: full platform
        OverviewResponse adminOverview = await service.OverviewAsync(range, null, CancellationToken.None);
        Assert.Equal(1, adminOverview.Orders.TotalOrders);
        Assert.Equal(1, adminOverview.Orders.PaidOrders);
        Assert.Equal(2, adminOverview.Rental.PaidItems);
        Assert.Equal(6_900_000m, adminOverview.Financial.GrossRentalValue);
        Assert.Equal(23_000_000m, adminOverview.Financial.DepositCollected);
        Assert.Equal(90_000m, adminOverview.Financial.PlatformRevenue);
        Assert.Equal(810_000m, adminOverview.Financial.PayoutCompleted);

        // 2. Supplier A view: ONLY Item 1
        OverviewResponse supplierA = await service.OverviewAsync(range, SupplierA, CancellationToken.None);
        Assert.Equal(1, supplierA.Orders.TotalOrders);
        Assert.Equal(1, supplierA.Orders.PaidOrders);
        Assert.Equal(1, supplierA.Rental.PaidItems); // 1 item of Supplier A
        Assert.Equal(900_000m, supplierA.Financial.GrossRentalValue); // NOT 6,900,000!
        Assert.Equal(3_000_000m, supplierA.Financial.DepositCollected); // NOT 23,000,000!
        Assert.Equal(90_000m, supplierA.Financial.PlatformRevenue);
        Assert.Equal(810_000m, supplierA.Financial.PayoutCompleted);
        Assert.Null(supplierA.Financial.PendingRefund);

        // 3. Supplier B view: ONLY Item 2
        OverviewResponse supplierB = await service.OverviewAsync(range, SupplierB, CancellationToken.None);
        Assert.Equal(1, supplierB.Orders.TotalOrders);
        Assert.Equal(1, supplierB.Orders.PaidOrders);
        Assert.Equal(1, supplierB.Rental.PaidItems); // 1 item of Supplier B
        Assert.Equal(6_000_000m, supplierB.Financial.GrossRentalValue); // NOT 6,900,000!
        Assert.Equal(20_000_000m, supplierB.Financial.DepositCollected); // NOT 23,000,000!
        Assert.Equal(0m, supplierB.Financial.PlatformRevenue);
        Assert.Equal(0m, supplierB.Financial.PayoutCompleted);
    }

    [Fact]
    public async Task SupplierRevenue_SeparatesPaymentAndPayoutDays()
    {
        await using var db = CreateDb();
        SeedMultiSupplierData(db);

        var paymentRepo = new PaymentFinancialEventRepository(db);
        var service = new RevenueAnalyticsQueryService(paymentRepo);

        // Supplier A: Paid on Day 1 (900k), Payout on Day 3 (810k)
        RevenueAnalyticsResponse responseA = await service.GetAsync(
            new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12), RevenueGranularity.Day, SupplierA, CancellationToken.None);

        Assert.Equal(900_000m, responseA.TotalGrossRentalValue);
        Assert.Equal(810_000m, responseA.TotalSupplierPayout);

        var bucketDay1 = responseA.Buckets.Single(b => b.PeriodStart == new DateOnly(2026, 10, 10));
        Assert.Equal(900_000m, bucketDay1.GrossRentalValue);
        Assert.Equal(0m, bucketDay1.SupplierPayout);

        var bucketDay3 = responseA.Buckets.Single(b => b.PeriodStart == new DateOnly(2026, 10, 12));
        Assert.Equal(0m, bucketDay3.GrossRentalValue);
        Assert.Equal(810_000m, bucketDay3.SupplierPayout);
    }

    [Fact]
    public async Task Controller_MeOverview_DefaultsTo30DaysWhenNull()
    {
        await using var db = CreateDb();
        SeedMultiSupplierData(db);

        var rentalRepo = new RentalFactRepository(db);
        var paymentRepo = new PaymentFinancialEventRepository(db);
        var dashboard = new DashboardQueryService(rentalRepo, paymentRepo);
        var trend = new OrderTrendQueryService(rentalRepo, dashboard);
        var revenue = new RevenueAnalyticsQueryService(paymentRepo);

        var controller = new AnalyticsDashboardController(dashboard, trend, revenue)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("sub", SupplierA.ToString()),
                        new Claim("r", "supplier")
                    ], "test"))
                }
            }
        };

        var result = await controller.MyOverview(null, null, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var overview = Assert.IsType<OverviewResponse>(ok.Value);

        // 30 days default: to - from == 29 days
        Assert.Equal(29, overview.To.DayNumber - overview.From.DayNumber);
    }

    [Fact]
    public async Task Controller_MeRevenue_ReturnsSupplierFilteredRevenue()
    {
        await using var db = CreateDb();
        SeedMultiSupplierData(db);

        var rentalRepo = new RentalFactRepository(db);
        var paymentRepo = new PaymentFinancialEventRepository(db);
        var dashboard = new DashboardQueryService(rentalRepo, paymentRepo);
        var trend = new OrderTrendQueryService(rentalRepo, dashboard);
        var revenue = new RevenueAnalyticsQueryService(paymentRepo);

        var controller = new AnalyticsDashboardController(dashboard, trend, revenue)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("sub", SupplierA.ToString()),
                        new Claim("r", "supplier")
                    ], "test"))
                }
            }
        };

        var result = await controller.MyRevenue(
            new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12), "day", CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rev = Assert.IsType<RevenueAnalyticsResponse>(ok.Value);

        Assert.Equal(900_000m, rev.TotalGrossRentalValue);
        Assert.Equal(810_000m, rev.TotalSupplierPayout);
    }

    private static void SeedMultiSupplierData(AnalyticsServiceDbContext db)
    {
        Guid orderId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid item1 = Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid item2 = Guid.Parse("20000000-0000-0000-0000-000000000002");

        db.RentalOrders.Add(new RentalOrderFact
        {
            RentalOrderId = orderId,
            CreatedAt = Day1.AddMinutes(-10),
            PaidAt = Day1,
            CompletedAt = Day2,
            Status = "COMPLETED"
        });

        db.RentalItems.AddRange(
            new RentalItemFact
            {
                RentalItemId = item1,
                RentalOrderId = orderId,
                SupplierId = SupplierA,
                EquipmentListingId = Guid.NewGuid(),
                Quantity = 3,
                RentalAmount = 900_000m,
                DepositAmount = 3_000_000m,
                ConfirmedAt = Day1.AddHours(1),
                HandoverAt = Day1.AddHours(2),
                CompletedAt = Day2,
                Status = "COMPLETED"
            },
            new RentalItemFact
            {
                RentalItemId = item2,
                RentalOrderId = orderId,
                SupplierId = SupplierB,
                EquipmentListingId = Guid.NewGuid(),
                Quantity = 1,
                RentalAmount = 6_000_000m,
                DepositAmount = 20_000_000m,
                ConfirmedAt = Day1.AddHours(1),
                Status = "PENDING_HANDOVER"
            });

        db.PaymentFinancialEvents.AddRange(
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(),
                MessageType = PaymentAnalyticsEventTypes.InvoicePaidFinancial,
                LogicalEventId = "inv:1",
                AggregateId = orderId.ToString(),
                RentalOrderId = orderId,
                RentalAmount = 6_900_000m,
                DepositAmount = 23_000_000m,
                TotalAmount = 29_900_000m,
                Currency = "VND",
                PaidAt = Day1
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(),
                MessageType = PaymentAnalyticsEventTypes.RentalItemFinancialSettled,
                LogicalEventId = "settled:1",
                AggregateId = orderId.ToString(),
                RentalOrderId = orderId,
                RentalItemId = item1,
                SupplierId = SupplierA,
                RentalAmount = 900_000m,
                DepositAmount = 3_000_000m,
                PlatformFeeAmount = 90_000m,
                SupplierPayable = 810_000m,
                FinalDeductionAmount = 0m,
                DepositRefundDue = 3_000_000m,
                Currency = "VND",
                CompletedAt = Day2
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(),
                MessageType = PaymentAnalyticsEventTypes.SupplierPayoutCompleted,
                LogicalEventId = "payout:1",
                AggregateId = orderId.ToString(),
                RentalItemId = item1,
                SupplierId = SupplierA,
                Amount = 810_000m,
                Currency = "VND",
                CompletedAt = Day3
            });

        db.SaveChanges();
    }

    private static AnalyticsServiceDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
