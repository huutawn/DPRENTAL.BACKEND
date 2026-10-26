using Dprental.AnalyticsService.Api.Tests.Kafka;
using Dprental.AnalyticsService.Api.Tests.TestHelpers;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Application.Kafka.Contracts;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Integration;

/// <summary>
/// Chạy trên PostgreSQL thật: migration, transaction nguyên khối, và kịch bản đơn X
/// (contract analytics-kpi-v1 mục 8) kể cả khi toàn bộ message bị gửi lại.
/// Cần biến môi trường ANALYTICS_TEST_POSTGRES; thiếu thì test tự bỏ qua.
/// </summary>
public sealed class ReadModelPostgreSqlTests
{
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

    [Fact]
    public async Task Migration_CreatesReportingIndexes()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return; // Không có PostgreSQL cho test
        }

        await using AnalyticsServiceDbContext context = database.CreateContext();
        List<string> indexes = await context.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'")
            .ToListAsync();

        Assert.Contains("IX_rental_item_facts_SupplierId_PaidAt", indexes);
        Assert.Contains("IX_rental_item_facts_EquipmentTypeId_PaidAt", indexes);
        Assert.Contains("IX_rental_order_facts_Status", indexes);
        Assert.Contains("IX_financial_settlement_facts_CompletedAt", indexes);
        Assert.Contains("IX_analytics_dead_letters_Topic_Partition_Offset", indexes);
    }

    [Fact]
    public async Task FailedSave_RollsBackFactsAndInboxTogether()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        DispatcherHarness h = new(database.Options);

        // OrderCode dài hơn varchar(64): PostgreSQL từ chối LÚC LƯU, sau khi projector đã sửa facts trong bộ nhớ
        DispatchResult result = await h.DispatchAsync(h.Message(AnalyticsMessageTypes.RentalOrderCreated, new
        {
            rentalOrderId = OrderX, orderCode = new string('X', 65), renterId = Guid.NewGuid(), currency = "VND",
            totalRentalAmount = 1m, totalDepositAmount = 0m, createdAt = Nov10, items = Array.Empty<object>(), orderVersion = 1
        }));

        Assert.Equal(DispatchOutcome.RetryLater, result.Outcome);
        await using AnalyticsServiceDbContext context = database.CreateContext();
        Assert.Equal(0, await context.RentalOrderFacts.CountAsync());      // facts không được lưu...
        Assert.Equal(0, await context.AnalyticsInboxMessages.CountAsync()); // ...và inbox cũng không: cùng một transaction
    }

    [Fact]
    public async Task OrderXScenario_MatchesContractExample_EvenWhenEveryMessageIsReplayed()
    {
        await using PostgresTestDatabase? database = await PostgresTestDatabase.CreateAsync();
        if (database is null)
        {
            return;
        }

        DispatcherHarness h = new(database.Options);
        List<KafkaRecord> messages = OrderXMessages(h);

        foreach (KafkaRecord message in messages)
        {
            Assert.True((await h.DispatchAsync(message)).ShouldCommitOffset);
        }

        await AssertOrderXKpis(database);

        // Kafka giao lại toàn bộ (Rental: cùng messageId; Payment: messageId mới nhưng cùng khóa tự nhiên)
        foreach (KafkaRecord message in OrderXMessages(h, paymentReplay: true).Concat(messages))
        {
            Assert.True((await h.DispatchAsync(message)).ShouldCommitOffset);
        }

        await AssertOrderXKpis(database); // AC: replay không làm thay đổi tổng
    }

    private static async Task AssertOrderXKpis(PostgresTestDatabase database)
    {
        await using AnalyticsServiceDbContext db = database.CreateContext();
        DateTimeOffset dayStart = Nov10.AddHours(-3);  // 00:00 UTC ngày 10/11
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        // Ngày 10/11 (theo PaidAt)
        Assert.Equal(6_900_000, await db.RentalItemFacts
            .Where(i => i.PaidAt >= dayStart && i.PaidAt < dayEnd).SumAsync(i => i.RentalAmount));       // GrossRentalValue
        Assert.Equal(23_000_000, await db.RentalItemFacts
            .Where(i => i.PaidAt >= dayStart && i.PaidAt < dayEnd).SumAsync(i => i.DepositAmount));      // DepositCollected
        Assert.Equal(6_000_000, await db.RefundFacts
            .Join(db.RentalItemFacts, r => r.RentalItemId, i => i.RentalItemId, (r, i) => new { r, i })
            .Where(x => x.i.PaidAt >= dayStart && x.i.PaidAt < dayEnd)
            .SumAsync(x => x.r.RefundedRentalAmount));                                                  // GrossRentalValueRefunded
        Assert.Equal(2, await db.RentalItemFacts.CountAsync(i => i.PaidAt >= dayStart && i.PaidAt < dayEnd)); // RentalItemCount

        // Ngày 11/11
        Assert.Equal(1, await db.RentalItemFacts.CountAsync(i => i.SupplierId == SupplierA && i.ConfirmedAt != null)); // AcceptedCount(A)
        Assert.Equal(1, await db.RentalItemFacts.CountAsync(i => i.SupplierId == SupplierB && i.RejectedAt != null));  // RejectedCount(B)
        Assert.Equal(26_000_000, await db.RefundFacts.SumAsync(r => r.RefundedRentalAmount + r.RefundedDepositAmount)); // RefundAmount
        Assert.Equal(0, await db.RentalOrderFacts.CountAsync(o => o.Status == OrderFactStatus.Rejected));           // RejectedOrders

        // Ngày 12/11, 15/11, 16/11
        Assert.Equal(3, await db.RentalItemFacts.Where(i => i.HandoverAt != null).SumAsync(i => i.Quantity));    // RentedQuantity
        Assert.Equal(900_000, await db.RentalItemFacts.Where(i => i.CompletedAt != null).SumAsync(i => i.RentalAmount)); // CompletedRentalValue
        Assert.Equal(1, await db.RentalOrderFacts.CountAsync(o => o.CompletedAt != null));                       // CompletedOrders
        Assert.Equal(0, await db.FinancialSettlementFacts.SumAsync(s => s.FinalDeductionAmount));                 // DeductionAmount

        // Không có dòng thừa sau replay
        Assert.Equal(1, await db.RentalOrderFacts.CountAsync());
        Assert.Equal(2, await db.RentalItemFacts.CountAsync());
        Assert.Equal(1, await db.RefundFacts.CountAsync());
        Assert.Equal(1, await db.FinancialSettlementFacts.CountAsync());
        Assert.Equal(OrderFactStatus.Completed, (await db.RentalOrderFacts.SingleAsync()).Status);
    }

    // Kịch bản contract mục 8. Rental messageId cố định theo vị trí (như messageId tất định của Rental);
    // paymentReplay = true thì Payment gửi lại với messageId MỚI (như Guid.NewGuid() bên Payment).
    private static List<KafkaRecord> OrderXMessages(DispatcherHarness h, bool paymentReplay = false)
    {
        static Guid Id(int n) => Guid.Parse($"90000000-0000-0000-0000-{n:D12}");
        Guid? PaymentId(int n) => paymentReplay ? Guid.NewGuid() : Id(n);

        List<KafkaRecord> rental =
        [
            h.Message(AnalyticsMessageTypes.RentalOrderCreated, new
            {
                rentalOrderId = OrderX, orderCode = "RNT-20261110-0001", renterId = Guid.NewGuid(), currency = "VND",
                totalRentalAmount = 6_900_000m, totalDepositAmount = 23_000_000m, createdAt = Nov10.AddMinutes(-5),
                items = new object[]
                {
                    new { rentalItemId = ItemI1, equipmentListingId = Guid.NewGuid(), supplierId = SupplierA, quantity = 3,
                          expectedStartDate = "2026-11-12", expectedEndDate = "2026-11-15",
                          rentalAmount = 900_000m, depositAmount = 3_000_000m, equipmentTypeName = "Máy đầm cóc" },
                    new { rentalItemId = ItemI2, equipmentListingId = Guid.NewGuid(), supplierId = SupplierB, quantity = 1,
                          expectedStartDate = "2026-11-12", expectedEndDate = "2026-11-15",
                          rentalAmount = 6_000_000m, depositAmount = 20_000_000m, equipmentTypeName = "Máy xúc mini" }
                },
                orderVersion = 1
            }, Id(1)),
            h.Message(AnalyticsMessageTypes.RentalOrderPaid, new { rentalOrderId = OrderX, paidAt = Nov10, orderVersion = 2 }, Id(2)),
            h.Message(AnalyticsMessageTypes.RentalItemRejected, new
            {
                rentalOrderId = OrderX, rentalItemId = ItemI2, supplierId = SupplierB, decisionAt = Nov11,
                reason = "SUPPLIER_REJECTED", orderVersion = 3
            }, Id(3)),
            h.Message(AnalyticsMessageTypes.RentalItemConfirmed, new
            {
                rentalOrderId = OrderX, rentalItemId = ItemI1, supplierId = SupplierA, decisionAt = Nov11, orderVersion = 4
            }, Id(4)),
            h.Message(AnalyticsMessageTypes.RentalItemHandoverCompleted, new
            {
                rentalOrderId = OrderX, rentalItemId = ItemI1, supplierId = SupplierA, quantity = 3,
                actualHandoverAt = Nov12, orderVersion = 5
            }, Id(5)),
            h.Message(AnalyticsMessageTypes.RentalItemCompleted,
                new { rentalOrderId = OrderX, rentalItemId = ItemI1, orderVersion = 6 }, Id(6), occurredAt: Nov15),
            h.Message(AnalyticsMessageTypes.RentalOrderCompleted,
                new { rentalOrderId = OrderX, completedAt = Nov15, orderVersion = 6 }, Id(7))
        ];

        List<KafkaRecord> payment =
        [
            h.Message(AnalyticsMessageTypes.RefundCompleted, new
            {
                rentalOrderId = OrderX, refundRef = "RF-X-I2", currency = "VND", completedAt = Nov11,
                items = new[] { new { rentalItemId = ItemI2, refundedRentalAmount = 6_000_000m, refundedDepositAmount = 20_000_000m } }
            }, PaymentId(8), topic: "payment.events.v1"),
            h.Message(AnalyticsMessageTypes.RentalSettled, new
            {
                rentalOrderId = OrderX, rentalItemId = ItemI1, settlementRef = "ST-X-I1", finalDeductionAmount = 0m,
                refundedDepositAmount = 3_000_000m, currency = "VND", completedAt = Nov16
            }, PaymentId(9), topic: "payment.events.v1")
        ];

        return paymentReplay ? payment : [.. rental, .. payment];
    }
}
