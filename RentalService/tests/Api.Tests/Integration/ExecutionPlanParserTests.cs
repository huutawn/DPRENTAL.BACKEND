using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class ExecutionPlanParserTests
{
    [Fact]
    public void ParseJsonPlan_WithDeeplyNestedFourLevels_RecursivelyExtractsAllNodes()
    {
        // 4 levels of nested plans:
        // Level 0: ModifyTable (0)
        // Level 1: Subquery Scan (0.0)
        // Level 2: Limit (0.0.0)
        // Level 3: LockRows (0.0.0.0)
        // Level 4: Index Scan (0.0.0.0.0)
        string json = @"
[
  {
    ""Plan"": {
      ""Node Type"": ""ModifyTable"",
      ""Operation"": ""Update"",
      ""Relation Name"": ""rental_reminder_schedules"",
      ""Alias"": ""u"",
      ""Startup Cost"": 0.42,
      ""Total Cost"": 805.20,
      ""Plan Rows"": 100,
      ""Plan Width"": 250,
      ""Actual Startup Time"": 0.120,
      ""Actual Total Time"": 12.340,
      ""Actual Rows"": 100,
      ""Actual Loops"": 1,
      ""Shared Hit Blocks"": 1250,
      ""Shared Read Blocks"": 45,
      ""Plans"": [
        {
          ""Node Type"": ""Subquery Scan"",
          ""Parent Relationship"": ""Member"",
          ""Alias"": ""candidate"",
          ""Startup Cost"": 0.42,
          ""Total Cost"": 800.00,
          ""Actual Rows"": 100,
          ""Actual Loops"": 1,
          ""Shared Hit Blocks"": 500,
          ""Plans"": [
            {
              ""Node Type"": ""Limit"",
              ""Parent Relationship"": ""Subquery"",
              ""Actual Rows"": 100,
              ""Actual Loops"": 1,
              ""Plans"": [
                {
                  ""Node Type"": ""LockRows"",
                  ""Parent Relationship"": ""Outer"",
                  ""Actual Rows"": 100,
                  ""Actual Loops"": 1,
                  ""Plans"": [
                    {
                      ""Node Type"": ""Index Scan"",
                      ""Parent Relationship"": ""Outer"",
                      ""Index Name"": ""ix_rental_reminder_schedules_claim"",
                      ""Relation Name"": ""rental_reminder_schedules"",
                      ""Filter"": ""(leased_until IS NULL OR leased_until <= now())"",
                      ""Actual Rows"": 100,
                      ""Actual Loops"": 2,
                      ""Rows Removed by Filter"": 50
                    }
                  ]
                }
              ]
            }
          ]
        }
      ]
    },
    ""Planning Time"": 1.543,
    ""Execution Time"": 14.821
  }
]";

        var report = ExecutionPlanParser.ParseJsonPlan(json);

        Assert.Equal(1.543, report.PlanningTimeMs);
        Assert.Equal(14.821, report.ExecutionTimeMs);
        Assert.Equal(1250, report.RootSharedHitBlocks);
        Assert.Equal(45, report.RootSharedReadBlocks);

        // 5 nodes in total across 5 levels (0, 0.0, 0.0.0, 0.0.0.0, 0.0.0.0.0)
        Assert.Equal(5, report.Nodes.Count);
        Assert.Equal("0", report.Nodes[0].Path);
        Assert.Equal("ModifyTable", report.Nodes[0].NodeType);

        Assert.Equal("0.0.0.0.0", report.Nodes[4].Path);
        Assert.Equal("Index Scan", report.Nodes[4].NodeType);
        Assert.Equal("ix_rental_reminder_schedules_claim", report.Nodes[4].IndexName);
        Assert.Equal(2.0, report.Nodes[4].ActualLoops);
        Assert.Equal(50, report.Nodes[4].RowsRemovedByFilter);
        // Derived total = 50 * 2 loops = 100
        Assert.Equal(100, report.Nodes[4].TotalRowsRemovedDerived);
        Assert.Equal(100, report.TotalRowsRemovedDerived);
    }

    [Fact]
    public void ParseJsonPlan_DistinguishesMissingFieldFromZero()
    {
        string json = @"
[
  {
    ""Plan"": {
      ""Node Type"": ""Seq Scan"",
      ""Actual Rows"": 10,
      ""Actual Loops"": 1,
      ""Plans"": [
        {
          ""Node Type"": ""FilterNode"",
          ""Actual Rows"": 5,
          ""Actual Loops"": 1,
          ""Rows Removed by Filter"": 0
        }
      ]
    },
    ""Planning Time"": 0.5,
    ""Execution Time"": 1.0
  }
]";

        var report = ExecutionPlanParser.ParseJsonPlan(json);

        // Root node has no "Rows Removed by Filter" field
        Assert.Null(report.Nodes[0].RowsRemovedByFilter);
        Assert.Null(report.Nodes[0].TotalRowsRemovedDerived);

        // Child node has "Rows Removed by Filter": 0
        Assert.NotNull(report.Nodes[1].RowsRemovedByFilter);
        Assert.Equal(0L, report.Nodes[1].RowsRemovedByFilter);
        Assert.Equal(0L, report.Nodes[1].TotalRowsRemovedDerived);
    }
}
