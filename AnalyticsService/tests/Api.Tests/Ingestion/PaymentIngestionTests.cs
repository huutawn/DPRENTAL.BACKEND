using Dprental.AnalyticsService.Api.Tests.TestHelpers;
using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Ingestion;

/// <summary>payment.analytics.v1 qua dispatcher thật: proof, chống trùng, xung đột, dead letter.</summary>
public sealed class PaymentIngestionTests
{
    private static readonly Guid OrderId = Guid.Parse("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d");
    private static readonly Guid ItemId = Guid.Parse("c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f");
    private static readonly Guid SupplierId = Guid.Parse("e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f90a1");
    private static readonly DateTimeOffset CompletedAt = new(2026, 11, 16, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidSettlement_IsRecorded_AndReportsSupplier()
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, Settled(fee: 90_000m)));

        Assert.Equal(DispatchOutcome.Processed, result.Outcome);
        Assert.Equal([SupplierId], h.LastTouchedSuppliers);
        await using AnalyticsServiceDbContext db = h.Read();
        PaymentFinancialEvent row = await db.PaymentFinancialEvents.SingleAsync();
        Assert.Equal(90_000m, row.PlatformFeeAmount);
        Assert.Equal(810_000m, row.SupplierPayable);
        Assert.Equal($"{PaymentAnalyticsEventTypes.RentalItemFinancialSettled}:ST-1", row.LogicalEventId);
    }

    [Fact]
    public async Task FinancialEvent_ReplayedWithNewMessageId_IsCountedOnce()
    {
        using DispatcherHarness h = new();

        await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m)));
        DispatchResult replay = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m)));

        Assert.Equal(DispatchOutcome.Duplicate, replay.Outcome);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Single(await db.PaymentFinancialEvents.ToListAsync());
    }

    [Fact]
    public async Task SameMessageIdDeliveredTwice_IsDuplicate()
    {
        using DispatcherHarness h = new();
        Guid messageId = Guid.NewGuid();

        await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m), messageId));
        DispatchResult second = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m), messageId));

        Assert.Equal(DispatchOutcome.Duplicate, second.Outcome);
    }

    [Fact]
    public async Task SameBusinessKeyWithDifferentAmount_IsConflict_AndKeepsOriginal()
    {
        using DispatcherHarness h = new();

        await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, Settled(fee: 90_000m)));
        DispatchResult conflict = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, Settled(fee: 95_000m)));

        Assert.Equal(DispatchOutcome.DeadLettered, conflict.Outcome);
        Assert.Equal(ProjectionErrors.FactConflictCode, conflict.ErrorCode);
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(90_000m, (await db.PaymentFinancialEvents.SingleAsync()).PlatformFeeAmount);
    }

    [Fact]
    public async Task MissingProof_IsDeadLettered()
    {
        using DispatcherHarness h = new();
        string value = DispatcherHarness.Envelope(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m));

        DispatchResult result = await h.DispatchAsync(h.Raw(h.Kafka.PaymentTopic, value, key: "k", proof: null));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(PaymentProofVerifier.ProofInvalidCode, result.ErrorCode);
    }

    [Fact]
    public async Task ProofForAnotherMessage_IsDeadLettered()
    {
        using DispatcherHarness h = new();
        string original = DispatcherHarness.Envelope(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m));
        string tampered = DispatcherHarness.Envelope(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(999_999m));
        string proofOfOriginal = h.Signer.Sign(h.Kafka.PaymentTopic, "k", original);

        DispatchResult result = await h.DispatchAsync(h.Raw(h.Kafka.PaymentTopic, tampered, key: "k", proof: proofOfOriginal));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(PaymentProofVerifier.ProofInvalidCode, result.ErrorCode);
    }

    [Fact]
    public async Task ProofForAnotherAudience_IsDeadLettered()
    {
        using DispatcherHarness h = new();
        string value = DispatcherHarness.Envelope(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m));

        DispatchResult result = await h.DispatchAsync(h.Raw(h.Kafka.PaymentTopic, value, "k",
            h.Signer.Sign(h.Kafka.PaymentTopic, "k", value, audience: "rental-service")));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
    }

    [Fact]
    public async Task UnknownProofKey_IsRetried_NotDeadLettered()
    {
        using DispatcherHarness h = new();
        string value = DispatcherHarness.Envelope(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m));

        DispatchResult result = await h.DispatchAsync(h.Raw(h.Kafka.PaymentTopic, value, "k",
            h.Signer.Sign(h.Kafka.PaymentTopic, "k", value, keyId: "rotated-key-2")));

        Assert.Equal(DispatchOutcome.RetryLater, result.Outcome); // thiếu cấu hình key ≠ message hỏng
        await using AnalyticsServiceDbContext db = h.Read();
        Assert.Equal(0, await db.DeadLetters.CountAsync());
    }

    [Fact]
    public async Task UnknownPaymentType_WithValidProof_IsSkipped()
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Payment("InvoiceCreatedFinancial", new { anything = 1 }));

        Assert.Equal(DispatchOutcome.Skipped, result.Outcome); // bản cũ ném lỗi → kẹt partition
    }

    [Fact]
    public async Task FractionalAmount_IsDeadLettered()
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000.5m)));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(VndAmount.InvalidErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task ForeignCurrency_IsDeadLettered()
    {
        using DispatcherHarness h = new();

        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RefundCompletedFinancial, Refund(1000m, currency: "USD")));

        Assert.Equal(ProjectionErrors.CurrencyInvalidCode, result.ErrorCode);
    }

    [Fact]
    public async Task MissingRequiredField_IsDeadLettered_InsteadOfSilentZero()
    {
        using DispatcherHarness h = new();

        // Thiếu platformFeeAmount: bản cũ (record vị trí) đọc thành 0 rồi cộng vào doanh thu
        DispatchResult result = await h.DispatchAsync(h.Payment(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, new
        {
            settlementId = Guid.NewGuid(), settlementRef = "ST-1", rentalOrderId = OrderId, rentalItemId = ItemId,
            supplierId = SupplierId, rentalAmount = 900_000m, depositAmount = 3_000_000m, finalDeductionAmount = 0m,
            depositRefundDue = 3_000_000m, platformFeeRate = 0.1m, supplierPayable = 810_000m, completedAt = CompletedAt
        }));

        Assert.Equal(DispatchOutcome.DeadLettered, result.Outcome);
        Assert.Equal(KafkaMessageReader.SchemaInvalidErrorCode, result.ErrorCode);
    }

    private static object Settled(decimal fee) => new
    {
        settlementId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        settlementRef = "ST-1",
        rentalOrderId = OrderId,
        rentalItemId = ItemId,
        supplierId = SupplierId,
        rentalAmount = 900_000m,
        depositAmount = 3_000_000m,
        finalDeductionAmount = 0m,
        depositRefundDue = 3_000_000m,
        platformFeeRate = 0.1m,
        platformFeeAmount = fee,
        supplierPayable = 810_000m,
        completedAt = CompletedAt
    };

    private static object Refund(decimal amount, string currency = "VND") => new
    {
        refundId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        refundRef = "REF-1",
        rentalOrderId = OrderId,
        rentalItemId = (Guid?)null,
        renterId = Guid.NewGuid(),
        refundKind = "CancellationRefund",
        amount,
        currency,
        completedAt = CompletedAt
    };
}
