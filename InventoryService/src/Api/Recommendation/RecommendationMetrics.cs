using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dprental.InventoryService.Application.Recommendation;

namespace Dprental.InventoryService.Api.Recommendation;

public sealed class RecommendationMetrics : IRecommendationMetrics
{
    public const string MeterName = "Dprental.InventoryService.Recommendation";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _latency;

    public RecommendationMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _requests = _meter.CreateCounter<long>(
            "inventory_recommendation_requests_total",
            description: "Tổng số lượt request recommendation phân loại theo tier và reason");
        _latency = _meter.CreateHistogram<double>(
            "inventory_recommendation_latency_ms",
            unit: "ms",
            description: "Thời gian đọc recommendation snapshot từ Valkey");
    }

    public void RecordRequest(string tier, string reason)
    {
        var tags = new TagList
        {
            { "tier", tier },
            { "reason", reason }
        };
        _requests.Add(1, tags);
    }

    public void RecordLatency(double latencyMs, string outcome)
    {
        var tags = new TagList
        {
            { "outcome", outcome }
        };
        _latency.Record(latencyMs, tags);
    }
}
