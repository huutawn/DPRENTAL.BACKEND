using Dprental.PaymentService.Common.Utils;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class VndAmountValidatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(500_000)]
    [InlineData(1_500_000)]
    [InlineData(999_999_999_999)]
    public void ValidVndAmounts_PassValidation(decimal amount)
    {
        var result = VndAmountValidator.ValidateVndAmount(amount);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-500_000)]
    public void NegativeAmounts_FailValidation(decimal amount)
    {
        var result = VndAmountValidator.ValidateVndAmount(amount);
        Assert.True(result.IsFailure);
        Assert.Equal("Money.NegativeAmount", result.Error.Code);
    }

    [Theory]
    [InlineData(100.5)]
    [InlineData(0.01)]
    [InlineData(100000.999)]
    public void FractionalAmounts_FailValidation(decimal amount)
    {
        var result = VndAmountValidator.ValidateVndAmount(amount);
        Assert.True(result.IsFailure);
        Assert.Equal("Money.FractionalVndNotAllowed", result.Error.Code);
    }

    [Fact]
    public void AmountExceedingCeiling_FailsValidation()
    {
        decimal amount = 1_000_000_000_000m;
        var result = VndAmountValidator.ValidateVndAmount(amount);
        Assert.True(result.IsFailure);
        Assert.Equal("Money.AmountLimitExceeded", result.Error.Code);
    }
}
