using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public class RentalItemSettlementTests
{
    // Snapshot thật, tạo qua InvoiceItem.Create (setter private nên không new trực tiếp được).
    private static InvoiceItem Item(decimal rental, decimal deposit)
    {
        var result = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), rental, deposit);
        Assert.False(result.IsFailure);
        return result.Value;
    }

    private static RentalItemSettlement Ok(decimal rental, decimal deposit, decimal deduction)
    {
        var result = SettlementService.Calculate(Item(rental, deposit), deduction, "DEC-1", "rental-operation-key");
        Assert.False(result.IsFailure);
        return result.Value;
    }

    [Fact]
    public void Normal_return_no_deduction()
    {
        var s = Ok(1_000_000, 500_000, 0);
        Assert.Equal(0.10m, s.PlatformFeeRate);
        Assert.Equal(100_000, s.PlatformFeeAmount);
        Assert.Equal(500_000, s.DepositRefundDue);
        Assert.Equal(900_000, s.SupplierPayable);
    }

    [Fact]
    public void Damaged_partial_deduction()
    {
        var s = Ok(1_000_000, 500_000, 200_000);
        Assert.Equal(100_000, s.PlatformFeeAmount);
        Assert.Equal(300_000, s.DepositRefundDue);
        Assert.Equal(1_100_000, s.SupplierPayable);
    }

    [Fact]
    public void Full_deposit_deduction()
    {
        var s = Ok(1_000_000, 500_000, 500_000);
        Assert.Equal(0, s.DepositRefundDue);
        Assert.Equal(1_400_000, s.SupplierPayable);
    }

    [Fact]
    public void Zero_deposit()
    {
        var s = Ok(1_000_000, 0, 0);
        Assert.Equal(0, s.DepositRefundDue);
        Assert.Equal(900_000, s.SupplierPayable);
    }

    [Fact]
    public void Zero_rental_supplier_gets_only_deduction()
    {
        var s = Ok(0, 500_000, 150_000);
        Assert.Equal(0, s.PlatformFeeAmount);
        Assert.Equal(150_000, s.SupplierPayable);
        Assert.Equal(350_000, s.DepositRefundDue);
    }

    [Theory]
    [InlineData(1, 0)]       // 0.1   -> 0
    [InlineData(4, 0)]       // 0.4   -> 0
    [InlineData(5, 1)]       // 0.5   -> 1 (đúng nửa VND, làm tròn ra xa 0)
    [InlineData(14, 1)]      // 1.4   -> 1
    [InlineData(15, 2)]      // 1.5   -> 2
    [InlineData(25, 3)]      // 2.5   -> 3 (không phải banker's rounding)
    [InlineData(1_005, 101)] // 100.5 -> 101
    public void Fee_rounding_away_from_zero(decimal rental, decimal expectedFee)
    {
        var s = Ok(rental, 0, 0);
        Assert.Equal(expectedFee, s.PlatformFeeAmount);
        Assert.Equal(rental - expectedFee, s.SupplierPayable);
    }

    [Fact]
    public void Fee_is_calculated_per_item_not_on_sum()
    {
        // 15 + 15: riêng từng item = 2 + 2 = 4, còn Round(30 * 0.1) = 3
        var a = Ok(15, 0, 0);
        var b = Ok(15, 0, 0);
        Assert.Equal(4, a.PlatformFeeAmount + b.PlatformFeeAmount);
    }

    [Theory]
    [InlineData(-1, "Money.NegativeAmount")]
    [InlineData(500_001, "Payment.DeductionExceedsDeposit")]
    [InlineData(0.5, "Money.FractionalVndNotAllowed")]
    public void Invalid_deduction_is_rejected(double deduction, string expectedCode)
    {
        var result = SettlementService.Calculate(Item(1_000_000, 500_000), (decimal)deduction, "DEC-1", "rental-operation-key");
        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public void Deduction_above_vnd_limit_is_rejected()
    {
        var result = SettlementService.Calculate(
            Item(1_000_000, 500_000), VndAmountValidator.MaxVndAmount + 1, "DEC-1", "rental-operation-key");
        Assert.True(result.IsFailure);
        Assert.Equal("Money.AmountLimitExceeded", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_decision_ref_is_rejected(string? decisionRef)
    {
        var result = SettlementService.Calculate(Item(1_000, 500), 0, decisionRef!, "rental-operation-key");
        Assert.True(result.IsFailure);
        Assert.Equal("Payment.InvalidDecisionRef", result.Error.Code);
    }

    [Fact]
    public void Decision_ref_is_trimmed()
    {
        var result = SettlementService.Calculate(Item(1_000, 500), 0, "  DEC-9  ", "rental-operation-key");
        Assert.False(result.IsFailure);
        Assert.Equal("DEC-9", result.Value.DecisionRef);
    }

    [Fact]
    public void Decision_ref_longer_than_contract_limit_is_rejected()
    {
        var result = SettlementService.Calculate(Item(1_000, 500), 0, new string('d', 257), "rental-operation-key");

        Assert.True(result.IsFailure);
        Assert.Equal("Payment.DecisionRefTooLong", result.Error.Code);
    }

    [Fact]
    public void Settlement_preserves_operation_key_received_from_rental()
    {
        const string rentalKey = "4b97863c531d21644fd134e8a3c177ef";
        var result = SettlementService.Calculate(Item(1_000, 500), 0, "DEC-1", rentalKey);

        Assert.False(result.IsFailure);
        Assert.Equal(rentalKey, result.Value.OperationKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_operation_key_is_rejected(string operationKey)
    {
        var result = SettlementService.Calculate(Item(1_000, 500), 0, "DEC-1", operationKey);

        Assert.True(result.IsFailure);
        Assert.Equal("Payment.InvalidOperationKey", result.Error.Code);
    }

    [Fact]
    public void Operation_key_longer_than_contract_limit_is_rejected()
    {
        var result = SettlementService.Calculate(Item(1_000, 500), 0, "DEC-1", new string('k', 65));

        Assert.True(result.IsFailure);
        Assert.Equal("Payment.InvalidOperationKey", result.Error.Code);
    }

    [Fact]
    public void Max_rental_amount_is_supported()
    {
        // InvoiceItem giới hạn Rental + Deposit <= Max, nên rental = Max thì deposit = 0.
        var max = VndAmountValidator.MaxVndAmount;
        var s = Ok(max, 0, 0);
        Assert.Equal(Math.Round(max * 0.10m, 0, MidpointRounding.AwayFromZero), s.PlatformFeeAmount);
        Assert.Equal(max - s.PlatformFeeAmount, s.SupplierPayable);
    }

    [Fact]
    public void Max_total_with_full_deduction_stays_within_limit()
    {
        var max = VndAmountValidator.MaxVndAmount;
        var deposit = 500_000_000m;
        var s = Ok(max - deposit, deposit, deposit);
        Assert.Equal(0, s.DepositRefundDue);
        Assert.True(s.SupplierPayable <= max);
    }

    [Fact]
    public void Increasing_deposit_does_not_increase_platform_fee()
    {
        var low = Ok(1_000_000, 0, 0);
        var high = Ok(1_000_000, 50_000_000, 0);
        Assert.Equal(low.PlatformFeeAmount, high.PlatformFeeAmount);
        Assert.Equal(low.SupplierPayable, high.SupplierPayable);
        Assert.Equal(50_000_000, high.DepositRefundDue);
    }
}
