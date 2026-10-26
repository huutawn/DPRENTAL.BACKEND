using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Utils;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Kafka;

public sealed class VndAmountTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("6500000.00", 6500000L)]
    [InlineData("1500000", 1500000L)]
    public void WholeAmount_ConvertsToLong(string amount, long expected)
    {
        Result<long> result = VndAmount.ToLong(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("6500000.50")] // có phần lẻ: không làm tròn
    [InlineData("0.01")]
    [InlineData("-1000")]      // âm
    public void InvalidAmount_Fails(string amount)
    {
        Result<long> result = VndAmount.ToLong(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(result.IsFailure);
        Assert.Equal(VndAmount.InvalidErrorCode, result.Error.Code);
    }

    [Fact]
    public void AmountAboveLongRange_Fails()
    {
        Result<long> result = VndAmount.ToLong(decimal.MaxValue);

        Assert.True(result.IsFailure);
        Assert.Equal(VndAmount.InvalidErrorCode, result.Error.Code);
    }
}
