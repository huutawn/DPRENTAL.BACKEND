using Dprental.RecommendationService.Application.Kafka;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Application;

public sealed class KafkaBackoffCalculatorTests
{
    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 16000)]
    public void CalculateDelay_ZeroJitter_MatchesExponentialBase(int attempt, double expectedMs)
    {
        var delay = KafkaBackoffCalculator.CalculateDelay(attempt, jitterFraction: 0.2, randomFraction: 0.0);
        Assert.Equal(expectedMs, delay.TotalMilliseconds);
    }

    [Theory]
    [InlineData(1, 1200)]
    [InlineData(2, 2400)]
    [InlineData(3, 4800)]
    [InlineData(4, 9600)]
    [InlineData(5, 19200)]
    public void CalculateDelay_MaxJitter_WithinExpectedBounds(int attempt, double expectedMs)
    {
        var delay = KafkaBackoffCalculator.CalculateDelay(attempt, jitterFraction: 0.2, randomFraction: 1.0);
        Assert.Equal(expectedMs, delay.TotalMilliseconds);
        Assert.True(delay.TotalMilliseconds <= 30000);
    }

    [Fact]
    public void CalculateDelay_NeverExceeds30SecondsCeiling()
    {
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            var delay = KafkaBackoffCalculator.CalculateDelay(attempt, jitterFraction: 0.5, randomFraction: 1.0);
            Assert.True(delay.TotalMilliseconds <= 30000, $"Delay {delay.TotalMilliseconds}ms exceeded 30000ms ceiling for attempt {attempt}.");
        }
    }
}
