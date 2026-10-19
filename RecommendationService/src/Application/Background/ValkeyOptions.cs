namespace Dprental.RecommendationService.Application.Background;

public sealed class ValkeyOptions
{
    public const string SectionName = "Valkey";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 60;
}
