namespace Dprental.InventoryService.Application.Recommendation;

public interface IRecommendationMetrics
{
    void RecordRequest(string tier, string reason);
    void RecordLatency(double latencyMs, string outcome);
}
