using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Payment;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Projections;

public sealed class FinancialProjectorTests
{
    private static readonly Guid OrderId = Guid.Parse("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d");
    private static readonly Guid ItemOne = Guid.Parse("c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f");
    private static readonly Guid ItemTwo = Guid.Parse("d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90");
    private static readonly DateTimeOffset CompletedAt = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SettlementSentTwiceWithSameValues_StoresOneRow()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Financial.ApplyAsync(Settled(deduction: 0m)));
        await h.ApplyOkAsync(() => h.Financial.ApplyAsync(Settled(deduction: 0m))); // Payment gửi lại, messageId mới

        FinancialSettlementFact settlement = await h.Context.FinancialSettlementFacts.SingleAsync();
        Assert.Equal(0, settlement.FinalDeductionAmount);
        Assert.Equal(3_000_000, settlement.DepositRefundDue);
    }

    [Fact]
    public async Task SettlementSameRefDifferentAmount_IsConflictAndKeepsOriginal()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Financial.ApplyAsync(Settled(deduction: 0m)));
        Result result = await h.ApplyAsync(() => h.Financial.ApplyAsync(Settled(deduction: 500_000m)));

        Assert.True(result.IsFailure);
        Assert.Equal(ProjectionErrors.FactConflictCode, result.Error.Code);
        FinancialSettlementFact settlement = await h.Context.FinancialSettlementFacts.SingleAsync();
        Assert.Equal(0, settlement.FinalDeductionAmount); // không ghi đè
    }

    [Fact]
    public async Task RefundWithTwoItems_StoresOneRowPerItemWithSeparateAmounts()
    {
        using ProjectionHarness h = new();

        await h.ApplyOkAsync(() => h.Financial.ApplyAsync(Refund(
            RefundItem(ItemOne, rental: 900_000m, deposit: 3_000_000m),
            RefundItem(ItemTwo, rental: 6_000_000m, deposit: 20_000_000m))));

        List<RefundFact> refunds = await h.Context.RefundFacts.OrderBy(r => r.RefundedRentalAmount).ToListAsync();
        Assert.Equal(2, refunds.Count);
        Assert.All(refunds, r => Assert.Equal("RF-001", r.RefundRef));
        Assert.Equal(6_000_000, refunds[1].RefundedRentalAmount);
        Assert.Equal(20_000_000, refunds[1].RefundedDepositAmount);
    }

    [Fact]
    public async Task RefundSentTwice_DoesNotDoubleTotal()
    {
        using ProjectionHarness h = new();
        RefundCompletedPayload refund = Refund(RefundItem(ItemOne, rental: 900_000m, deposit: 3_000_000m));

        await h.ApplyOkAsync(() => h.Financial.ApplyAsync(refund));
        await h.ApplyOkAsync(() => h.Financial.ApplyAsync(refund));

        Assert.Equal(900_000, await h.Context.RefundFacts.SumAsync(r => r.RefundedRentalAmount));
    }

    [Fact]
    public async Task RefundWithOneInvalidItem_WritesNothing()
    {
        using ProjectionHarness h = new();

        Result result = await h.ApplyAsync(() => h.Financial.ApplyAsync(Refund(
            RefundItem(ItemOne, rental: 900_000m, deposit: 0m),
            RefundItem(ItemTwo, rental: 100.5m, deposit: 0m))));

        Assert.True(result.IsFailure);
        Assert.Equal(0, await h.Context.RefundFacts.CountAsync());
    }

    [Fact]
    public async Task RefundWithDuplicateItem_Fails()
    {
        using ProjectionHarness h = new();

        Result result = await h.ApplyAsync(() => h.Financial.ApplyAsync(Refund(
            RefundItem(ItemOne, rental: 1m, deposit: 0m),
            RefundItem(ItemOne, rental: 1m, deposit: 0m))));

        Assert.True(result.IsFailure);
        Assert.Equal(ProjectionErrors.DuplicateItemCode, result.Error.Code);
    }

    private static RentalSettledPayload Settled(decimal deduction) => new()
    {
        RentalOrderId = OrderId,
        RentalItemId = ItemOne,
        SettlementRef = "ST-001",
        FinalDeductionAmount = deduction,
        RefundedDepositAmount = 3_000_000m,
        Currency = "VND",
        CompletedAt = CompletedAt
    };

    private static RefundCompletedPayload Refund(params RefundCompletedItemPayload[] items) => new()
    {
        RentalOrderId = OrderId,
        RefundRef = "RF-001",
        Currency = "VND",
        Items = items,
        CompletedAt = CompletedAt
    };

    private static RefundCompletedItemPayload RefundItem(Guid itemId, decimal rental, decimal deposit) => new()
    {
        RentalItemId = itemId,
        RefundedRentalAmount = rental,
        RefundedDepositAmount = deposit
    };
}
