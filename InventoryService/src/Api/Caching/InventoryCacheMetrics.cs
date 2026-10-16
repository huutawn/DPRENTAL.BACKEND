using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Dprental.InventoryService.Api.Caching;

/// <summary>
/// Quản lý các metric đo lường hiệu năng, tỉ lệ hit/miss/bypass và lỗi của Valkey Read Cache.
/// Đảm bảo không gắn raw key, user ID hoặc từ khóa tìm kiếm vào metric tags.
/// </summary>
public sealed class InventoryCacheMetrics
{
    public const string MeterName = "Dprental.InventoryService.Cache";

    private readonly Meter _meter;
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _bypasses;
    private readonly Histogram<double> _latency;
    private readonly Counter<long> _errors;
    private readonly Counter<long> _generationRejections;
    private readonly Counter<long> _invalidationRetries;
    private readonly Counter<long> _invalidationLeaseLoss;
    private readonly Counter<long> _fanoutProgress;

    public InventoryCacheMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _hits = _meter.CreateCounter<long>("inventory_cache_hits_total", description: "Tổng số lượt cache hits");
        _misses = _meter.CreateCounter<long>("inventory_cache_misses_total", description: "Tổng số lượt cache misses");
        _bypasses = _meter.CreateCounter<long>("inventory_cache_bypasses_total", description: "Tổng số lượt cache bypasses");
        _latency = _meter.CreateHistogram<double>("inventory_cache_latency_ms", unit: "ms", description: "Thời gian phản hồi thao tác cache");
        _errors = _meter.CreateCounter<long>("inventory_cache_errors_total", description: "Tổng số lỗi thao tác cache");
        _generationRejections = _meter.CreateCounter<long>("inventory_cache_generation_rejections_total", description: "Tổng số lượt ghi bị từ chối do lệch generation token");
        _invalidationRetries = _meter.CreateCounter<long>("inventory_cache_invalidation_retries_total", description: "Tổng số lượt retry của cache invalidation jobs");
        _invalidationLeaseLoss = _meter.CreateCounter<long>("inventory_cache_invalidation_lease_loss_total", description: "Tổng số lần mất lease của cache invalidation jobs");
        _fanoutProgress = _meter.CreateCounter<long>("inventory_cache_invalidation_fanout_pages_total", description: "Tổng số trang fanout đã xử lý");
    }

    public void RecordHit(string scopeType, string queryType, double latencyMs)
    {
        var tags = new TagList { { "scope_type", scopeType }, { "query_type", queryType } };
        _hits.Add(1, tags);
        _latency.Record(latencyMs, tags);
    }

    public void RecordMiss(string scopeType, string queryType, double latencyMs)
    {
        var tags = new TagList { { "scope_type", scopeType }, { "query_type", queryType } };
        _misses.Add(1, tags);
        _latency.Record(latencyMs, tags);
    }

    public void RecordBypass(string scopeType, string queryType)
    {
        var tags = new TagList { { "scope_type", scopeType }, { "query_type", queryType } };
        _bypasses.Add(1, tags);
    }

    public void RecordError(string scopeType, string queryType, string operation)
    {
        var tags = new TagList { { "scope_type", scopeType }, { "query_type", queryType }, { "operation", operation } };
        _errors.Add(1, tags);
    }

    public void RecordGenerationRejection(string scopeType, string queryType)
    {
        var tags = new TagList { { "scope_type", scopeType }, { "query_type", queryType } };
        _generationRejections.Add(1, tags);
    }

    public void RecordInvalidationRetry()
    {
        _invalidationRetries.Add(1);
    }

    public void RecordInvalidationLeaseLoss()
    {
        _invalidationLeaseLoss.Add(1);
    }

    public void RecordFanoutProgress()
    {
        _fanoutProgress.Add(1);
    }
}
