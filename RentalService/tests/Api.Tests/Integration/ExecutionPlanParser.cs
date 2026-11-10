using System.Text.Json;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed record PlanNodeMetrics(
    string Path,
    string NodeType,
    string? RelationName,
    string? IndexName,
    string? Filter,
    double? ActualRows,
    double? ActualLoops,
    long? RowsRemovedByFilter,
    long? TotalRowsRemovedDerived);

public sealed record PlanExecutionReport(
    double PlanningTimeMs,
    double ExecutionTimeMs,
    long RootSharedHitBlocks,
    long RootSharedReadBlocks,
    IReadOnlyList<PlanNodeMetrics> Nodes,
    long TotalRowsRemovedDerived,
    string RawJson);

public static class ExecutionPlanParser
{
    public static PlanExecutionReport ParseJsonPlan(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement[0];
        double planningTime = root.GetProperty("Planning Time").GetDouble();
        double executionTime = root.GetProperty("Execution Time").GetDouble();
        var plan = root.GetProperty("Plan");

        long rootHit = plan.TryGetProperty("Shared Hit Blocks", out var h) ? h.GetInt64() : 0;
        long rootRead = plan.TryGetProperty("Shared Read Blocks", out var r) ? r.GetInt64() : 0;

        var nodes = new List<PlanNodeMetrics>();
        TraversePlanNode(plan, "0", nodes);

        long totalRemovedDerived = nodes
            .Where(n => n.TotalRowsRemovedDerived.HasValue)
            .Sum(n => n.TotalRowsRemovedDerived!.Value);

        return new PlanExecutionReport(planningTime, executionTime, rootHit, rootRead, nodes, totalRemovedDerived, json);
    }

    private static void TraversePlanNode(JsonElement node, string currentPath, List<PlanNodeMetrics> nodes)
    {
        string nodeType = node.GetProperty("Node Type").GetString() ?? "";
        string? relName = node.TryGetProperty("Relation Name", out var rn) ? rn.GetString() : null;
        string? idxName = node.TryGetProperty("Index Name", out var iname) ? iname.GetString() : null;
        string? filter = node.TryGetProperty("Filter", out var f) ? f.GetString() : null;
        double? actualRows = node.TryGetProperty("Actual Rows", out var ar) ? ar.GetDouble() : null;
        double? actualLoops = node.TryGetProperty("Actual Loops", out var al) ? al.GetDouble() : null;

        long? rowsRemoved = null;
        long? totalRemovedDerived = null;
        if (node.TryGetProperty("Rows Removed by Filter", out var rrf))
        {
            rowsRemoved = rrf.GetInt64();
            double loops = actualLoops ?? 1.0;
            totalRemovedDerived = (long)Math.Round(rowsRemoved.Value * loops);
        }

        nodes.Add(new PlanNodeMetrics(currentPath, nodeType, relName, idxName, filter, actualRows, actualLoops, rowsRemoved, totalRemovedDerived));

        if (node.TryGetProperty("Plans", out var subPlans))
        {
            int index = 0;
            foreach (var subPlan in subPlans.EnumerateArray())
            {
                TraversePlanNode(subPlan, $"{currentPath}.{index}", nodes);
                index++;
            }
        }
    }
}
