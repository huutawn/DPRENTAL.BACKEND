using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Recommendation;

public enum RecommendationOutcome
{
    Available = 0,
    Miss = 1,
    Unavailable = 2
}

public static class RecommendationReasons
{
    public const string Available = "available";
    public const string Miss = "miss";
    public const string Filtered = "filtered";
    public const string Unconfigured = "unconfigured";
    public const string Timeout = "timeout";
    public const string Unavailable = "unavailable";
    public const string Invalid = "invalid";
}

public sealed record RecommendationSnapshot(
    IReadOnlyList<EquipmentTypeInterestScore> PersonalInterests,
    IReadOnlyList<ListingPopularityScore> GlobalPopularity,
    RecommendationOutcome Outcome,
    string Reason)
{
    public static RecommendationSnapshot Miss(string reason = RecommendationReasons.Miss) =>
        new([], [], RecommendationOutcome.Miss, reason);

    public static RecommendationSnapshot Unavailable(string reason = RecommendationReasons.Unavailable) =>
        new([], [], RecommendationOutcome.Unavailable, reason);

    public static RecommendationSnapshot Available(
        IReadOnlyList<EquipmentTypeInterestScore> personalInterests,
        IReadOnlyList<ListingPopularityScore> globalPopularity) =>
        new(personalInterests, globalPopularity, RecommendationOutcome.Available, RecommendationReasons.Available);
}
