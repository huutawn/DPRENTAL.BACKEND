using Dprental.AnalyticsService.Api.Tests.TestHelpers;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Dashboard.Revenue;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Dprental.AnalyticsService.Api.Tests.Integration;

/// <summary>
/// Chạy trên PostgreSQL thật (biến môi trường ANALYTICS_TEST_POSTGRES; thiếu thì tự bỏ qua):
/// migration, transaction nguyên khối, kịch bản đơn X (contract mục 8) đo bằng chính dashboard API,
/// message hỏng không chặn hàng đợi, và các câu native SQL (AGENTS §5).
/// </summary>
public sealed class AnalyticsPostgreSqlTests
{
    [Fact]
    public async Task SupplierDashboard_PaginatesDistinctCandidatesFromBothSourcesOnPostgres()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync()
            ?? throw new InvalidOperationException("Thiếu ANALYTICS_TEST_POSTGRES để kiểm thử PostgreSQL thật.");
        await using AnalyticsServiceDbContext db = database.CreateContext();
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);
        var supplierA = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var supplierB = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var supplierC = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var outside = Guid.Parse("40000000-0000-0000-0000-000000000004");
        var orderId = Guid.NewGuid();
        db.RentalOrders.Add(new RentalOrderFact { RentalOrderId = orderId, CreatedAt = start });
        foreach (var supplierId in new[] { supplierA, supplierB, supplierB })
        {
            db.RentalItems.Add(new RentalItemFact { RentalItemId = Guid.NewGuid(), RentalOrderId = orderId,
                SupplierId = supplierId, EquipmentListingId = Guid.NewGuid(), Quantity = 1 });
        }
        foreach (var entry in new[] { (supplierB, start), (supplierC, start), (outside, end) })
        {
            var id = Guid.NewGuid();
            db.PaymentFinancialEvents.Add(new PaymentFinancialEvent { MessageId = id, LogicalEventId = id.ToString(),
                AggregateId = id.ToString(), MessageType = "SupplierPayoutCompleted", SupplierId = entry.Item1,
                OccurredAt = entry.Item2, CompletedAt = entry.Item2 });
        }
        await db.SaveChangesAsync();

        var repo = new SupplierDashboardQueryRepository(db);
        var first = await repo.GetCandidatesAsync(start, end, 0, 2);
        var second = await repo.GetCandidatesAsync(start, end, 2, 2);
        var empty = await repo.GetCandidatesAsync(start, end, 4, 2);
        Assert.Equal(3, first.Total);
        Assert.Equal(3, second.Total);
        Assert.Equal(3, empty.Total);
        Assert.Equal(new[] { supplierA, supplierB }, first.SupplierIds);
        Assert.Equal(new[] { supplierC }, second.SupplierIds);
        Assert.Empty(empty.SupplierIds);
        var noMatches = await repo.GetCandidatesAsync(start.AddDays(-2), start.AddDays(-1), 0, 2);
        Assert.Equal(0, noMatches.Total);
        Assert.Empty(noMatches.SupplierIds);
    }

    private static readonly Guid OrderX = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ItemI1 = Guid.Parse("20000000-0000-0000-0000-000000000001"); // máy đầm cóc, NCC A
    private static readonly Guid ItemI2 = Guid.Parse("20000000-0000-0000-0000-000000000002"); // máy xúc mini, NCC B
    private static readonly Guid SupplierA = Guid.Parse("30000000-0000-0000-0000-00000000000a");
    private static readonly Guid SupplierB = Guid.Parse("30000000-0000-0000-0000-00000000000b");

    private static readonly DateTimeOffset Nov10 = new(2026, 11, 10, 3, 0, 0, TimeSpan.Zero); // 10:00 giờ VN
    private static readonly DateTimeOffset Nov11 = new(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov12 = new(2026, 11, 12, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov15 = new(2026, 11, 15, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov16 = new(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nov17 = new(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);
    private static readonly DashboardDateRange Range = DashboardDateRange.Create(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 17));

    [Theory]
    [InlineData(false, false, 0, null)]
    [InlineData(false, true, 200, null)]
    [InlineData(true, false, 0, 500)]
    [InlineData(true, true, 200, 300)]
    [InlineData(true, true, 500, 0)]
    [InlineData(true, true, 700, 0)]
    public async Task Overview_PendingRefundOnPostgres(
        bool hasRequest, bool hasCompletion, int completedAmount, int? expected)
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync()
            ?? throw new InvalidOperationException("Thiếu ANALYTICS_TEST_POSTGRES để kiểm thử PostgreSQL thật.");
        await using AnalyticsServiceDbContext db = database.CreateContext();

        void Add(string type, string? reference, decimal? amount, string currency = "VND") =>
            db.PaymentFinancialEvents.Add(new PaymentFinancialEvent
            {
                MessageId = Guid.NewGuid(),
                LogicalEventId = Guid.NewGuid().ToString(),
                AggregateId = OrderX.ToString(),
                MessageType = type,
                RefundRef = reference,
                Amount = amount,
                Currency = currency,
                OccurredAt = Nov10.AddYears(-1),
                RequestedAt = Nov10.AddYears(-1),
                CompletedAt = Nov10.AddYears(-1)
            });

        if (hasRequest)
        {
            Add(PaymentAnalyticsEventTypes.RefundRequestedFinancial, "partial", 500m);
        }
        if (hasCompletion)
        {
            Add(PaymentAnalyticsEventTypes.RefundCompletedFinancial, "partial", completedAmount / 2m);
            Add(PaymentAnalyticsEventTypes.RefundCompletedFinancial, "partial", completedAmount / 2m);
        }

        // Unmatched completions and invalid references/currencies must not change the balance.
        Add(PaymentAnalyticsEventTypes.RefundCompletedFinancial, "unmatched", 900m);
        Add(PaymentAnalyticsEventTypes.RefundRequestedFinancial, null, 900m);
        Add(PaymentAnalyticsEventTypes.RefundRequestedFinancial, "foreign", 900m, "USD");
        await db.SaveChangesAsync();
        var service = new DashboardQueryService(new RentalFactRepository(db), new PaymentFinancialEventRepository(db), new SupplierDashboardQueryRepository(db));
        OverviewResponse overview = await service.OverviewAsync(Range);
        Assert.Equal(expected.HasValue ? (decimal?)expected.Value : null, overview.Financial.PendingRefund);

        // Clamp each refund separately: an overpaid refund cannot cancel another pending refund.
        Add(PaymentAnalyticsEventTypes.RefundRequestedFinancial, "overpaid", 100m);
        Add(PaymentAnalyticsEventTypes.RefundCompletedFinancial, "overpaid", 1000m);
        Add(PaymentAnalyticsEventTypes.RefundRequestedFinancial, "outstanding", 250m);
        Add(PaymentAnalyticsEventTypes.RefundRequestedFinancial, "null-amount", null);
        await db.SaveChangesAsync();
        overview = await service.OverviewAsync(Range);
        Assert.Equal((expected ?? 0) + 250m, overview.Financial.PendingRefund);
    }

    [Fact]
    public async Task Migrations_CreateHardeningSchemaAndIndexes()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        await using AnalyticsServiceDbContext db = database.CreateContext();
        List<string> indexes = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'")
            .ToListAsync();
        Assert.Contains("IX_analytics_dead_letters_Topic_Partition_Offset", indexes);
        Assert.Contains("IX_analytics_rental_items_Status", indexes);
        Assert.Contains("IX_payment_financial_events_LogicalEventId", indexes);

        // Tiền tài chính là numeric(18,0): VND số nguyên
        List<string> scales = await db.Database.SqlQuery<string>($"""
            SELECT column_name || ':' || numeric_scale AS "Value" FROM information_schema.columns
            WHERE table_name = 'payment_financial_events' AND column_name IN ('PlatformFeeAmount', 'SupplierPayable', 'Amount')
            """).ToListAsync();
        Assert.All(scales, scale => Assert.EndsWith(":0", scale, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedSave_RollsBackFactsAndInboxTogether()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        using DispatcherHarness h = new(database.Options);

        // Tên loại máy dài hơn varchar(200): PostgreSQL từ chối LÚC LƯU, sau khi projector đã sửa trong bộ nhớ
        DispatchResult result = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, new
        {
            rentalOrderId = OrderX, currency = "VND", createdAt = Nov10, orderVersion = 1,
            items = new[]
            {
                new { rentalItemId = ItemI1, equipmentListingId = Guid.NewGuid(), supplierId = SupplierA, quantity = 1,
                      rentalAmount = 1m, depositAmount = 0m, equipmentTypeName = new string('X', 201) }
            }
        }));

        Assert.Equal(DispatchOutcome.RetryLater, result.Outcome);
        await using AnalyticsServiceDbContext db = database.CreateContext();
        Assert.Equal(0, await db.RentalOrders.CountAsync());          // facts không lưu...
        Assert.Equal(0, await db.RentalItems.CountAsync());
        Assert.Equal(0, await db.ProcessedRentalEvents.CountAsync()); // ...và inbox cũng không: cùng một transaction
    }

    [Fact]
    public async Task OrderXScenario_DashboardMatchesContract_EvenWhenEveryMessageIsReplayed()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        using DispatcherHarness h = new(database.Options);
        List<KafkaRecord> messages = OrderXMessages(h);
        foreach (KafkaRecord message in messages)
        {
            Assert.Equal(DispatchOutcome.Processed, (await h.DispatchAsync(message)).Outcome);
        }

        await AssertOrderXDashboard(database);

        // Kafka giao lại toàn bộ (cùng messageId), và Payment phát lại với messageId MỚI
        foreach (KafkaRecord message in messages.Concat(OrderXPaymentMessages(h, freshMessageIds: true)))
        {
            Assert.Equal(DispatchOutcome.Duplicate, (await h.DispatchAsync(message)).Outcome);
        }

        await AssertOrderXDashboard(database); // AC: replay không đổi tổng
    }

    [Fact]
    public async Task PoisonMessage_IsDeadLettered_AndLaterMessagesStillProcessed()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        using DispatcherHarness h = new(database.Options);
        DispatchResult poison = await h.DispatchAsync(h.Raw(h.Kafka.RentalTopic, "{ broken"));
        DispatchResult next = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid,
            new { rentalOrderId = OrderX, paidAt = Nov10, orderVersion = 2 }));

        Assert.Equal(DispatchOutcome.DeadLettered, poison.Outcome);
        Assert.Equal(DispatchOutcome.Processed, next.Outcome);
        await using AnalyticsServiceDbContext db = database.CreateContext();
        Assert.Equal(1, await db.DeadLetters.CountAsync());
        Assert.Equal(1, await db.RentalOrders.CountAsync());
    }

    [Fact]
    public async Task NativeSql_GroupsByVietnamDay()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        using DispatcherHarness h = new(database.Options);
        DateTimeOffset lateUtc = new(2026, 11, 15, 17, 30, 0, TimeSpan.Zero); // 00:30 ngày 16/11 giờ VN
        await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, Settled(lateUtc)));
        await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, new
        {
            rentalOrderId = OrderX, currency = "VND", createdAt = lateUtc, orderVersion = 1, items = Array.Empty<object>()
        }));

        await using AnalyticsServiceDbContext db = database.CreateContext();
        RevenueAnalyticsResponse revenue = await new RevenueAnalyticsQueryService(new PaymentFinancialEventRepository(db))
            .GetAsync(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 16), RevenueGranularity.Day);
        Assert.Equal(0m, revenue.Buckets.Single(b => b.PeriodStart == new DateOnly(2026, 11, 15)).PlatformRevenue);
        Assert.Equal(90_000m, revenue.Buckets.Single(b => b.PeriodStart == new DateOnly(2026, 11, 16)).PlatformRevenue);

        var queryDashboard = new DashboardQueryService(new RentalFactRepository(db), new PaymentFinancialEventRepository(db), new SupplierDashboardQueryRepository(db));
        OrdersResponse trend = await new OrderTrendQueryService(new RentalFactRepository(db), queryDashboard)
            .GetAsync(DashboardDateRange.Create(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 16)), CancellationToken.None);
        Assert.Equal(1, trend.Points.Single(p => p.Date == new DateOnly(2026, 11, 16)).Created);
        Assert.Equal(0, trend.Points.Single(p => p.Date == new DateOnly(2026, 11, 15)).Created);
    }

    private static async Task AssertOrderXDashboard(PostgresTestDatabase database)
    {
        await using AnalyticsServiceDbContext db = database.CreateContext();
        DashboardQueryService dashboard = new(new RentalFactRepository(db), new PaymentFinancialEventRepository(db), new SupplierDashboardQueryRepository(db));
        OverviewResponse overview = await dashboard.OverviewAsync(Range, null, CancellationToken.None);
        Assert.Equal(new OrderMetrics(TotalOrders: 1, PaidOrders: 1, ActiveOrders: 0, CompletedOrders: 1,
            CancelledOrders: 0, ExpiredOrders: 0, RejectedItems: 1), overview.Orders);
        Assert.Equal(new FinancialMetrics(
            GrossRentalValue: 6_900_000m,      // tiền thuê đã trả, không gồm cọc
            CompletedRentalValue: 900_000m,     // chỉ item đã hoàn thành
            DepositCollected: 23_000_000m,
            PlatformRevenue: 90_000m,           // lấy nguyên từ Payment
            DeductionAmount: 0m,
            RefundAmount: 26_000_000m,
            SupplierPayable: 810_000m,
            PayoutCompleted: 810_000m,
            PendingRefund: null), overview.Financial);

        // REST call cho NCC A và NCC B: cô lập số liệu và phân quyền đúng
        OverviewResponse overviewA = await dashboard.OverviewAsync(Range, SupplierA, CancellationToken.None);
        Assert.Equal(900_000m, overviewA.Financial.GrossRentalValue);
        Assert.Equal(3_000_000m, overviewA.Financial.DepositCollected);
        Assert.Equal(810_000m, overviewA.Financial.PayoutCompleted);
        Assert.Equal(1, overviewA.Rental.PaidItems);

        OverviewResponse overviewB = await dashboard.OverviewAsync(Range, SupplierB, CancellationToken.None);
        Assert.Equal(6_000_000m, overviewB.Financial.GrossRentalValue);
        Assert.Equal(20_000_000m, overviewB.Financial.DepositCollected);
        Assert.Equal(0m, overviewB.Financial.PayoutCompleted);
        Assert.Equal(1, overviewB.Rental.PaidItems);

        SuppliersResponse suppliers = await dashboard.SuppliersAsync(Range, 1, 20, CancellationToken.None);
        SupplierMetrics a = suppliers.Items.Single(s => s.SupplierId == SupplierA);
        SupplierMetrics b = suppliers.Items.Single(s => s.SupplierId == SupplierB);
        Assert.Equal((1, 1, 900_000m, 810_000m, 810_000m), (a.Accepted, a.CompletedRentals, a.RentalValue, a.SupplierPayable, a.PayoutCompleted));
        Assert.Equal(1, b.Rejected);

        EquipmentResponse equipment = await dashboard.EquipmentAsync(Range, 10, CancellationToken.None);
        Assert.Equal("Máy đầm cóc", equipment.TopEquipmentTypes[0].Name); // hòa 1 lượt → xét số máy đã giao (3 > 0)
    }

    [Fact]
    public async Task PaymentBeforeRentalSnapshot_ThenSnapshot_AndRefundOnPostgres()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            throw new InvalidOperationException("Thiếu biến môi trường ANALYTICS_TEST_POSTGRES để chạy kiểm thử trên PostgreSQL thật.");
        }

        using DispatcherHarness h = new(database.Options);

        // 1. Payment arrives BEFORE rental snapshot on real PostgreSQL
        var invoice = new
        {
            invoiceId = Guid.NewGuid(), invoiceRef = "INV-REAL-1", rentalOrderId = OrderX, renterId = Guid.NewGuid(),
            rentalAmount = 6_900_000m, depositAmount = 23_000_000m, totalAmount = 29_900_000m, currency = "VND", paidAt = Nov10
        };
        DispatchResult payRes = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.InvoicePaidFinancial, invoice));
        Assert.Equal(DispatchOutcome.Processed, payRes.Outcome);
        Assert.Empty(h.LastTouchedSuppliers); // Snapshot not present yet -> admin only

        // 2. Rental snapshot arrives later with multi-suppliers
        List<KafkaRecord> messages = OrderXMessages(h);
        DispatchResult createRes = await h.DispatchAsync(messages[0]);
        Assert.Equal(DispatchOutcome.Processed, createRes.Outcome);
        Assert.Contains(SupplierA, h.LastTouchedSuppliers);
        Assert.Contains(SupplierB, h.LastTouchedSuppliers);

        // 3. Refund for item of Supplier B
        DispatchResult refundRes = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, new
        {
            refundId = Guid.NewGuid(), refundRef = "RF-REAL-B", rentalOrderId = OrderX, rentalItemId = ItemI2,
            renterId = Guid.NewGuid(), refundKind = "SupplierRejection", amount = 26_000_000m, currency = "VND", completedAt = Nov11
        }));
        Assert.Equal(DispatchOutcome.Processed, refundRes.Outcome);
        Assert.Equal([SupplierB], h.LastTouchedSuppliers);

        // 4. Terminal order completion notifies both suppliers
        DispatchResult orderCompRes = await h.DispatchAsync(h.Rental(RentalAnalyticsEventTypes.RentalOrderCompleted, new
        {
            rentalOrderId = OrderX, completedAt = Nov15, orderVersion = 10
        }));
        Assert.Equal(DispatchOutcome.Processed, orderCompRes.Outcome);
        Assert.Contains(SupplierA, h.LastTouchedSuppliers);
        Assert.Contains(SupplierB, h.LastTouchedSuppliers);
    }

    private static List<KafkaRecord> OrderXMessages(DispatcherHarness h)
    {
        static Guid Id(int n) => Guid.Parse($"90000000-0000-0000-0000-{n:D12}");

        return
        [
            h.Rental(RentalAnalyticsEventTypes.RentalOrderCreated, new
            {
                rentalOrderId = OrderX, currency = "VND", createdAt = Nov10.AddMinutes(-5), orderVersion = 1,
                items = new object[]
                {
                    new { rentalItemId = ItemI1, equipmentListingId = Guid.NewGuid(), supplierId = SupplierA, quantity = 3,
                          rentalAmount = 900_000m, depositAmount = 3_000_000m, equipmentTypeId = Guid.NewGuid(), equipmentTypeName = "Máy đầm cóc" },
                    new { rentalItemId = ItemI2, equipmentListingId = Guid.NewGuid(), supplierId = SupplierB, quantity = 1,
                          rentalAmount = 6_000_000m, depositAmount = 20_000_000m, equipmentTypeId = Guid.NewGuid(), equipmentTypeName = "Máy xúc mini" }
                }
            }, Id(1)),
            h.Rental(RentalAnalyticsEventTypes.RentalOrderPaid, new { rentalOrderId = OrderX, paidAt = Nov10, orderVersion = 2 }, Id(2)),
            h.Rental(RentalAnalyticsEventTypes.RentalItemRejected, new
            {
                rentalOrderId = OrderX, rentalItemId = ItemI2, supplierId = SupplierB, decisionAt = Nov11, reason = "SUPPLIER_REJECTED", orderVersion = 3
            }, Id(3)),
            h.Rental(RentalAnalyticsEventTypes.RentalItemConfirmed, new
            {
                rentalOrderId = OrderX, rentalItemId = ItemI1, supplierId = SupplierA, decisionAt = Nov11, orderVersion = 4
            }, Id(4)),
            h.Rental(RentalAnalyticsEventTypes.RentalItemHandoverCompleted, new
            {
                rentalOrderId = OrderX, rentalItemId = ItemI1, supplierId = SupplierA, quantity = 3, actualHandoverAt = Nov12, orderVersion = 5
            }, Id(5)),
            h.Rental(RentalAnalyticsEventTypes.RentalItemCompleted,
                new { rentalOrderId = OrderX, rentalItemId = ItemI1, orderVersion = 6 }, Id(6), occurredAt: Nov15),
            h.Rental(RentalAnalyticsEventTypes.RentalOrderCompleted, new { rentalOrderId = OrderX, completedAt = Nov15, orderVersion = 6 }, Id(7)),
            .. OrderXPaymentMessages(h, freshMessageIds: false)
        ];
    }

    private static List<KafkaRecord> OrderXPaymentMessages(DispatcherHarness h, bool freshMessageIds)
    {
        Guid? Id(int n) => freshMessageIds ? Guid.NewGuid() : Guid.Parse($"91000000-0000-0000-0000-{n:D12}");

        return
        [
            h.Payment(PaymentAnalyticsEventTypes.InvoicePaidFinancial, new
            {
                invoiceId = Guid.Parse("70000000-0000-0000-0000-000000000001"), invoiceRef = "INV-X",
                rentalOrderId = OrderX, renterId = Guid.NewGuid(),
                rentalAmount = 6_900_000m, depositAmount = 23_000_000m, totalAmount = 29_900_000m,
                currency = "VND", paidAt = Nov10
            }, Id(4)),
            h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, new
            {
                refundId = Guid.Parse("40000000-0000-0000-0000-000000000001"), refundRef = "RF-X-I2", rentalOrderId = OrderX,
                rentalItemId = ItemI2, renterId = Guid.NewGuid(), refundKind = "SupplierRejection",
                amount = 26_000_000m, currency = "VND", completedAt = Nov11
            }, Id(1)),
            h.Payment(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, Settled(Nov16), Id(2)),
            h.Payment(PaymentAnalyticsEventTypes.SupplierPayoutCompleted, new
            {
                payoutId = Guid.Parse("50000000-0000-0000-0000-000000000001"), payoutRef = "PO-X-I1",
                settlementId = Guid.Parse("60000000-0000-0000-0000-000000000001"), rentalItemId = ItemI1, supplierId = SupplierA,
                amount = 810_000m, currency = "VND", completedAt = Nov17
            }, Id(3))
        ];
    }
    private static object Settled(DateTimeOffset completedAt) => new
    {
        settlementId = Guid.Parse("60000000-0000-0000-0000-000000000001"), settlementRef = "ST-X-I1",
        rentalOrderId = OrderX, rentalItemId = ItemI1, supplierId = SupplierA,
        rentalAmount = 900_000m, depositAmount = 3_000_000m, finalDeductionAmount = 0m, depositRefundDue = 3_000_000m,
        platformFeeRate = 0.1m, platformFeeAmount = 90_000m, supplierPayable = 810_000m, completedAt
    };
}
