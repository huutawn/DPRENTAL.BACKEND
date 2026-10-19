namespace Dprental.RecommendationService.Application.Kafka;

public static class KafkaBackoffCalculator
{
    public const int MaxRetryDelayMs = 30000;
    public const int InitialRetryDelayMs = 1000;
    public const int MaxAttemptsPerRound = 5;

    public static TimeSpan CalculateDelay(int attemptInRound, double jitterFraction = 0.2, double? randomFraction = null)
    {
        int clampedAttempt = Math.Max(1, Math.Min(attemptInRound, MaxAttemptsPerRound));
        double baseDelay = InitialRetryDelayMs * Math.Pow(2, clampedAttempt - 1);
        double rand = randomFraction.HasValue
            ? Math.Clamp(randomFraction.Value, 0.0, 1.0)
            : Random.Shared.NextDouble();

        double jitter = rand * jitterFraction * baseDelay;
        double totalDelay = Math.Min(MaxRetryDelayMs, baseDelay + jitter);
        return TimeSpan.FromMilliseconds(totalDelay);
    }
}
