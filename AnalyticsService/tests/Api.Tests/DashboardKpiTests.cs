using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests;

public sealed class DashboardKpiTests
{
    [Fact]
    public async Task Overview_UsesPaidRentalAndSettlementFactsWithoutCountingDepositAsRevenue()
    {
        await using var db = CreateDb();
        Seed(db);
        var range = DashboardDateRange.Create(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 26));
        var service = new DashboardQueryService(new RentalFactRepository(db), new PaymentFinancialEventRepository(db), new SupplierDashboardQueryRepository(db));

        OverviewResponse overview = await service.OverviewAsync(range, null, CancellationToken.None);

        Assert.Equal(3, overview.Orders.TotalOrders);
        Assert.Equal(3, overview.Orders.PaidOrders);
        Assert.Equal(1, overview.Orders.ActiveOrders); // As of the end of the selected Vietnam day.
        Assert.Equal(1, overview.Orders.RejectedItems);
        Assert.Equal(3500, overview.Financial.GrossRentalValue);
        Assert.Equal(500, overview.Financial.DepositCollected);
        Assert.Equal(100, overview.Financial.PlatformRevenue);
        Assert.Equal(200, overview.Financial.RefundAmount);
        Assert.Equal(900, overview.Financial.SupplierPayable);
        Assert.Equal(900, overview.Financial.PayoutCompleted);
        Assert.Equal(300, overview.Financial.PendingRefund);
    }

    [Fact]
    public async Task EquipmentAndSupplierStats_UsePaidCohortAndCountCompletedItems()
    {
        await using var db = CreateDb();
        Guid supplierId = Seed(db);
        var range = DashboardDateRange.Create(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 26));
        var service = new DashboardQueryService(new RentalFactRepository(db), new PaymentFinancialEventRepository(db), new SupplierDashboardQueryRepository(db));
        EquipmentResponse equipment = await service.EquipmentAsync(range, 10, CancellationToken.None);
        SuppliersResponse suppliers = await service.SuppliersAsync(range, 1, 10, CancellationToken.None);

        Assert.Single(equipment.TopEquipmentTypes);
        Assert.Equal(1, equipment.TopEquipmentTypes.Sum(x => x.CompletedCount));
        Assert.Equal(3, equipment.TopEquipmentTypes.Sum(x => x.RentalItemCount));
        SupplierMetrics supplier = Assert.Single(suppliers.Items);
        Assert.Equal(supplierId, supplier.SupplierId);
        Assert.Equal(2, supplier.TotalRequests); // Three items across two orders; one supplier request per order.
        Assert.Equal(1, supplier.Accepted);
        Assert.Equal(1, supplier.Rejected);
        Assert.Equal(1, supplier.CompletedRentals);
        Assert.Equal(3500, supplier.RentalValue);
    }

    private static Guid Seed(AnalyticsServiceDbContext db)
    {
        Guid supplierId = Guid.NewGuid();
        Guid equipmentTypeId = Guid.NewGuid();
        Guid firstOrderId = Guid.NewGuid();
        Guid secondOrderId = Guid.NewGuid();
        Guid rejectedOrderId = Guid.NewGuid();
        DateTimeOffset At(int hour) => new(2026, 10, 26, hour, 0, 0, TimeSpan.FromHours(7));

        db.RentalOrders.AddRange(
            new RentalOrderFact
            {
                RentalOrderId = firstOrderId, CreatedAt = At(8), PaidAt = At(9),
                CompletedAt = At(18), Status = "COMPLETED"
            },
            new RentalOrderFact
            {
                RentalOrderId = secondOrderId, CreatedAt = At(8), PaidAt = At(10),
                CompletedAt = At(10).AddDays(2), Status = "COMPLETED"
            },
            new RentalOrderFact
            {
                RentalOrderId = rejectedOrderId, CreatedAt = At(8), PaidAt = At(9),
                RejectedAt = At(15), Status = "REJECTED"
            });
        db.RentalItems.AddRange(
            new RentalItemFact
            {
                RentalItemId = Guid.NewGuid(), RentalOrderId = firstOrderId,
                SupplierId = supplierId, EquipmentListingId = Guid.NewGuid(),
                EquipmentTypeId = equipmentTypeId, Quantity = 1, RentalAmount = 1000,
                DepositAmount = 200, ConfirmedAt = At(11), HandoverAt = At(12),
                CompletedAt = At(17)
            },
            new RentalItemFact
            {
                RentalItemId = Guid.NewGuid(), RentalOrderId = secondOrderId,
                SupplierId = supplierId, EquipmentListingId = Guid.NewGuid(),
                EquipmentTypeId = equipmentTypeId, Quantity = 2, RentalAmount = 2000,
                DepositAmount = 300, RejectedAt = At(13), RejectionReason = "SUPPLIER_REJECTED"
            },
            new RentalItemFact
            {
                RentalItemId = Guid.NewGuid(), RentalOrderId = firstOrderId,
                SupplierId = supplierId, EquipmentListingId = Guid.NewGuid(),
                EquipmentTypeId = equipmentTypeId, Quantity = 1, RentalAmount = 500,
                DepositAmount = 0
            });
        db.PaymentFinancialEvents.AddRange(
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(), MessageType = PaymentAnalyticsEventTypes.InvoicePaidFinancial,
                LogicalEventId = "invoice-paid:1", AggregateId = firstOrderId.ToString(),
                RentalOrderId = firstOrderId, RentalAmount = 1500, DepositAmount = 200, TotalAmount = 1700,
                PaidAt = At(9), Currency = "VND"
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(), MessageType = PaymentAnalyticsEventTypes.InvoicePaidFinancial,
                LogicalEventId = "invoice-paid:2", AggregateId = secondOrderId.ToString(),
                RentalOrderId = secondOrderId, RentalAmount = 2000, DepositAmount = 300, TotalAmount = 2300,
                PaidAt = At(10), Currency = "VND"
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(), MessageType = PaymentAnalyticsEventTypes.InvoicePaidFinancial,
                LogicalEventId = "invoice-paid:3", AggregateId = rejectedOrderId.ToString(),
                RentalOrderId = rejectedOrderId, RentalAmount = 0, DepositAmount = 0, TotalAmount = 0,
                PaidAt = At(9), Currency = "VND"
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(), MessageType = PaymentAnalyticsEventTypes.RentalItemFinancialSettled,
                LogicalEventId = Guid.NewGuid().ToString(), AggregateId = firstOrderId.ToString(),
                SupplierId = supplierId, CompletedAt = At(18), PlatformFeeAmount = 100,
                SupplierPayable = 900, DepositRefundDue = 200, Currency = "VND"
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(), MessageType = PaymentAnalyticsEventTypes.RefundRequestedFinancial,
                LogicalEventId = "refund-requested:REFUND-1", AggregateId = firstOrderId.ToString(),
                RefundRef = "REFUND-1", Amount = 500, RequestedAt = At(16), Currency = "VND"
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(), MessageType = PaymentAnalyticsEventTypes.RefundCompletedFinancial,
                LogicalEventId = Guid.NewGuid().ToString(), AggregateId = firstOrderId.ToString(),
                RefundRef = "REFUND-1", CompletedAt = At(19), Amount = 200, Currency = "VND"
            },
            new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(), MessageType = PaymentAnalyticsEventTypes.SupplierPayoutCompleted,
                LogicalEventId = Guid.NewGuid().ToString(), AggregateId = firstOrderId.ToString(),
                SupplierId = supplierId, CompletedAt = At(20), Amount = 900, Currency = "VND"
            });
        db.SaveChanges();
        return supplierId;
    }

    private static AnalyticsServiceDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
