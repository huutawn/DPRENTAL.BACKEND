using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public class ShippingAndSettlementFinancialTests
{
    [Fact]
    public void InvoiceItem_WithShippingFees_CalculatesTotalCorrectly()
    {
        var itemRes = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 1_000_000m, 5_000_000m, 150_000m, 120_000m);
        Assert.False(itemRes.IsFailure);
        var item = itemRes.Value;

        Assert.Equal(1_000_000m, item.RentalAmount);
        Assert.Equal(5_000_000m, item.DepositAmount);
        Assert.Equal(150_000m, item.DeliveryFee);
        Assert.Equal(120_000m, item.ReturnFee);
        Assert.Equal(6_270_000m, item.TotalAmount);
    }

    [Fact]
    public void Invoice_WithShippingFees_SumsItemFeesAndTotalCorrectly()
    {
        var item1 = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 1_000_000m, 2_000_000m, 100_000m, 50_000m).Value;
        var item2 = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 2_000_000m, 3_000_000m, 150_000m, 100_000m).Value;

        var now = DateTimeOffset.UtcNow;
        var invRes = Invoice.Create(
            Guid.NewGuid(), "DH-TEST-001", Guid.NewGuid(), "VND",
            now.AddHours(2), [item1, item2], "CODE123", "MB", "0123456789", now);

        Assert.False(invRes.IsFailure);
        var inv = invRes.Value;

        Assert.Equal(3_000_000m, inv.TotalRentalAmount);
        Assert.Equal(5_000_000m, inv.TotalDepositAmount);
        Assert.Equal(250_000m, inv.TotalDeliveryFee);
        Assert.Equal(150_000m, inv.TotalReturnFee);
        // Total = 3M + 5M + 250K + 150K = 8,400,000 VND
        Assert.Equal(8_400_000m, inv.TotalAmount);
    }

    [Fact]
    public void Settlement_PlatformFee_StrictlyTenPercentOnRentalOnly()
    {
        // Rental = 2,000,000, Deposit = 10,000,000, Delivery = 150,000, Return = 150,000
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 2_000_000m, 10_000_000m, 150_000m, 150_000m).Value;

        // Deduction = 500,000, DeliveryEarned = 150,000, ReturnEarned = 150,000
        var settlementRes = SettlementService.Calculate(item, 500_000m, "DEC-001", "OP-001", 150_000m, 150_000m);
        Assert.False(settlementRes.IsFailure);
        var s = settlementRes.Value;

        // 10% fee strictly on rental: Math.Round(2,000,000 * 0.10) = 200,000
        Assert.Equal(200_000m, s.PlatformFeeAmount);
        Assert.Equal(0.10m, s.PlatformFeeRate);

        // Supplier payable = Rental (2M) - Fee (200K) + Deduction (500K) + DeliveryEarned (150K) + ReturnEarned (150K)
        // = 1,800,000 + 500,000 + 300,000 = 2,600,000 VND
        Assert.Equal(2_600_000m, s.SupplierPayable);

        // Deposit refund due = 10M - 500K = 9,500,000 VND
        Assert.Equal(9_500_000m, s.DepositRefundDue);

        // Mathematical invariant check (matching ck_settlement_money_balance):
        // SupplierPayable + PlatformFeeAmount + DepositRefundDue == RentalAmount + DepositAmount + DeliveryFeeEarned + ReturnFeeEarned
        decimal lhs = s.SupplierPayable + s.PlatformFeeAmount + s.DepositRefundDue;
        decimal rhs = s.RentalAmount + s.DepositAmount + s.DeliveryFeeEarned + s.ReturnFeeEarned;
        Assert.Equal(lhs, rhs);
        Assert.Equal(12_300_000m, lhs);
    }

    [Fact]
    public void Settlement_Rejects_DeliveryFeeEarnedExceedingSnapshot()
    {
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 1_000_000m, 2_000_000m, 100_000m, 100_000m).Value;

        // Requesting 150,000 delivery fee earned when snapshot is only 100,000
        var result = SettlementService.Calculate(item, 0m, "DEC-001", "OP-001", 150_000m, 50_000m);
        Assert.True(result.IsFailure);
        Assert.Equal("Payment.DeliveryFeeEarnedExceedsItemFee", result.Error.Code);
    }

    [Fact]
    public void Settlement_Rejects_ReturnFeeEarnedExceedingSnapshot()
    {
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 1_000_000m, 2_000_000m, 100_000m, 100_000m).Value;

        // Requesting 150,000 return fee earned when snapshot is only 100,000
        var result = SettlementService.Calculate(item, 0m, "DEC-001", "OP-001", 50_000m, 150_000m);
        Assert.True(result.IsFailure);
        Assert.Equal("Payment.ReturnFeeEarnedExceedsItemFee", result.Error.Code);
    }

    [Fact]
    public void FivePart_SupplierEarningsBreakdown_CalculatesConsistently()
    {
        decimal rentalAmount = 2_000_000m;
        decimal platformFee = Math.Round(rentalAmount * 0.10m, 0, MidpointRounding.AwayFromZero);
        decimal deduction = 500_000m;
        decimal deliveryEarned = 150_000m;
        decimal returnEarned = 150_000m;
        decimal shippingEarned = deliveryEarned + returnEarned;
        decimal netPayable = rentalAmount - platformFee + deduction + shippingEarned;

        var breakdown = new SupplierEarningsBreakdownResponse(
            rentalAmount, 0.10m, platformFee, deduction,
            deliveryEarned, returnEarned, shippingEarned, netPayable);

        Assert.Equal(2_000_000m, breakdown.RentalAmount);
        Assert.Equal(200_000m, breakdown.PlatformFeeAmount);
        Assert.Equal(500_000m, breakdown.FinalDeductionAmount);
        Assert.Equal(300_000m, breakdown.ShippingFeeEarned);
        Assert.Equal(2_600_000m, breakdown.NetSupplierPayable);
    }

    [Fact]
    public void ManualRefundItem_AndRequirement_SumsShippingFeesCorrectly()
    {
        var item1 = ManualRefundItem.Create(Guid.NewGuid(), 1_000_000m, 2_000_000m, 100_000m, 50_000m);
        var item2 = ManualRefundItem.Create(Guid.NewGuid(), 500_000m, 1_000_000m, 50_000m, 50_000m);

        var req = ManualRefundRequirement.Create(
            Guid.NewGuid(), Guid.NewGuid(), "PAY-001", "OP-REF-001", "Cancellation",
            ManualRefundKind.RentalRefund, null, [item1, item2], DateTimeOffset.UtcNow,
            FinancialRefundKinds.CancellationRefund);

        // Sum = (1M + 2M + 100K + 50K) + (500K + 1M + 50K + 50K) = 3,150,000 + 1,600,000 = 4,750,000 VND
        Assert.Equal(4_750_000m, req.Amount);
    }

    [Fact]
    public void OutboxPayloads_CarriesRenterIdAndShippingFees()
    {
        var renterId = Guid.NewGuid();
        var refundPayload = new RefundRequestedFinancialPayload(
            "MRF-001", Guid.NewGuid(), Guid.NewGuid(), "CancellationRefund", 1_000_000m, "VND", DateTimeOffset.UtcNow, renterId);

        Assert.Equal(renterId, refundPayload.RenterId);

        var paidPayload = new InvoicePaidFinancialPayload(
            Guid.NewGuid(), "INV-001", Guid.NewGuid(), renterId, 2_000_000m, 5_000_000m, 7_300_000m, "VND", DateTimeOffset.UtcNow,
            TotalDeliveryFee: 150_000m, TotalReturnFee: 150_000m);

        Assert.Equal(150_000m, paidPayload.TotalDeliveryFee);
        Assert.Equal(150_000m, paidPayload.TotalReturnFee);
    }
}
